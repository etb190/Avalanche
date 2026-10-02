// Shell/SidebarNotes.cs - the sidebar's NOTES tab (the 50-page chunked
// recall digest) and its tab switch. Replaces the old outlines tab: the
// reader names a page span, Generate asks the model for dense ~200-word
// review cards over 50-page blocks (Features/Notes/NotesGenerator), and the
// cards render as markdown, each with its own copy chip.

namespace Avalanche
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Threading;
    using Avalanche.Features.AI;
    using Avalanche.Features.Notes;
    using Avalanche.Services;

    public partial class MainWindow
    {
        private const double SidebarMaxNotes = 480;   // cards read best wide; the splitter can drag under it

        private readonly List<NoteCard> _noteCards = [];
        private string? _notesDocumentPath;           // the cards on screen belong to this document

        // Generated notes belong to the document that paid for them: a tab
        // flit parks the cards (and the typed range) under the old book's id
        // and brings the new book's own cards back - switching tabs never
        // burns a generation, and two books never see each other's cards.
        private sealed record NotesSnapshot(List<NoteCard> Cards, string RangeText);
        private readonly Dictionary<string, NotesSnapshot> _notesByDocument = [];
        private CancellationTokenSource? _notesCts;
        private int _notesRun;                        // bumped on cancel/document switch: stale continuations bail
        private bool _notesBusy;
        private bool _notesRangeWired;                // the range field's input gates wire once

        private void SidebarNotesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToNotesTab();

        private void SwitchSidebarToNotesTab()
        {
            EnsureNotesRangeWired();
            // Save current pages width, then restore (or seed) the notes width.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxPages));

            _sidebarShowingNotes = true;
            PageList.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Visible;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarPagesTab.Tag = null;
            SidebarNotesTab.Tag = "on";     // the segmented style paints the active face
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SbPx(SidebarMaxNotes);
            if (!_sidebarCollapsed)
            {
                double target = _savedNotesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        // ------------------------------------------------------------------
        // Document lifecycle
        // ------------------------------------------------------------------

        /// <summary>The document changed (open / tab switch / close): the old
        /// book's cards park under its id, and the new book's own cards come
        /// back if it has parked ones - a fresh book reseeds the range pair
        /// and opens empty. Same-path no-ops keep tab flits from wiping notes
        /// for nothing.</summary>
        internal void ResetNotesForDocument(string? filePath)
        {
            if (string.Equals(_notesDocumentPath, filePath, StringComparison.Ordinal))
            {
                return;
            }

            // Park the outgoing book's generation before anything moves: the
            // cards on screen are the ones the reader paid requests for.
            if (!string.IsNullOrEmpty(_notesDocumentPath) && _noteCards.Count > 0)
            {
                string oldId = Features.AI.DocumentIndexer.ComputeDocumentId(_notesDocumentPath);
                _notesByDocument[oldId] = new NotesSnapshot(
                    new List<NoteCard>(_noteCards), NotesRangeBox.Text);
            }

            _notesDocumentPath = filePath;
            CancelNotesRun();
            _notesBusy = false;
            SetNotesBusy(false);

            int pages = _doc?.PageCount ?? 0;
            if (!string.IsNullOrEmpty(filePath) &&
                _notesByDocument.TryGetValue(
                    Features.AI.DocumentIndexer.ComputeDocumentId(filePath),
                    out NotesSnapshot? saved))
            {
                // A returning book: its own cards take the stage again.
                RenderNoteCards(saved.Cards);
                NotesRangeBox.Text = saved.RangeText;
            }
            else
            {
                // A fresh book: empty stage, the pair reseeds to its full span.
                _noteCards.Clear();
                ShowNotesEmpty();
                HideNotesStatus();
                NotesRangeBox.Text = pages > 0
                    ? "1-" + pages.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }
        }

        private void CancelNotesRun()
        {
            _notesRun++;
            try { _notesCts?.Cancel(); } catch { /* cancelling twice is fine */ }
            _notesCts?.Dispose();
            _notesCts = null;
        }

        // ------------------------------------------------------------------
        // Generation
        // ------------------------------------------------------------------

        private async void NotesGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                SetStatusHeld(Loc("Str_SummaryOpenBook"));
                return;
            }

            if (_notesBusy)
            {
                return;   // one run at a time; document switches cancel silently
            }

            int pages = _doc.PageCount;
            if (!TryParseNotesRange(NotesRangeBox.Text, pages, out int from, out int to))
            {
                ShowNotesStatus(string.Format(Loc("Str_SummaryInvalidRange"), pages));
                return;
            }

            int run = ++_notesRun;
            _notesBusy = true;
            SetNotesBusy(true);
            _noteCards.Clear();
            NotesCardsHost.Children.Clear();
            ShowNotesStatus(string.Format(Loc("Str_Notes_Generating"), from, to));

            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _notesCts = new CancellationTokenSource();
            try
            {
                var config = _aiSettingsViewModel.ToGenConfig();
                var cards = await NotesGenerator.GenerateAsync(
                    config, _currentFile, from, to, progress: null, _notesCts.Token);
                if (run != _notesRun)
                {
                    return;   // a document switch or close superseded this run
                }

                RenderNoteCards(cards);
            }
            catch (OperationCanceledException)
            {
                // cancelled by a document switch / new run: the UI already moved on
            }
            catch (Exception ex)
            {
                if (run == _notesRun)
                {
                    SurfaceHealthLog.Log("notes: failed: " + ex.Message);
                    ShowNotesStatus(string.Format(Loc("Str_Notes_Failed"), FriendlyMessage(ex)));
                }
            }
            finally
            {
                if (run == _notesRun)
                {
                    _notesBusy = false;
                    SetNotesBusy(false);
                }
            }
        }

        /// <summary>Provider error text can carry the whole wire body; the
        /// status line keeps the first line only.</summary>
        private static string FriendlyMessage(Exception ex)
        {
            string message = ex.Message ?? ex.GetType().Name;
            int cut = message.IndexOfAny(['\r', '\n']);
            string first = cut >= 0 ? message[..cut] : message;
            return first.Length <= 160 ? first : first[..160];
        }

        // The notes range speaks the AI tester's dialect: one [start]-[end]
        // pair ("84-120"), digits and the dash only, en-dash tolerated, the
        // pair validated against the document (start >= 1, end >= start,
        // end <= pages) before Generate spends a request.
        private static bool TryParseNotesRange(string? text, int pageCount, out int from, out int to)
        {
            from = 0;
            to = 0;
            string raw = (text ?? string.Empty).Trim().Replace('\u2013', '-');
            int dash = raw.IndexOf('-');
            if (dash < 0)
            {
                // "124" instead of "45-124": a lone number spans the head of
                // the book, from the very first page of the pdf through that
                // page - the pair the box would have held as "1-124".
                if (!TryParseNotesHeadRange(raw, pageCount, out int head))
                {
                    return false;
                }

                from = 1;
                to = head;
                return true;
            }

            if (dash <= 0 ||
                !int.TryParse(raw[..dash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
                !int.TryParse(raw[(dash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) ||
                start < 1 || end < start || end > Math.Max(1, pageCount))
            {
                return false;
            }

            from = start;
            to = end;
            return true;
        }

        // A lone number ("124") is the head of the book: page 1 through that
        // page. The number must sit inside the document to be a real range.
        private static bool TryParseNotesHeadRange(string raw, int pageCount, out int to)
        {
            to = 0;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int head)
                || head < 1
                || head > Math.Max(1, pageCount))
            {
                return false;
            }

            to = head;
            return true;
        }

        // The range field's alphabet, the tester's own: digits and the dash
        // (minus or en-dash), spaces tolerated around the dash - anything else
        // dies at the input or at the paste gate.
        private static bool IsNotesRangeChar(char c)
        {
            return char.IsAsciiDigit(c) || c == '-' || c == '\u2013' || c == ' ';
        }

        /// <summary>The one-time input gates for the notes range field, wired on
        /// the first switch to the NOTES tab: the alphabet filter, the paste
        /// gate, and the tester's field manner - focusing selects the whole
        /// pair ("84-120") so typing replaces it in one stroke, and the
        /// swallowed first click cannot park the caret behind the selection.</summary>
        private void EnsureNotesRangeWired()
        {
            if (_notesRangeWired)
            {
                return;
            }

            _notesRangeWired = true;
            NotesRangeBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(IsNotesRangeChar);
            NotesRangeBox.GotFocus += (_, _) => NotesRangeBox.SelectAll();
            NotesRangeBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!NotesRangeBox.IsKeyboardFocused)
                {
                    NotesRangeBox.Focus();
                    e.Handled = true;
                }
            };
            System.Windows.DataObject.AddPastingHandler(NotesRangeBox, (_, e) =>
            {
                if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is string pasted &&
                    !pasted.All(IsNotesRangeChar))
                {
                    e.CancelCommand();
                }
            });
        }

        /// <summary>Enter in the range field starts the generation.</summary>
        private void NotesBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_notesBusy)
            {
                e.Handled = true;
                NotesGenerate_Click(sender, e);
            }
        }

        private void SetNotesBusy(bool busy)
        {
            NotesGenerateBtn.IsEnabled = !busy;
            NotesRangeBox.IsEnabled = !busy;
        }

        private void ShowNotesStatus(string text)
        {
            NotesStatusText.Text = text;
            NotesStatusText.Visibility = Visibility.Visible;
        }

        private void HideNotesStatus()
        {
            NotesStatusText.Visibility = Visibility.Collapsed;
        }

        private void ShowNotesEmpty()
        {
            NotesCardsHost.Children.Clear();
            NotesCardsHost.Children.Add(NotesEmptyText);
            NotesEmptyText.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Card rendering
        // ------------------------------------------------------------------

        private void RenderNoteCards(List<NoteCard> cards)
        {
            HideNotesStatus();
            _noteCards.Clear();
            _noteCards.AddRange(cards);
            NotesCardsHost.Children.Clear();
            if (_noteCards.Count == 0)
            {
                ShowNotesEmpty();
                return;
            }

            foreach (NoteCard card in _noteCards)
            {
                NotesCardsHost.Children.Add(BuildNoteCard(card));
            }
        }

        private string NoteTitle(NoteCard card) =>
            string.Format(Loc("Str_Notes_Pages"), card.FirstPage, card.LastPage);

        private Border BuildNoteCard(NoteCard card)
        {
            var title = new TextBlock
            {
                Text = NoteTitle(card),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("PrimaryBrush"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var copyBtn = new Button
            {
                Content = "\uE8C8",   // Segoe MDL2 Copy
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 22,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                BorderThickness = new Thickness(0),   // the flat chip look: no dark outline
                Foreground = Brushes.White,
                Style = (Style)FindResource("DarkButton"),
                ToolTip = Loc("Str_Notes_CopyCard"),
                Tag = card,
                FocusVisualStyle = null
            };
            copyBtn.Click += CopyNoteCard_Click;

            var header = new DockPanel { LastChildFill = true };
            header.Children.Add(copyBtn);
            DockPanel.SetDock(copyBtn, Dock.Right);
            header.Children.Add(title);

            // The note renders as real markdown (the model's card: bold page
            // anchors, structure) through the same renderer the chat and the
            // summary use. Its own scrollbar is disabled so the card grows
            // into the sidebar's scroll.
            var body = new RichTextBox
            {
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsDocumentEnabled = false
            };
            body.PreviewMouseWheel += NotesWheelForward;
            AiMarkdown.Rebuild(body, card.Content);

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(body);

            return new Border
            {
                Background = (Brush)FindResource("PaneBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack
            };
        }

        // The RichTextBox swallows the wheel before the outer scroll viewer
        // sees it (its disabled inner scrollbar still handles the event), so
        // the cards would not scroll - forward it, the outline panel's old
        // pattern.
        private void NotesWheelForward(object sender, MouseWheelEventArgs e)
        {
            NotesScrollViewer.ScrollToVerticalOffset(NotesScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // ------------------------------------------------------------------
        // Copy actions
        // ------------------------------------------------------------------

        private void CopyNoteCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not NoteCard card)
            {
                return;
            }

            CopyNotesToClipboard([card]);
            FlashCopyButton(btn);
        }

        /// <summary>Copies "**Pages X - Y**\n\n{content}" per card - the same
        /// shape the cards display, pasted as markdown.</summary>
        private void CopyNotesToClipboard(IReadOnlyList<NoteCard> cards)
        {
            var sb = new StringBuilder();
            foreach (NoteCard card in cards)
            {
                sb.Append("**").Append(NoteTitle(card)).Append("**\n\n")
                  .Append(card.Content)
                  .Append("\n\n");
            }

            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
            }
            catch
            {
                // the clipboard can be held by another process; a failed copy
                // must never take the sidebar down
            }
        }

        private void FlashCopyButton(Button btn)
        {
            object restore = btn.Content;
            btn.Content = "\u2713";   // check mark
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                btn.Content = restore;
                timer.Stop();
            };
            timer.Start();
        }
    }
}
