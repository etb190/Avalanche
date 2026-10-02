// Features/Summary/SummaryWindow.xaml.cs — the floating page-summary companion.
//
// Single owned window (MainWindow keeps one instance), themed chrome via
// DialogChrome (rounded card, themed title bar, Escape-close, fade). The body
// is a reading navigator, not a form:
//   * a start-page stepper at the top: the arrows jump the whole range by the
//     selected span (40p chip -> 40 pages) and start the new digest, the field
//     between them shows the range ("41-80") and edits the start page,
//   * Start and Reset under the field: together with the arrows they are the
//     ONLY controls that may begin or end a generation,
//   * range chips (1p..100p) that reshape the displayed range without generating,
//   * a word-limit dropdown telling the model how long the digest should be,
//   * a summary-language dropdown; Arabic flips the digest right-to-left.
// The arrows never overlap: forward lands on the first page after the stretch
// just covered, backward re-opens the stretch before this one. A superseded run
// (another arrow press, Start, Reset, close) is detached by a generation counter
// and its request cancelled - stale continuations cannot repaint the card.
// The reading position survives restarts per document; so do range, word
// ceiling, language and the window's size and place.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using System.Windows.Threading;
    using Avalanche.Controls;
    using Avalanche.Features.AI;
    using Avalanche.Services;

    public partial class SummaryWindow : Window
    {
        // The navigator's fixed vocabularies: range chips, word ceilings, and the
        // languages the digest can be written in (labels exist in every Strings file).
        private static readonly int[] RangeChoices = { 1, 5, 20, 40, 60, 80, 100 };
        private static readonly int[] WordChoices = { 500, 750, 1000, 1500, 2000, 3000, 4500 };
        private static readonly string[] LanguageChoices = { "English", "French", "Spanish", "Italian", "Arabic" };

        private static readonly Dictionary<string, string> LangKeySuffix = new()
        {
            ["English"] = "En",
            ["French"] = "Fr",
            ["Spanish"] = "Es",
            ["Italian"] = "It",
            ["Arabic"] = "Ar"
        };

        private readonly string _filePath;
        private readonly string _documentId;
        private readonly int _pageCount;


        private readonly Func<int> _currentPageProvider;   // 0-based, -1 when closed
        private readonly Func<AiProviderConfig> _configProvider;
        private readonly Func<string, string> _loc;

        private CancellationTokenSource? _cts;
        private bool _generating;
        private string _fullText = string.Empty;
        private bool _flushPending;
        private bool _closed;
        private int _generation;        // bumped by supersede/reset/close: stale continuations can't repaint

        private int _rangePages = 20;   // pages per digest: the selected range chip's value
        private int _targetWords = 1000;
        private string _language = "English";
        private int _startPage = 1;     // the anchor: first page of the displayed range
        private bool _editing;          // the field is showing the bare start page for editing
        private double _digestFont = 13;    // the digest's face; the title-bar + and - move it
        private int _runFirstPage = 1;  // the range the current/last digest covered; the
        private int _runLastPage = 1;   // verification badge maps its audit onto these pages

        // The 30-second prefetch buffer: the NEXT sequential range, fetched in
        // the background while the reader digests the current one. A ready
        // buffer -> the next arrow paints with zero lag; an in-flight fetch ->
        // the arrow attaches to it; any manual range move discards both.
        private DispatcherTimer? _prefetchTimer;    // the 30s read delay, one-shot
        private CancellationTokenSource? _prefetchCts;
        private Task<string>? _prefetchFlight;      // the in-flight fetch
        private string? _prefetchText;              // the completed buffer (null = none)
        private int _prefetchFirst, _prefetchLast;  // the stretch the buffer covers

        public SummaryWindow(
            MainWindow owner,
            string filePath,
            string documentId,
            int pageCount,
            Func<int> currentPageProvider,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            InitializeComponent();
            _filePath = filePath;
            _documentId = documentId;
            _pageCount = Math.Max(1, pageCount);
            _currentPageProvider = currentPageProvider;
            _configProvider = configProvider;
            _loc = loc;

            // fade:false - the navigator owns its own two-sided choreography (the
            // pop-in entrance plus the pop-flavored fade close just below);
            // Configure's generic close-only fade would stack a second Closing
            // handler onto the same window and the two cancels would fight.
            DialogChrome.Configure(this, owner, resizable: true, fade: false);
            WindowFx.EnableFadeClose(this, WindowFx.PopMs, pop: true);

            // The entrance: the window is born transparent and PlayOpenPop raises
            // it on Loaded, so the first painted frame is already animating from
            // zero - no flash of a fully drawn window before the motion starts.
            Opacity = 0;
            Loaded += (_, _) => WindowFx.PlayOpenPop(this);
            // Borderless windows (WindowStyle.None) have no native resize border - the same
            // WindowChrome PrintPreviewWindow uses restores edge resizing without a grip.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(12),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            // The title bar: the wordmark, two digest-font chips before the close mark,
            // a close mark squared to the wordmark's height, and a hairline under the
            // whole bar separating it from the navigator's body.
            var frame = DialogChrome.Frame(this, owner, "Avalanche", Close, BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    BottomSeparator = true,
                    CloseButtonSize = 24,
                    // The Recap switch rides the bar's middle: centered between
                    // the wordmark on the left and the beaker chip on the right.
                    Centered = new UIElement[] { BuildRecapToggle() },
                    BeforeClose = new UIElement[]
                    {
                        // The test-this-range chip: leftmost of the three, the AI
                        // Test beaker with real air before the minus. It opens the
                        // AI test window and starts a probe over the exact stretch
                        // the navigator is showing - the range's first page through
                        // its last.
                        BuildTestRangeChip(),
                        // The pair the reader asked for: minus on the left, a clear gap
                        // between the two, and the increase chip on the right, nearest
                        // the close mark. Drawn + and - faces - rectangles center
                        // exactly, where the MDL2 glyphs' ink rode high in the chip.
                        // The gap between the increase chip and the close mark comes
                        // from the chip's own right margin: the X keeps its exact place
                        // (the reader was explicit - no more sliding it toward the edge).
                        TitleChip(plus: false, "Str_SummaryFontDown", () => AdjustDigestFont(-1), new Thickness(0, 0, 6, 0)),
                        TitleChip(plus: true, "Str_SummaryFontUp", () => AdjustDigestFont(+1), new Thickness(0, 0, 8, 0))
                    },
                    CloseCreated = DressCloseChip
                });
            // The lower corner squares have no visible pixels (transparent halo over the rounded
            // card corners), so the OS passes clicks straight through and WindowChrome's
            // geometric band never fires there - the upper corners sit on the title-bar band
            // and work. Hit-testable grips with a ResizeGripDirection restore two-axis corner
            // resizing, matching the upper corners.
            var root = new Grid();
            root.Children.Add(frame);
            root.Children.Add(CornerGrip(HorizontalAlignment.Left, System.Windows.Shell.ResizeGripDirection.BottomLeft));
            root.Children.Add(CornerGrip(HorizontalAlignment.Right, System.Windows.Shell.ResizeGripDirection.BottomRight));
            Content = root;
            Title = "Avalanche";

            // Stepper arrows: each press moves the whole range by the selected span and
            // starts the digest for the new stretch - "next page of the reading".
            NavPrevBtn.Click += (_, _) => MoveRange(-1);
            NavNextBtn.Click += (_, _) => MoveRange(+1);

            // The field shows the range at rest; the first click swaps it for the bare
            // start page, fully selected, so typing replaces it in one stroke.
            StartBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!StartBox.IsKeyboardFocused)
                {
                    StartBox.Focus();
                    e.Handled = true;   // GotKeyboardFocus selects all; the click must not re-place the caret
                }
            };
            StartBox.GotKeyboardFocus += (_, _) => BeginStartEdit();
            StartBox.LostKeyboardFocus += (_, _) => CommitStartEdit();
            StartBox.TextChanged += OnStartTextChanged;
            StartBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    CommitStartEdit();
                    StartBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    EndStartEdit();     // revert to the range face, nothing committed
                }
            };

            // Start and Reset: with the arrows, the only generation owners there are.
            StartBtn.Click += (_, _) => StartGeneration();
            ResetBtn.Click += (_, _) => ResetAll();

            // Range chips: label from Strings, page count from Tag. A click checks the
            // chip (accent state) and reshapes the displayed range - deliberately NOT
            // a generation trigger: only the arrows, Start and Reset may start a run.
            foreach (var chip in new[] { RangeChip1, RangeChip5, RangeChip20, RangeChip40, RangeChip60, RangeChip80, RangeChip100 })
            {
                int pages = int.Parse((string)chip.Tag, CultureInfo.InvariantCulture);
                chip.Content = loc("Str_SummaryR" + pages + "p");
                chip.Checked += (_, _) => OnRangeChanged(pages);
            }

            // Word ceiling: displayed "1,000"-style, the raw number rides in Tag.
            foreach (int words in WordChoices)
            {
                WordsCombo.Items.Add(new ComboBoxItem
                {
                    Content = words.ToString("N0", CultureInfo.InvariantCulture),
                    Tag = words
                });
            }

            WordsCombo.SelectionChanged += (_, _) =>
            {
                if (WordsCombo.SelectedItem is ComboBoxItem item && item.Tag is int words)
                {
                    _targetWords = words;
                    AppDataPaths.SetSetting("summary.words", words.ToString(CultureInfo.InvariantCulture));
                    InvalidatePrefetch();   // the buffer was fetched for the old ceiling
                }
            };

            // Summary language: endonym labels, canonical name in Tag. Arabic turns the
            // digest area right-to-left; the book's headings stay verbatim either way.
            foreach (string language in LanguageChoices)
            {
                LangCombo.Items.Add(new ComboBoxItem
                {
                    Content = loc("Str_SummaryLang" + LangKeySuffix[language]),
                    Tag = language
                });
            }

            LangCombo.SelectionChanged += (_, _) =>
            {
                if (LangCombo.SelectedItem is ComboBoxItem item && item.Tag is string language)
                {
                    _language = language;
                    AppDataPaths.SetSetting("summary.lang", language);
                    InvalidatePrefetch();   // the buffer was fetched for the old language
                    ApplyReadingDirection();
                }
            };

            LoadPreferences();
            SelectChip(_rangePages);            // fires Checked -> OnRangeChanged -> ShowRange
            SelectCombo(WordsCombo, _targetWords);
            SelectCombo(LangCombo, _language);
            ApplyReadingDirection();
            DocBox.FontSize = _digestFont;
            RestoreDigest();                    // the last digest of this book, if any

            // The prefetch clock: one-shot. Thirty seconds after a digest lands,
            // while the reader is reading, the NEXT sequential stretch starts in
            // the background so the next arrow can land on a ready page.
            _prefetchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _prefetchTimer.Tick += (_, _) =>
            {
                _prefetchTimer.Stop();
                StartPrefetch();
            };

            Closed += (_, _) =>
            {
                _closed = true;
                _generation++;      // a run cancelled by the close can't repaint either
                _cts?.Cancel();
                InvalidatePrefetch();   // the clock and the flight die with the window
                SaveDigest();       // the digest stays with the book across sessions
                PersistPlacement();
            };

            RestorePlacement();

            // Placement follows the window live (debounced): a killed app still finds
            // the window where the reader left it, not where it started.
            var placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            placementTimer.Tick += (_, _) =>
            {
                placementTimer.Stop();
                if (!_closed)
                {
                    PersistPlacement();
                }
            };
            LocationChanged += (_, _) =>
            {
                if (_closed)
                {
                    return;
                }

                placementTimer.Stop();
                placementTimer.Start();
            };
            SizeChanged += (_, _) =>
            {
                if (_closed)
                {
                    return;
                }

                placementTimer.Stop();
                placementTimer.Start();
            };
        }

        /// <summary>Raised whenever the displayed range changes (a chip, an arrow, the
        /// start field, Reset, restore-on-open) - MainWindow re-highlights the page list.</summary>
        public event Action? RangeVisualChanged;

        // Raised when the arrows move the reading range (the 1-based first page
        // of the new range): the document follows - a smooth, fast scroll that
        // parks the new stretch's first page at the top of the viewport.
        public event Action<int>? PageNavigationRequested;

        // Raised by the title bar's beaker chip: the reader wants the AI test to
        // probe exactly the stretch on screen. MainWindow opens (or reuses) the
        // tester and starts it on this range - first page through last.
        public event Action<int, int>? TestRangeRequested;

        /// <summary>True when this window already summarizes the given document
        /// (MainWindow reuses the instance instead of opening a second one).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Reading navigator
        // ------------------------------------------------------------------

        // Range, word ceiling, language - and the per-document reading position -
        // survive window reopenings, restarts and documents: a reading session
        // keeps its shape until the reader changes it.
        private void LoadPreferences()
        {
            if (int.TryParse(AppDataPaths.GetSetting("summary.range"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int range)
                && RangeChoices.Contains(range))
            {
                _rangePages = range;
            }

            if (int.TryParse(AppDataPaths.GetSetting("summary.words"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int words)
                && WordChoices.Contains(words))
            {
                _targetWords = words;
            }

            string? lang = AppDataPaths.GetSetting("summary.lang");
            if (!string.IsNullOrEmpty(lang) && LanguageChoices.Contains(lang))
            {
                _language = lang;
            }

            if (double.TryParse(AppDataPaths.GetSetting("summary.font"), NumberStyles.Float, CultureInfo.InvariantCulture, out double font)
                && font >= 10 && font <= 24)
            {
                _digestFont = font;
            }

            // The opening anchor is the page the reader is LOOKING at: the
            // navigator starts at the viewer's active page - not at page one,
            // not at a stale bookmark. Only when the viewer cannot say (no page
            // rendered yet) does the remembered anchor - or page one - step in.
            int current = _currentPageProvider();
            if (current >= 0)
            {
                _startPage = Math.Clamp(current + 1, 1, _pageCount);
            }
            else
            {
                int savedStart = ReadIntSetting("summary.start." + _documentId);
                _startPage = savedStart >= 1 && savedStart <= _pageCount ? savedStart : 1;
            }
        }

        private void SelectChip(int pages)
        {
            foreach (var chip in new[] { RangeChip1, RangeChip5, RangeChip20, RangeChip40, RangeChip60, RangeChip80, RangeChip100 })
            {
                chip.IsChecked = int.Parse((string)chip.Tag, CultureInfo.InvariantCulture) == pages;
            }
        }

        private static void SelectCombo(ComboBox combo, object value)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                item.IsSelected = Equals(item.Tag, value);
            }
        }

        // The last page of the range on screen: N pages from the anchor, clipped at
        // the document (a 60p chip near the end reads to the last page).
        private int RangeEnd()
        {
            return Math.Min(_startPage + _rangePages - 1, _pageCount);
        }

        // The right end of the status line: how many pages of the document remain
        // AFTER the range currently on screen - total pages minus the range's
        // last page, floored at zero. The count follows the range, not the
        // reader's cursor: stepping to the next stretch refills it, reaching the
        // end of the book empties it. MainWindow pokes NotifyViewerPageChanged
        // on every page move, and every range repaint re-runs it here.
        private void UpdatePagesLeft()
        {
            int left = Math.Max(0, _pageCount - RangeEnd());
            PagesLeftText.Text = string.Format(
                _loc("Str_SummaryPagesLeft"), left.ToString(CultureInfo.InvariantCulture));
        }

        // The field's at-rest face: the page range the next digest will cover, e.g.
        // "41-80". The range appears nowhere else in the window - the field IS it.
        private void ShowRange()
        {
            UpdateNavEnabled();
            if (!_editing)
            {
                int last = RangeEnd();
                StartBox.Text = string.Create(CultureInfo.InvariantCulture, $"{_startPage}-{last}");
            }

            UpdatePagesLeft();
            RangeVisualChanged?.Invoke();
        }

        // Arrows dim at the ends of the book: nothing to move onto there.
        private void UpdateNavEnabled()
        {
            NavPrevBtn.IsEnabled = _startPage > 1;
            NavNextBtn.IsEnabled = RangeEnd() < _pageCount;
        }

        // The arrows page through the book one digest at a time: forward lands on the
        // first page AFTER the stretch just covered (no overlap, no gap), backward
        // re-opens the stretch before this one. Both start the new digest at once.
        private void MoveRange(int direction)
        {
            if (direction > 0)
            {
                int end = RangeEnd();
                if (end >= _pageCount)
                {
                    return;     // the last stretch of the book is already on screen
                }

                SetStartPage(end + 1);
                PageNavigationRequested?.Invoke(_startPage);
                if (ConsumePrefetch())
                {
                    return;     // the buffer (or its flight) served the new stretch
                }
            }
            else
            {
                int previous = Math.Max(1, _startPage - _rangePages);
                if (previous == _startPage)
                {
                    return;     // already on the first stretch
                }

                SetStartPage(previous);
                PageNavigationRequested?.Invoke(_startPage);
                InvalidatePrefetch();   // backward: the next-stretch buffer no longer fits
            }

            StartGeneration();
        }

        private void SetStartPage(int page)
        {
            _startPage = Math.Clamp(page, 1, _pageCount);
            PersistStart();
            ShowRange();
        }

        private void PersistStart()
        {
            AppDataPaths.SetSetting(
                "summary.start." + _documentId, _startPage.ToString(CultureInfo.InvariantCulture));
        }

        // ----- the range field's two faces -----

        private void BeginStartEdit()
        {
            if (_editing || _generating)
            {
                return;
            }

            _editing = true;
            StartBox.Text = _startPage.ToString(CultureInfo.InvariantCulture);
            StartBox.SelectAll();
        }

        private void EndStartEdit()
        {
            _editing = false;
            ShowRange();
        }

        private void CommitStartEdit()
        {
            if (!_editing)
            {
                return;
            }

            _editing = false;
            int parsed = int.TryParse(StartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : _startPage;   // an empty or non-numeric field keeps the current anchor
            int previous = _startPage;
            SetStartPage(parsed);   // clamps, persists, repaints the range
            // Typing a new anchor IS a navigation command, the arrows' equal: the
            // document view glides to the new stretch's first page and the page
            // list parks it at its top. An unchanged commit (focus merely leaving
            // the field) must not yank the reader anywhere.
            if (_startPage != previous)
            {
                InvalidatePrefetch();   // a hand-typed anchor retires the buffer
                PageNavigationRequested?.Invoke(_startPage);
            }
        }

        // While edited, the field takes digits only (a 5-digit cap covers any real
        // document); anything else is stripped as it is typed. The range face - shown
        // at rest - contains a dash by design and is never sanitized.
        private void OnStartTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_editing)
            {
                return;
            }

            string clean = new string((StartBox.Text ?? string.Empty).Where(char.IsDigit).ToArray());
            if (clean.Length > 5)
            {
                clean = clean[..5];
            }

            if (clean != StartBox.Text)
            {
                StartBox.Text = clean;
                StartBox.CaretIndex = clean.Length;
            }
        }

        private void OnRangeChanged(int pages)
        {
            _rangePages = pages;
            AppDataPaths.SetSetting("summary.range", pages.ToString(CultureInfo.InvariantCulture));
            InvalidatePrefetch();   // the buffered stretch no longer matches the span
            ShowRange();        // reshape the displayed range - never a generation trigger
        }

        // Arabic reads right to left: the digest area mirrors (scrollbar flips, text
        // starts from the right). The rest of the chrome keeps its orientation.
        private void ApplyReadingDirection()
        {
            DocBox.FlowDirection = string.Equals(_language, "Arabic", StringComparison.Ordinal)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }

        // ------------------------------------------------------------------
        // Generation
        // ------------------------------------------------------------------

        private async void StartGeneration()
        {
            if (_closed)
            {
                return;
            }

            InvalidatePrefetch();   // a manual run retires the buffer and its flight

            int first = _startPage;     // always clamped by SetStartPage
            if (first < 1 || first > _pageCount)
            {
                Overlay(string.Format(_loc("Str_SummaryInvalidRange"), _pageCount));
                return;
            }

            // Supersede: a running digest is detached (its continuations lose the
            // right to repaint) and its request cancelled before the new one starts.
            // This is how the arrows and Start "stop" a run - by starting the next.
            if (_generating)
            {
                _generation++;
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            }

            int last = RangeEnd();
            int gen = ++_generation;
            _generating = true;
            _fullText = string.Empty;
            // The superseded source is deliberately NOT disposed: the detached loop is
            // still polling its token, and a disposed source can throw from those
            // polls. Garbage collection reclaims it.
            _cts = new CancellationTokenSource();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);

            var request = new SummaryRequest(
                _filePath, _documentId, first, last, _targetWords, _language, BypassCache: false);
            _runFirstPage = first;
            _runLastPage = last;
            try
            {
                await foreach (SummaryUpdate update in PageSummarizer.GenerateAsync(
                                   request, _configProvider(), _loc, _cts.Token))
                {
                    switch (update.Kind)
                    {
                        case "progress":
                            if (gen == _generation)
                            {
                                StatusText.Text = update.Text;
                            }

                            break;
                        case "delta":
                            AppendDelta(update.Text, gen);
                            break;
                        case "done":
                            if (gen == _generation)
                            {
                                if (update.Text.Length > 0)
                                {
                                    _fullText = update.Text;
                                }

                                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                                FinishSuccess();
                                // The field keeps showing the stretch just read; the
                                // next arrow lands on the page after it.
                            }

                            break;
                        case "notext":
                            if (gen == _generation)
                            {
                                Overlay(_loc("Str_SummaryNoText"));
                                DiagnosticsBundle.Dump("summary notext");
                            }

                            break;
                        case "error":
                            if (gen == _generation)
                            {
                                Overlay(string.Format(_loc("Str_SummaryError"), update.Text));
                                DiagnosticsBundle.Dump("summary error");
                            }

                            break;
                    }

                    if (update.Kind is "done" or "notext" or "error")
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation means this run was superseded (or the window closed);
                // both bump the generation, so only repaint when still current.
                if (gen == _generation)
                {
                    DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                    StatusText.Text = _loc("Str_SummaryStopped");
                }
            }
            catch (Exception ex)
            {
                // Terminal failure: drop the evidence (log tail with the extraction
                // and POST lines) on the Desktop regardless of generation - a stale
                // dump after a close is still evidence.
                DiagnosticsBundle.Dump("summary exception: " + ex.GetType().Name);
                if (gen == _generation)
                {
                    Overlay(string.Format(_loc("Str_SummaryError"), PageSummarizer.FriendlyError(ex)));
                }
            }
            finally
            {
                // A superseded run must not clear the busy state of the run that
                // replaced it - only the current generation owns the UI here.
                if (gen == _generation)
                {
                    _generating = false;
                    SetBusy(false);
                }
            }
        }

        private void AppendDelta(string delta, int gen)
        {
            _fullText += delta;
            if (_flushPending)
            {
                return;
            }

            _flushPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _flushPending = false;
                if (_generating && gen == _generation)
                {
                    DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                    DocBox.ScrollToEnd();
                }
            }));
        }

        private void FinishSuccess()
        {
            Overlay(null);
            // The status line stays factual - and, when the provider reported its
            // usage, upgrades into the verification badge: how many prompt tokens
            // the model actually read against how many the extracted pages
            // estimate. A provider that silently truncated the input can no longer
            // hide behind a fluent digest; without usage the plain word/character
            // count remains.
            StatusText.Text = VerificationStatusLine();
            SaveDigest();       // the digest survives the window, the app, the session
            _cts?.Dispose();
            _cts = null;
            SchedulePrefetch(); // 30s from now, the next stretch fetches itself
        }

        // The post-run status line. With provider usage: the verification badge -
        // "[ v N pages verified ] | T tokens read | W words", or the red-flag
        // truncation line naming the page the text was cut at. The audit rides on
        // PageSummarizer's run counters (reset and accumulated inside the gate, so
        // they belong to exactly the run that just finished). Without usage: the
        // plain word/character count, exactly as before.
        private string VerificationStatusLine()
        {
            int words = PageSummarizer.CountWords(_fullText);
            if (!PageSummarizer.RunPromptTokensKnown || PageSummarizer.RunTokensEstimated <= 0)
            {
                return string.Format(
                    _loc("Str_SummaryCounts"), words, _fullText.Length);
            }

            (bool truncated, _) = AiContextTester.Audit(
                PageSummarizer.RunPromptTokens, PageSummarizer.RunTokensEstimated);
            int pages = Math.Max(1, _runLastPage - _runFirstPage + 1);
            string tokens = PageSummarizer.RunPromptTokens.ToString("N0", CultureInfo.InvariantCulture);
            if (truncated)
            {
                // Walk the token shortfall through the page span: the fraction of
                // the estimated tokens the model actually read maps onto the range.
                double fraction = (double)PageSummarizer.RunPromptTokens / PageSummarizer.RunTokensEstimated;
                int cutPage = Math.Min(_runLastPage, _runFirstPage + (int)(fraction * (pages - 1)));
                return string.Format(
                    _loc("Str_SummaryBadgeTruncated"), cutPage, _runLastPage, tokens);
            }

            return string.Format(
                _loc("Str_SummaryBadgeVerified"), pages, tokens, words);
        }

        // Reset: the fourth generation owner. It stops any live run, clears the
        // card and returns the navigator to idle/ready - and NOTHING else: the
        // selected range stays exactly where the reader left it, waiting for
        // Start. A reset that also yanked the anchor back to page one would
        // punish a reader who only wanted a clean slate of TEXT, not of place.
        private void ResetAll()
        {
            _generation++;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _generating = false;
            _cts = null;
            _fullText = string.Empty;
            _flushPending = false;
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            StatusText.Text = string.Empty;
            InvalidatePrefetch();   // the reset was manual: the buffer goes too
            SaveDigest();       // the reset was the reader's action: forget the digest
            SetBusy(false);
        }

        // While a run is in flight the parameter controls quiet down (they cannot
        // start or stop a run, so they have nothing to do here). The four generation
        // owners stay live: arrows and Start supersede the run, Reset ends it.
        private void SetBusy(bool busy)
        {
            StartBox.IsEnabled = !busy;
            RangePanel.IsEnabled = !busy;
            WordsCombo.IsEnabled = !busy;
            LangCombo.IsEnabled = !busy;
        }

        // Invisible but hit-testable corner handle: a near-transparent fill keeps the layered
        // window from passing clicks through, and the attached ResizeGripDirection tells
        // WindowChrome which non-client behaviour (both-axis corner resize) the grip maps to.
        private static Rectangle CornerGrip(HorizontalAlignment align, System.Windows.Shell.ResizeGripDirection direction)
        {
            var grip = new Rectangle
            {
                Width = 20,
                Height = 20,
                HorizontalAlignment = align,
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0))
            };
            System.Windows.Shell.WindowChrome.SetResizeGripDirection(grip, direction);
            return grip;
        }

        private void Overlay(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                OverlayText.Visibility = Visibility.Collapsed;
                OverlayText.Text = string.Empty;
                DocBox.Visibility = Visibility.Visible;
            }
            else
            {
                OverlayText.Text = message;
                OverlayText.Visibility = Visibility.Visible;
                DocBox.Visibility = Visibility.Collapsed;
            }
        }

        // ------------------------------------------------------------------
        // The 30-second prefetch buffer
        // ------------------------------------------------------------------

        // While the reader is busy with the digest just delivered, the navigator
        // quietly fetches the NEXT sequential stretch: 30 seconds after a run
        // finishes, the following range (same span, clipped at the document)
        // starts in the background. The next arrow then lands on a ready digest
        // - shown with zero lag - or attaches to the still-running fetch; any
        // manual range move discards both.

        private void SchedulePrefetch()
        {
            InvalidatePrefetch();
            if (_closed || _generating || RangeEnd() >= _pageCount)
            {
                return;     // the book has no next stretch (or a run is already live)
            }

            _prefetchTimer?.Stop();
            _prefetchTimer?.Start();
        }

        private void StartPrefetch()
        {
            InvalidatePrefetch();
            if (_closed || _generating)
            {
                return;
            }

            int first = RangeEnd() + 1;
            if (first > _pageCount)
            {
                return;
            }

            int last = Math.Min(first + _rangePages - 1, _pageCount);
            _prefetchFirst = first;
            _prefetchLast = last;
            _prefetchText = null;
            _prefetchCts = new CancellationTokenSource();
            CancellationToken ct = _prefetchCts.Token;
            var request = new SummaryRequest(
                _filePath, _documentId, first, last, _targetWords, _language, BypassCache: false);
            AiProviderConfig config = _configProvider();
            Func<string, string> loc = _loc;
            _prefetchFlight = Task.Run(
                async () =>
                {
                    var buffer = new System.Text.StringBuilder();
                    await foreach (SummaryUpdate update in
                        PageSummarizer.GenerateAsync(request, config, loc, ct).ConfigureAwait(false))
                    {
                        if (update.Kind == "delta")
                        {
                            buffer.Append(update.Text);
                        }
                        else if (update.Kind == "done")
                        {
                            return update.Text.Length > 0 ? update.Text : buffer.ToString();
                        }
                        else if (update.Kind is "notext" or "error")
                        {
                            throw new InvalidOperationException(
                                update.Kind == "notext" ? "notext" : update.Text);
                        }
                    }

                    return buffer.ToString();
                },
                ct);
            _ = _prefetchFlight.ContinueWith(
                t => _prefetchText = t.IsCompletedSuccessfully ? t.Result : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        // The next arrow's shortcut. True when the buffer served the new stretch:
        // a ready digest paints at once (and earns the same verification badge a
        // fresh run would), an in-flight one is attached to with the progress
        // live; anything else falls through to a fresh generation.
        private bool ConsumePrefetch()
        {
            if (_prefetchFirst != _startPage)
            {
                return false;
            }

            if (_prefetchText is { Length: > 0 } ready)
            {
                _generation++;      // any stray continuation of the old run loses the card
                _generating = false;
                _fullText = ready;
                _runFirstPage = _prefetchFirst;
                _runLastPage = _prefetchLast;
                Overlay(null);
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                StatusText.Text = VerificationStatusLine();
                SaveDigest();
                InvalidatePrefetch();
                return true;
            }

            if (_prefetchFlight is { IsCompleted: false } flight)
            {
                AttachPrefetch(flight);
                return true;
            }

            return false;
        }

        // The prefetch is still running: park the card in the busy state and let
        // the flight finish into it - no second request, no duplicate spend. A
        // flight that dies (network, provider) falls back to a fresh run; a
        // flight that reports "notext" shows the same verdict a live run would.
        private async void AttachPrefetch(Task flight)
        {
            int gen = ++_generation;
            _generating = true;
            _fullText = string.Empty;
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);
            StatusText.Text = string.Format(
                _loc("Str_SummaryPreparing"), _prefetchFirst, _prefetchLast);
            try
            {
                await flight.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (gen == _generation && !_closed)
                {
                    _generating = false;
                    SetBusy(false);
                    InvalidatePrefetch();
                    if (ex.Message == "notext")
                    {
                        Overlay(_loc("Str_SummaryNoText"));
                        return;
                    }

                    StartGeneration();      // the fetch died: run the stretch fresh
                }

                return;
            }

            if (gen != _generation || _closed)
            {
                return;     // superseded, or the window closed meanwhile
            }

            _generating = false;
            SetBusy(false);
            string? text = flight is Task<string> typed && typed.IsCompletedSuccessfully
                ? typed.Result
                : _prefetchText;
            if (!string.IsNullOrEmpty(text))
            {
                _fullText = text;
                _runFirstPage = _prefetchFirst;
                _runLastPage = _prefetchLast;
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                StatusText.Text = VerificationStatusLine();
                SaveDigest();
            }
            else
            {
                Overlay(_loc("Str_SummaryNoText"));
            }

            InvalidatePrefetch();
        }

        // Kill the clock, the flight and the buffer. Every manual navigation,
        // parameter change, reset, new run and close funnels through here, so
        // stale text can never surface.
        private void InvalidatePrefetch()
        {
            _prefetchTimer?.Stop();
            try { _prefetchCts?.Cancel(); } catch (ObjectDisposedException) { }
            _prefetchCts?.Dispose();
            _prefetchCts = null;
            _prefetchFlight = null;
            _prefetchText = null;
            _prefetchFirst = 0;
            _prefetchLast = 0;
        }

        // ------------------------------------------------------------------
        // Title-bar font chips + digest persistence
        // ------------------------------------------------------------------

        // One 24px title-bar square: the glyph rides the shared icon font, the tooltip
        // is localized, and the click is taken in the tunnel so the bar's DragMove
        // handler never mistakes a chip press for the start of a window move.
        private Button TitleChip(bool plus, string tooltipKey, Action onClick, Thickness? margin = null)
        {
            // Drawn minus/plus instead of MDL2 glyphs: the font's ink rides high
            // in its line box, which read as glued to the chip's top border.
            // Rectangles center exactly - no font metrics to guess - and the
            // shared stroke keeps every mark in the bar the same weight.
            const double t = 1.6;   // stroke, in control px
            var mark = new Grid { Width = 10, Height = 10 };
            var bar = new Rectangle { Width = 10, Height = t, RadiusX = t / 2, RadiusY = t / 2 };
            bar.SetResourceReference(Shape.FillProperty, "TextBrush");
            mark.Children.Add(bar);
            if (plus)
            {
                var stem = new Rectangle { Width = t, Height = 10, RadiusX = t / 2, RadiusY = t / 2 };
                stem.SetResourceReference(Shape.FillProperty, "TextBrush");
                mark.Children.Add(stem);
            }
            var chip = new Button
            {
                Style = (Style)FindResource("SumTitleBtn"),
                Content = mark,
                Margin = margin ?? new Thickness(0),
                ToolTip = _loc(tooltipKey)
            };
            chip.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return chip;
        }

        // The Recap switch: the AI tester's iOS-style toggle (the exact same
        // 40px track, the same 16px white thumb gliding 18px in 150ms - the
        // style is mirrored verbatim in this window's resources) beside a bold
        // "Recap" label. The chrome centers the pair in the top middle bar,
        // between the wordmark and this window's beaker chip. Checked is the
        // feature's persisted state; unchecking also dismisses any recap
        // window that is showing.
        private FrameworkElement BuildRecapToggle()
        {
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("TestModeToggle"),
                IsChecked = RecapController.Enabled,
                ToolTip = _loc("Str_TT_RecapMode")
            };
            // Press, not click: the flip happens on touch-down like every iOS
            // switch, taken in the tunnel so the bar's DragMove can never turn
            // a tap into a window move. Setting IsChecked by hand raises
            // Checked/Unchecked, which drive both the thumb animation and the
            // feature's state.
            toggle.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                toggle.IsChecked = toggle.IsChecked != true;
            };
            toggle.Checked += (_, _) => RecapController.SetEnabled(true);
            toggle.Unchecked += (_, _) => RecapController.SetEnabled(false);

            var label = new TextBlock
            {
                Text = _loc("Str_Lbl_RecapMode"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = _loc("Str_TT_RecapMode")
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(toggle);
            row.Children.Add(label);
            return row;
        }

        // The test-this-range chip: the same 24px title-bar square as the font
        // chips, wearing the AI Test toolbar's own beaker glyph so the two read
        // as one feature. The click is taken in the tunnel like theirs (the bar's
        // DragMove must not eat it) and raises TestRangeRequested with the
        // stretch currently on screen - the range's first page through its last.
        private Button BuildTestRangeChip()
        {
            var glyph = new TextBlock
            {
                Text = "\uE9D9",                    // Segoe MDL2 TestBeaker
                FontFamily = UiKit.IconFont,
                FontSize = 13,
                Margin = new Thickness(0, -1, 0, 0) // the font's ink rides high; optically center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var chip = new Button
            {
                Style = (Style)FindResource("SumTitleBtn"),
                Content = glyph,
                Margin = new Thickness(0, 0, 6, 0),     // the gap before the minus - the same air the minus keeps before the plus
                ToolTip = _loc("Str_SummaryTestRange")
            };
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                TestRangeRequested?.Invoke(_startPage, RangeEnd());
            };
            return chip;
        }

        // The close mark wears the same container as its neighbours: the
        // SumTitleBtn face (pane fill, hairline border, hover) with a drawn X
        // at the same stroke as the drawn minus and plus - the chrome style's
        // bare floating X read as a smaller, different species beside the chips.
        private void DressCloseChip(Button close)
        {
            const double t = 1.6;   // same stroke as the font chips
            var x = new Grid { Width = 10, Height = 10 };
            foreach (double angle in new[] { 45d, -45d })
            {
                var bar = new Rectangle { Width = 10, Height = t, RadiusX = t / 2, RadiusY = t / 2 };
                // The close mark is red: the ink rides the button's Foreground so
                // SumCloseBtn's triggers drive it - danger red at rest, the
                // on-primary color over the red hover face.
                bar.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
                bar.RenderTransform = new RotateTransform(angle);
                x.Children.Add(bar);
            }
            close.Content = x;
            close.Style = (Style)FindResource("SumCloseBtn");
            // A chip cannot hug the card's corner the way the chrome face did:
            // guarantee real air between the mark and the window's right edge
            // (most themes leave DialogCaptionButtonsMargin at zero).
            var chromeMargin = Application.Current.TryFindResource("DialogCaptionButtonsMargin") is Thickness m
                ? m
                : new Thickness(0);
            close.Margin = new Thickness(chromeMargin.Left, chromeMargin.Top, Math.Max(chromeMargin.Right, 5), chromeMargin.Bottom);
        }

        private void AdjustDigestFont(int direction)
        {
            _digestFont = Math.Clamp(_digestFont + direction, 10, 24);
            DocBox.FontSize = _digestFont;
            AppDataPaths.SetSetting("summary.font", _digestFont.ToString(CultureInfo.InvariantCulture));
            // The rendered document carries the old base face (BuildDocument bakes
            // rtb.FontSize into the FlowDocument and headings set explicit sizes), so a
            // bare FontSize change touched nothing the reader could see. Re-render at
            // the new base: body and headings scale together.
            if (_fullText.Length > 0)
            {
                AiMarkdown.Rebuild(DocBox, _fullText);
            }
        }

        // The generated digest belongs to the reader, not to the window: it is saved
        // with the document and shown again on reopen (word count and all) until the
        // reader generates another one or resets. Empty text clears the saved copy.
        private void SaveDigest()
        {
            try
            {
                AppDataPaths.SetSetting("summary.digest." + _documentId, _fullText);
            }
            catch
            {
                // best-effort
            }
        }

        private void RestoreDigest()
        {
            try
            {
                string text = AppDataPaths.GetSetting("summary.digest." + _documentId) ?? string.Empty;
                if (text.Length == 0)
                {
                    return;
                }

                _fullText = text;
                DocBox.SetValue(AiMarkdown.TextProperty, text);
                StatusText.Text = string.Format(
                    _loc("Str_SummaryCounts"), PageSummarizer.CountWords(text), text.Length);
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>The range the navigator is showing right now, for the page list's
        /// reading-range highlight. False once the window is gone.</summary>
        public bool TryGetVisibleRange(out int first, out int last)
        {
            first = _startPage;
            last = RangeEnd();
            return !_closed && IsVisible;
        }

        /// <summary>The viewer moved to another page (MainWindow pokes this from the
        /// page list's selection changes): the pages-left counter recomputes at once.</summary>
        public void NotifyViewerPageChanged() => UpdatePagesLeft();

        // ------------------------------------------------------------------
        // Placement persistence
        // ------------------------------------------------------------------

        private void RestorePlacement()
        {
            try
            {
                if (TryGetSetting("summary.win.w", out double width) && width >= 320 && width <= 1200)
                {
                    Width = width;
                }

                if (TryGetSetting("summary.win.h", out double height) && height >= 360 && height <= 1600)
                {
                    Height = height;
                }

                if (TryGetSetting("summary.win.left", out double left) && TryGetSetting("summary.win.top", out double top))
                {
                    // DialogChrome.Configure leaves WindowStartupLocation.CenterOwner in
                    // place, and WPF applies the startup location at Show() REGARDLESS of
                    // explicitly set Left/Top - CalculateWindowLocation overwrites them for
                    // CenterOwner and CenterScreen alike. The restored place was being
                    // clobbered on every open, so the window always reopened centered on
                    // the reader instead of where they had dragged it. Manual hands the
                    // position back to the saved coordinates.
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    double vsLeft = SystemParameters.VirtualScreenLeft;
                    double vsTop = SystemParameters.VirtualScreenTop;
                    double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                    double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
                    Left = Math.Clamp(left, vsLeft - 100, Math.Max(vsLeft - 100, vsRight - 200));
                    Top = Math.Clamp(top, vsTop - 20, Math.Max(vsTop - 20, vsBottom - 120));
                }
            }
            catch
            {
                // placement is best-effort
            }
        }

        private void PersistPlacement()
        {
            try
            {
                AppDataPaths.SetSetting("summary.win.w", Width.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("summary.win.h", Height.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("summary.win.left", Left.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("summary.win.top", Top.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>Right-clicking the toolbar's Summary button calls this: the window
        /// returns to its default spot (centered on the owner) - position ONLY, the
        /// size and every reading parameter stay as the reader left them. The saved
        /// coordinates are dropped, so future opens center again too.</summary>
        public void ResetPosition()
        {
            try
            {
                AppDataPaths.SetSetting("summary.win.left", string.Empty);
                AppDataPaths.SetSetting("summary.win.top", string.Empty);

                if (Owner is { } owner)
                {
                    Left = Math.Max(SystemParameters.VirtualScreenLeft,
                        owner.Left + ((owner.ActualWidth - ActualWidth) / 2));
                    Top = Math.Max(SystemParameters.VirtualScreenTop,
                        owner.Top + ((owner.ActualHeight - ActualHeight) / 2));
                }
                else
                {
                    Left = SystemParameters.VirtualScreenLeft
                        + ((SystemParameters.VirtualScreenWidth - ActualWidth) / 2);
                    Top = SystemParameters.VirtualScreenTop
                        + ((SystemParameters.VirtualScreenHeight - ActualHeight) / 2);
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static int ReadIntSetting(string name)
        {
            string? raw = AppDataPaths.GetSetting(name);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }

        private static bool TryGetSetting(string name, out double value)
        {
            string? raw = AppDataPaths.GetSetting(name);
            if (raw != null
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                return true;
            }

            value = 0;
            return false;
        }
    }
}
