// Features/Summary/DigestWordHighlight.cs - the digest's hover highlight.
//
// When the pointer rests on a word of the generated summary, that one word
// wears a rounded dark-green plate with a little padding on all sides, painted
// by this adorner above DocBox's text: the document itself is never touched,
// so the text flow, the selection and the clipboard behavior stay exactly the
// RichTextBox's own, and the painting can never shift, resize or reflow a
// single neighbor. The resting digest reads as plain text - the plate exists
// only under the pointer, alongside the hand cursor that marks the word as
// the floating action popup's target. The plate hits nothing
// (IsHitTestVisible=false), so clicks fall through to the text. A scroll only
// translates the cached rects (they ride the scroll delta); a text or layout
// change re-walks and re-measures on the dispatcher's quiet lane, coalescing a
// streaming digest's dozens of ticks into one pass.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Threading;

    internal sealed class DigestWordHighlightAdorner : Adorner
    {
        // A digest word is the same creature the action popup isolates: bounded
        // by whitespace and punctuation, everything else keeps it whole.
        internal static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && !char.IsPunctuation(c);

        private const int MaxWords = 12000;     // a pathological document cannot loop forever

        private readonly RichTextBox _box;
        private readonly List<(TextPointer Start, TextPointer End)> _words = new();
        private readonly List<Rect> _rects = new();
        private bool _rectsDirty = true;
        private bool _rebuildQueued;
        private int _hover = -1;

        // The plate: the AI buttons' dark green under the pointer's word,
        // translucent enough for the glyphs to read through. The colors ride
        // BrushConverter so the exact values are visible in the literal heap.
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
            box.TextChanged += (_, _) => QueueRebuild();
            box.LayoutUpdated += (_, _) => _rectsDirty = true;   // fonts, sizes, re-layouts
            box.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnScroll));
            box.PreviewMouseMove += OnMouseMove;
            box.MouseLeave += (_, _) => SetHover(-1);
            QueueRebuild();
        }

        // The digest changed (or the adorner just attached): re-walk the words on
        // the dispatcher's quiet lane - a streaming digest lands dozens of
        // TextChanged ticks and this coalesces them into one walk per batch.
        private void QueueRebuild()
        {
            if (_rebuildQueued)
            {
                return;
            }

            _rebuildQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _rebuildQueued = false;
                RebuildWords();
            }));
        }

        private void RebuildWords()
        {
            _words.Clear();
            _rectsDirty = true;

            FlowDocument? doc = _box.Document;
            if (doc is null)
            {
                InvalidateVisual();
                return;
            }

            (Run Run, int Start)? carry = null;     // a word mid-flight across run edges
            foreach (Block block in doc.Blocks)
            {
                if (_words.Count >= MaxWords)
                {
                    break;
                }

                if (block is Paragraph paragraph)
                {
                    foreach (Inline inline in EnumerateInlines(paragraph))
                    {
                        if (_words.Count >= MaxWords)
                        {
                            break;
                        }

                        if (inline is Run run && !string.IsNullOrEmpty(run.Text))
                        {
                            ScanRun(run, ref carry);
                        }
                        else if (carry is { } open)
                        {
                            Emit(open, open.Run.ContentEnd);    // a LineBreak or figure splits words
                            carry = null;
                        }
                    }
                }

                if (carry is { } dangling)
                {
                    Emit(dangling, dangling.Run.ContentEnd);    // a paragraph edge splits words
                    carry = null;
                }
            }

            if (carry is { } tail)
            {
                Emit(tail, tail.Run.ContentEnd);
            }

            InvalidateVisual();
        }

        // Bold segments live inside spans; the digest uses them freely, so the
        // walk descends one level (AiMarkdown's shapes: paragraph > span > run).
        private static IEnumerable<Inline> EnumerateInlines(Paragraph paragraph)
        {
            foreach (Inline inline in paragraph.Inlines)
            {
                if (inline is Span span)
                {
                    foreach (Inline nested in span.Inlines)
                    {
                        yield return nested;
                    }
                }
                else
                {
                    yield return inline;
                }
            }
        }

        private void ScanRun(Run run, ref (Run Run, int Start)? carry)
        {
            string text = run.Text;
            int i = 0;

            // A word carried from the previous run either continues here or
            // closes at the boundary between them.
            if (carry is { } open && open.Run != run)
            {
                if (i < text.Length && IsWordChar(text[i]))
                {
                    int j = i;
                    while (j < text.Length && IsWordChar(text[j]))
                    {
                        j++;
                    }

                    if (j < text.Length)
                    {
                        Emit(open, run.ContentStart.GetPositionAtOffset(j));
                        carry = null;
                        i = j;
                    }
                    else
                    {
                        i = j;      // still open at this run's edge
                    }
                }
                else
                {
                    Emit(open, run.ContentStart);
                    carry = null;
                }
            }

            while (i < text.Length)
            {
                while (i < text.Length && !IsWordChar(text[i]))
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    return;
                }

                int start = i;
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }

                if (i < text.Length)
                {
                    Emit((run, start), run.ContentStart.GetPositionAtOffset(i));
                }
                else
                {
                    carry = (run, start);   // the word reaches the run edge: may continue
                }
            }
        }

        private void Emit((Run Run, int Start) word, TextPointer? end)
        {
            TextPointer? start = word.Run.ContentStart.GetPositionAtOffset(word.Start);
            if (start is null || end is null || start.CompareTo(end) >= 0)
            {
                return;
            }

            _words.Add((start, end));
        }

        private void OnScroll(object sender, ScrollChangedEventArgs e)
        {
            // A scroll only translates the view: slide the cached plates with the
            // content instead of re-measuring every word per scroll tick.
            if (e.HorizontalChange == 0 && e.VerticalChange == 0)
            {
                return;
            }

            for (int i = 0; i < _rects.Count; i++)
            {
                if (!_rects[i].IsEmpty)
                {
                    _rects[i] = new Rect(
                        _rects[i].X - e.HorizontalChange,
                        _rects[i].Y - e.VerticalChange,
                        _rects[i].Width,
                        _rects[i].Height);
                }
            }

            InvalidateVisual();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            EnsureRects();
            Point pt = e.GetPosition(this);
            int hit = -1;
            for (int i = 0; i < _rects.Count; i++)
            {
                if (!_rects[i].IsEmpty && _rects[i].Contains(pt))
                {
                    hit = i;
                    break;
                }
            }

            SetHover(hit);
        }

        private void SetHover(int index)
        {
            if (_hover == index)
            {
                return;
            }

            _hover = index;
            _box.Cursor = index >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }

        private void EnsureRects()
        {
            if (!_rectsDirty && _rects.Count == _words.Count)
            {
                return;
            }

            _rects.Clear();
            for (int i = 0; i < _words.Count; i++)
            {
                Rect start = _words[i].Start.GetCharacterRect(LogicalDirection.Forward);
                Rect end = _words[i].End.GetCharacterRect(LogicalDirection.Backward);
                Rect rect = start.IsEmpty ? end : end.IsEmpty ? start : Rect.Union(start, end);
                if (!rect.IsEmpty && rect.Width <= 0)
                {
                    rect = new Rect(start.X, start.Y, 1, start.Height);
                }

                _rects.Add(rect);
            }

            _rectsDirty = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_hover < 0 || _hover >= _rects.Count || _rects[_hover].IsEmpty)
            {
                return;     // the pointer is off the text: the digest stays plain
            }

            DrawPlate(dc, _rects[_hover]);
        }

        private void DrawPlate(DrawingContext dc, Rect rect)
        {
            // The padding: the plate grows a little beyond the glyphs on every
            // side - pure painting, so the words around it hold their place.
            Rect plate = Rect.Inflate(rect, 2.5, 1.5);
            if (plate.Bottom < 0 || plate.Top > ActualHeight)
            {
                return;     // outside the viewport: nothing to paint
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
