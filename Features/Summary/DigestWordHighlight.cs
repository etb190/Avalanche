// Features/Summary/DigestWordHighlight.cs - the digest's instant hover highlight.
//
// When the pointer rests on a word of the generated summary, that one word
// wears a rounded dark-green plate with padding on all sides, painted by this
// adorner above DocBox's text. The document itself is never touched, so text flow,
// selection, and clipboard behavior stay native.
//
// A clicked word is pinned: while the action popup stands, that word keeps
// the same translucent green plate the hover wears - glyphs always
// readable, never a second layer stacked on top - no matter where the
// pointer wanders, until the popup closes or a newer word or passage
// takes the anchor.
//
// Zero-cost on-demand hit-testing:
// Rather than pre-measuring thousands of words and re-checking them on every
// layout tick, hit-testing is performed on-demand in O(1) time directly under the
// mouse pointer using TextPointer. When the mouse moves within the same word,
// execution is a 0-cost no-op. When entering a new word, only that single word
// is measured. When leaving text or during drag-selection, the plate clears instantly.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;
    using System.Windows.Input;
    using System.Windows.Media;

    internal sealed class DigestWordHighlightAdorner : Adorner
    {
        // A digest word is bounded by whitespace and punctuation.
        internal static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && !char.IsPunctuation(c);

        private readonly RichTextBox _box;
        private TextPointer? _hoverStart;
        private TextPointer? _hoverEnd;
        private Rect _hoverRect = Rect.Empty;

        // The pinned word: the popup's living target. Set when the action
        // popup opens on a clicked word, it holds the plate on that word -
        // independent of where the pointer wanders - until the popup closes
        // or a newer word or passage takes the anchor.
        private TextPointer? _pinnedStart;
        private TextPointer? _pinnedEnd;

        // The plate: the AI buttons' dark green under the pointer's word (#1B5E20 on #0F3D14),
        // translucent enough for the glyphs to read through cleanly.
        private static readonly Brush PlateFill = Plate("#B31B5E20");
        private static readonly Pen PlateEdge = Edge("#E60F3D14");

        private static Brush Plate(string hex) =>
            (Brush)new BrushConverter().ConvertFromString(hex)!;

        private static Pen Edge(string hex)
        {
            var pen = new Pen(Plate(hex), 1);
            pen.Freeze();
            return pen;
        }

        public DigestWordHighlightAdorner(RichTextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;

            box.PreviewMouseMove += OnMouseMove;
            box.MouseLeave += (_, _) => ClearHover();
            box.TextChanged += (_, _) => ClearHover();
            box.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnScroll));
        }

        private void OnScroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0 || e.VerticalChange != 0)
            {
                ClearHover();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            // If the user is drag-selecting text or a selection already exists,
            // suppress the single-word hover plate so it doesn't fight selection.
            if (e.LeftButton == MouseButtonState.Pressed || !_box.Selection.IsEmpty)
            {
                ClearHover();
                return;
            }

            Point pt = e.GetPosition(_box);

            // Fast hit-test: snapToText=false ensures we only hit when the pointer
            // is actually over glyphs, not empty margins or padding.
            TextPointer? hit = _box.GetPositionFromPoint(pt, snapToText: false);
            if (hit is null || !TryIsolateWord(hit, out TextPointer? start, out TextPointer? end))
            {
                ClearHover();
                return;
            }

            // Resting on the pinned word itself: the pin already lights it -
            // drop any stale hover from the word just left, keep the hand
            // cursor, and never stack a second plate on top.
            if (_pinnedStart != null && _pinnedEnd != null &&
                _pinnedStart.CompareTo(start) == 0 && _pinnedEnd.CompareTo(end) == 0)
            {
                ClearHover();
                if (_box.Cursor != Cursors.Hand)
                {
                    _box.Cursor = Cursors.Hand;
                }

                return;
            }

            // If the pointer is still over the exact same isolated word, do nothing:
            // 0 allocations, 0 layout calls, 0 redraws.
            if (_hoverStart != null && _hoverEnd != null &&
                _hoverStart.CompareTo(start) == 0 && _hoverEnd.CompareTo(end) == 0)
            {
                return;
            }

            // Measure ONLY this one isolated word.
            Rect rStart = start!.GetCharacterRect(LogicalDirection.Forward);
            Rect rEnd = end!.GetCharacterRect(LogicalDirection.Backward);

            if (rStart.IsEmpty && rEnd.IsEmpty)
            {
                ClearHover();
                return;
            }

            Rect wordRect;
            if (Math.Abs(rStart.Top - rEnd.Top) < 4)
            {
                // Single line word
                double x = Math.Min(rStart.Left, rEnd.Left);
                double right = Math.Max(rStart.Right, rEnd.Right);
                double y = Math.Min(rStart.Top, rEnd.Top);
                double bottom = Math.Max(rStart.Bottom, rEnd.Bottom);
                wordRect = new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
            }
            else
            {
                // Wrapped across lines: take the line segment containing the pointer
                if (pt.Y <= rStart.Bottom + 2)
                {
                    wordRect = new Rect(rStart.Left, rStart.Top, Math.Max(4, _box.ActualWidth - rStart.Left), rStart.Height);
                }
                else
                {
                    wordRect = new Rect(0, rEnd.Top, Math.Max(4, rEnd.Right), rEnd.Height);
                }
            }

            // Check if the pointer is within the inflated plate boundary
            Rect plateHitBox = Rect.Inflate(wordRect, 3, 2);
            if (!plateHitBox.Contains(pt))
            {
                ClearHover();
                return;
            }

            _hoverStart = start;
            _hoverEnd = end;
            _hoverRect = wordRect;

            if (_box.Cursor != Cursors.Hand)
            {
                _box.Cursor = Cursors.Hand;
            }

            InvalidateVisual();
        }

        private void ClearHover()
        {
            if (_hoverStart is null && _hoverRect.IsEmpty)
            {
                return;
            }

            _hoverStart = null;
            _hoverEnd = null;
            _hoverRect = Rect.Empty;

            if (_box.Cursor != null)
            {
                _box.Cursor = null;
            }

            InvalidateVisual();
        }

        // Pins the plate on the popup's target word: it holds there - pointer
        // moves, hover plates, everything else notwithstanding - until the
        // popup that set it closes or a newer open replaces it.
        internal void Pin(TextPointer start, TextPointer end)
        {
            _pinnedStart = start;
            _pinnedEnd = end;
            ClearHover();       // the hover plate is redundant over the pin
            InvalidateVisual();
        }

        // Releases the pin - the popup closed, or a passage took the anchor
        // (a passage rides the native selection, no plate).
        internal void ClearPin()
        {
            if (_pinnedStart is null)
            {
                return;
            }

            _pinnedStart = null;
            _pinnedEnd = null;
            InvalidateVisual();
        }

        // Re-measures the pinned word's plate from its own pointers on every
        // paint, so a re-layout slides the plate with the word instead of
        // leaving it behind. Two GetCharacterRect calls, on paints only.
        private bool TryMeasurePin(out Rect rect)
        {
            rect = Rect.Empty;
            if (_pinnedStart is null || _pinnedEnd is null)
            {
                return false;
            }

            Rect rStart = _pinnedStart.GetCharacterRect(LogicalDirection.Forward);
            Rect rEnd = _pinnedEnd.GetCharacterRect(LogicalDirection.Backward);

            if (rStart.IsEmpty && rEnd.IsEmpty)
            {
                return false;
            }

            if (rStart.IsEmpty)
            {
                rStart = rEnd;
            }

            if (rEnd.IsEmpty)
            {
                rEnd = rStart;
            }

            if (Math.Abs(rStart.Top - rEnd.Top) < 4)
            {
                // Single line word
                double x = Math.Min(rStart.Left, rEnd.Left);
                double right = Math.Max(rStart.Right, rEnd.Right);
                double y = Math.Min(rStart.Top, rEnd.Top);
                double bottom = Math.Max(rStart.Bottom, rEnd.Bottom);
                rect = new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
            }
            else
            {
                // A word wrapped across lines: the plate holds the leading
                // line's segment, the same face the hover plate would wear.
                rect = new Rect(rStart.Left, rStart.Top,
                    Math.Max(4, _box.ActualWidth - rStart.Left), rStart.Height);
            }

            return true;
        }

        internal static bool TryIsolateWord(TextPointer hit, out TextPointer? start, out TextPointer? end)
        {
            start = null;
            end = null;

            // Check if immediately adjacent characters are word characters.
            string fwd = hit.GetTextInRun(LogicalDirection.Forward);
            string bwd = hit.GetTextInRun(LogicalDirection.Backward);

            bool fwdIsWord = fwd.Length > 0 && IsWordChar(fwd[0]);
            bool bwdIsWord = bwd.Length > 0 && IsWordChar(bwd[^1]);

            if (!fwdIsWord && !bwdIsWord)
            {
                return false;
            }

            TextPointer left = hit;
            for (int pass = 0; pass < 64; pass++)
            {
                string run = left.GetTextInRun(LogicalDirection.Backward);
                int i = run.Length;
                while (i > 0 && IsWordChar(run[i - 1]))
                {
                    i--;
                }

                if (i == run.Length)
                {
                    break;
                }

                left = left.GetPositionAtOffset(i - run.Length) ?? left;
                if (i > 0)
                {
                    break;
                }
            }

            TextPointer right = hit;
            for (int pass = 0; pass < 64; pass++)
            {
                string run = right.GetTextInRun(LogicalDirection.Forward);
                int i = 0;
                while (i < run.Length && IsWordChar(run[i]))
                {
                    i++;
                }

                if (i == 0)
                {
                    break;
                }

                right = right.GetPositionAtOffset(i) ?? right;
                if (i < run.Length)
                {
                    break;
                }
            }

            if (left.CompareTo(right) >= 0)
            {
                return false;
            }

            start = left;
            end = right;
            return true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            // The pinned word first: the popup's target keeps its plate -
            // the same translucent face as the hover, so the glyphs read
            // through - no matter where the pointer has moved.
            bool pinned = TryMeasurePin(out Rect pinnedRect);
            if (pinned)
            {
                Rect pinPlate = Rect.Inflate(pinnedRect, 2.5, 1.5);
                if (pinPlate.Bottom >= 0 && pinPlate.Top <= ActualHeight)
                {
                    dc.DrawRoundedRectangle(PlateFill, PlateEdge, pinPlate, 3, 3);
                }
            }

            if (_hoverRect.IsEmpty)
            {
                return;
            }

            // One face per word: a hover landing on the pinned word adds
            // nothing - two stacked plates would drown the glyphs.
            if (pinned && pinnedRect.IntersectsWith(_hoverRect))
            {
                return;
            }

            // Inflate plate slightly around the glyph bounds
            Rect plate = Rect.Inflate(_hoverRect, 2.5, 1.5);
            if (plate.Bottom < 0 || plate.Top > ActualHeight)
            {
                return;
            }

            dc.DrawRoundedRectangle(PlateFill, PlateEdge, plate, 3, 3);
        }

        public void Detach()
        {
            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(_box);
            layer?.Remove(this);
            _box.Cursor = null;
        }
    }
}
