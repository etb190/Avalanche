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
// (another arrow press, Start, Reset) is detached by a generation counter
// and its request cancelled - stale continuations cannot repaint the card.
// Closing the window cancels nothing: a generating digest finishes underground
// and saves itself, and a reopened navigator adopts the run it finds there,
// elapsed clock still counting (v1.19.35). A digest that lands while the
// reader is elsewhere can say so out loud: the Ping switch (v1.19.40) rings
// the moment the summary is ready - on by default, remembered across
// sessions. v1.19.41: the ding is the app's own bundled sound, played
// directly, because the OS scheme's notification alias is silent on
// machines whose sound scheme is "No Sounds" - and nothing was heard.
// The reading position survives restarts per document; so do range, word
// ceiling, language and the window's size and place.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Documents;
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

        // The genre dropdown (v1.17.0): the per-book persona for the digest
        // prompt. Tags are the canonical genre ids PageSummarizer understands;
        // labels ride in every Strings file as Str_Genre_*.
        private static readonly string[] GenreChoices =
        {
            "nonfiction_classic", "fiction", "philosophical_fiction",
            "research_papers", "self_help", "law"
        };

        private static readonly Dictionary<string, string> GenreKeySuffix = new()
        {
            ["nonfiction_classic"] = "Nonfiction",
            ["fiction"] = "Fiction",
            ["philosophical_fiction"] = "Philosophical",
            ["research_papers"] = "Research",
            ["self_help"] = "SelfHelp",
            ["law"] = "Law"
        };

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
        // v1.19.32: the recap dial's own config - the quiet recap behind the
        // navigator answers to the same "Recap" choice the settings panel
        // shows, never to the summary's dial.
        private readonly Func<AiProviderConfig> _recapConfigProvider;
        private readonly Func<string, string> _loc;

        private CancellationTokenSource? _cts;
        private bool _generating;
        private string _fullText = string.Empty;
        private bool _flushPending;
        private bool _closed;
        private int _generation;        // bumped by supersede/reset/close: stale continuations can't repaint
        private bool _freshNextRun;     // armed by Reset: the next run must be GENERATED, never replayed from the cache

        private int _rangePages = 20;   // pages per digest: the selected range chip's value
        private int _targetWords = 1000;
        private string _language = "English";
        private string _genre = "nonfiction_classic";
        private int _startPage = 1;     // the anchor: first page of the displayed range
        private bool _editing;          // the field is showing the bare start page for editing
        private double _digestFont = 13;    // the digest's face; the title-bar + and - move it
        private int _runFirstPage = 1;  // the range the current/last digest covered; the
        private int _runLastPage = 1;   // verification badge maps its audit onto these pages
        private string _runModel = "";  // v1.19.33: the brain the current/last digest came
                                        // from - saved beside the digest so a restored card
                                        // names its own writer, not the dial's today-word

        // v1.19.34: where the reader had scrolled the digest - read by the
        // owner when the browser parks the navigator, replayed when the book
        // welcomes it back.
        public double DigestScrollOffset => DocBox.VerticalOffset;

        public void RestoreDigestScroll(double offset)
        {
            if (offset <= 0)
            {
                return;
            }

            // The digest paints in the constructor; the scroll needs a layout
            // pass to exist before it can climb back. One deferred hop at
            // Loaded priority - the same queue the first frame renders on.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () =>
                {
                    try
                    {
                        DocBox.UpdateLayout();
                        DocBox.ScrollToVerticalOffset(offset);
                    }
                    catch
                    {
                        // a scroll that cannot climb is nobody's emergency
                    }
                });
        }

        // The active pass's raw extraction, keyed by the stretch it covers: the
        // floating action popup's Explain serves the author's pages from here
        // and only re-extracts when the range moved or no pass ever ran.
        private string? _cachedRangeRawText;
        private int _cachedRangeFirst, _cachedRangeLast;

        // The 30-second prefetch buffer: the NEXT sequential range, fetched in
        // the background while the reader digests the current one. A ready
        // buffer -> the next arrow paints with zero lag; an in-flight fetch ->
        // the arrow attaches to it; any manual range move discards both.
        private int _postQueueGeneration;   // v1.19.30: an invalidated queue can't repaint or start
        private CancellationTokenSource? _prefetchCts;
        private Task<string>? _prefetchFlight;      // the in-flight fetch
        private string? _prefetchText;              // the completed buffer (null = none)
        private int _prefetchFirst, _prefetchLast;  // the stretch the buffer covers
        private bool _prefetchFromCache;            // v1.19.34: the buffer served the cache,
                                                    // not a generation - no time to claim
        private bool _runFromCache;                 // v1.19.34: the live run served the cache
        private DateTime _prefetchStartedUtc = DateTime.UtcNow;  // the flight's clock
        private TimeSpan? _prefetchDuration;    // v1.19.28: how long the buffer took to generate
        private bool _schedulePrefetchOnIdle;   // v1.19.26: the finally arms the next buffer - SchedulePrefetch refuses a live run
        private bool _digestRestored;           // v1.19.27: a restored digest arms the buffer like a finished run

        // The live run's clock: StartGeneration winds it, the digest spends
        // it into the status line ("took 42s" / "took 1m 12s", v1.19.25).
        private DateTime _runStartedUtc = DateTime.UtcNow;

        // v1.19.35: the live elapsed clock - a DispatcherTimer re-serving the
        // status line with the ticking wall clock while a run generates, and
        // the progress word the clock keeps company.
        private DispatcherTimer? _elapsedTimer;
        private string _progressBase = string.Empty;

        // v1.19.35: the run the reader left behind. Closing the navigator no
        // longer kills a generating digest - the run finishes underground and
        // saves its work. The static slot holds the window whose generation is
        // still running; a reopened navigator adopts it and watches until the
        // digest lands.
        private static SummaryWindow? _orphanRun;
        private SummaryWindow? _orphanOwner;    // the underground run this window watches
        private DispatcherTimer? _orphanPoll;   // the 400ms mirror of that run

        public SummaryWindow(
            MainWindow owner,
            string filePath,
            string documentId,
            int pageCount,
            Func<int> currentPageProvider,
            Func<AiProviderConfig> configProvider,
            Func<AiProviderConfig>? recapConfigProvider,
            Func<string, string> loc)
        {
            InitializeComponent();
            _filePath = filePath;
            _documentId = documentId;
            _pageCount = Math.Max(1, pageCount);
            _currentPageProvider = currentPageProvider;
            _configProvider = configProvider;
            _recapConfigProvider = recapConfigProvider ?? configProvider;
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

            // F belongs to the AI summary windows now - never to the PDF
            // editor (the reader asked for exactly that): while the navigator
            // holds the focus, one press toggles the recap companion - open
            // over the stretch on screen, or put away. Typing in the start
            // field types; F stays a letter there.
            PreviewKeyDown += TryRecapHotkeyFromNavigator;
            // The reader's hands on the digest: bare W/S ease it up/down, bare
            // A/D step the reading range (the arrows' own move). Only while
            // THIS window holds the focus - the editor never hears them - and
            // never with a modifier attached (Shift+W is someone else's chord).
            PreviewKeyDown += TryReadingNavigatorKey;
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
            // The switches ride the navigator row itself: Ping into the slot
            // ahead of Recap (v1.19.40), Recap into the slot left of the back
            // arrow, Discord into the slot right of the forward arrow, each a
            // small space from its neighbours - and the window a full
            // switch-row shorter than the rail-above-the-stepper layout it
            // replaces.
            PingSlot.Content = BuildPingToggle();
            RecapSlot.Content = BuildRecapToggle();
            DiscordSlot.Content = BuildDiscordToggle();
            BufferSlot.Content = BuildBufferToggle();
            // The title bar: the wordmark, two digest-font chips before the close mark,
            // a close mark squared to the wordmark's height, and a hairline under the
            // whole bar separating it from the navigator's body.
            var frame = DialogChrome.Frame(this, owner, "Avalanche", Close, BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    BottomSeparator = true,
                    CloseButtonSize = 24,
                    // The Recap and Discord switches no longer ride the bar's
                    // middle: the title bar's right column (the font chips and
                    // the close mark) is ~140px wide, which pulled the centered
                    // slot ~70px left of the window's true center - the reader's
                    // screenshot showed the pair floating, misaligned with the
                    // stepper below. They live in the body now, in the navigator
                    // row itself - Recap left of the back arrow, Discord right
                    // of the forward arrow (v1.19.15).
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

            // Genre persona: localized labels, canonical id in Tag. The choice is
            // per-book (book.<docId>.genre) - each volume remembers its own genre
            // across sessions and tab switches - while summary.default_genre keeps
            // the reader's latest pick as the default for a book that never chose.
            // The digest prompt AND the cache identity ride on it.
            foreach (string genre in GenreChoices)
            {
                GenreCombo.Items.Add(new ComboBoxItem
                {
                    Content = loc("Str_Genre_" + GenreKeySuffix[genre]),
                    Tag = genre
                });
            }

            GenreCombo.SelectionChanged += (_, _) =>
            {
                if (GenreCombo.SelectedItem is ComboBoxItem item && item.Tag is string genre)
                {
                    _genre = genre;
                    AppDataPaths.SetSetting("book." + _documentId + ".genre", genre);
                    AppDataPaths.SetSetting("summary.default_genre", genre);
                    InvalidatePrefetch();   // the buffer was fetched for the old genre
                }
            };

            LoadPreferences();
            SelectChip(_rangePages);            // fires Checked -> OnRangeChanged -> ShowRange
            SelectCombo(WordsCombo, _targetWords);
            SelectCombo(LangCombo, _language);
            SelectCombo(GenreCombo, _genre);
            ApplyReadingDirection();
            DocBox.FontSize = _digestFont;
            WireActionPopup();                  // the floating action popup over the digest
            RestoreDigest();                    // the last digest of this book, if any
            AdoptOrphanRun();                   // v1.19.35: adopt a run left generating when the window last closed

            // v1.19.30: there is no thirty-second clock anymore. When a digest
            // lands - live or restored - the post-digest queue runs at once:
            // the recap (when Recap mode is on) condenses first with its own
            // words in the status line, then the buffer starts, so the wait the
            // reader could never explain is gone.

            // A digest restored on open reads like a finished run: the card is
            // full and pages still lie ahead, so the buffer arms for it exactly
            // as it would after a live digest. Without this the reopened window
            // never spoke - the reader watched pages left and nothing else, on
            // every open, forever.
            if (_digestRestored)
            {
                SchedulePrefetch();
            }

            Closed += (_, _) =>
            {
                _closed = true;
                SummaryWindow? watching = _orphanOwner;
                StopOrphanWatch(cancel: false);     // the poll dies with the window; the run it watched does not
                StopElapsedClock();
                if (watching is { } live && live._generating)
                {
                    // An adopted underground run is still going: hand it back
                    // to the slot so the next navigator can adopt it in turn.
                    _orphanRun = live;
                }
                else if (watching is null && _generating)
                {
                    // This window's own generation goes underground: it keeps
                    // running - no bump, no cancel - and lands in its own save.
                    _orphanRun = this;
                }
                else
                {
                    _generation++;      // nothing live: a stale continuation can't repaint either
                    try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                }

                InvalidatePrefetch();   // the clock and the flight die with the window
                DismissActionPopup();   // the popup dies with the window
                if (!_generating)
                {
                    // A still-generating card must not save: its text is empty,
                    // and saving it would wipe the book's last digest. The
                    // underground run saves its own digest when it lands.
                    SaveDigest();       // the digest stays with the book across sessions
                }

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

        // The range MOVED - the visible (first, last) pair changed since the
        // window last painted one: the stepper arrows, a keyboard Left/Right
        // or A/D, a retyped anchor, a span chip. The window's first paint and
        // an Escape revert repaint the same pair and stay silent, so the
        // recap answers to real moves only (and to F, which never comes
        // through here).
        public event Action? RangeMoved;
        private (int First, int Last)? _notifiedRange;

        // Raised when the arrows move the reading range (the 1-based first page
        // of the new range): the document follows - a smooth, fast scroll that
        // parks the new stretch's first page at the top of the viewport.
        public event Action<int>? PageNavigationRequested;

        // Raised by the title bar's beaker chip: the reader wants the AI test to
        // probe exactly the stretch on screen. MainWindow opens (or reuses) the
        // tester and starts it on this range - first page through last.
        public event Action<int, int>? TestRangeRequested;

        /// <summary>
        /// Raised when the floating popup's Search fires (v1.19.25) with the
        /// ready-to-open query URL. The window spawns nothing itself: the
        /// app's browser is MainWindow's pane, and the query opens there as
        /// a new tab instead of in the system browser.
        /// </summary>
        public event Action<string>? WebSearchRequested;

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

            // The book's own genre first; a book that never picked one falls back
            // to the reader's latest genre choice anywhere, then to the classic.
            string? genre = AppDataPaths.GetSetting("book." + _documentId + ".genre");
            if (string.IsNullOrEmpty(genre))
            {
                genre = AppDataPaths.GetSetting("summary.default_genre");
            }

            _genre = genre != null && GenreChoices.Contains(genre) ? genre : "nonfiction_classic";

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
            (int, int) pair = (_startPage, RangeEnd());
            if (_notifiedRange is { } seen && seen != pair)
            {
                RangeMoved?.Invoke();
            }

            _notifiedRange = pair;
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
            DismissActionPopup();   // the range moved: the anchor no longer marks the word
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
            DismissActionPopup();   // the range reshaped: the anchor no longer marks the word
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

            StopOrphanWatch(cancel: true);   // a fresh Start retires the underground run this window was watching
            RetireStaleOrphan();    // an invisible run for another book ends when the reader asks elsewhere
            DismissActionPopup();   // a new run resets the card: the popup's anchor is gone
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
            _runStartedUtc = DateTime.UtcNow;
            _progressBase = string.Empty;
            // v1.19.32: the run's own model takes the bottom-right word before
            // its first word lands - the config the run asks is the config named.
            // v1.19.33: the word is remembered with the run, so the saved
            // digest can name the brain that actually wrote it when it comes
            // back (the dial may say something else by then).
            _runModel = _configProvider().Model;
            SetModelLabel(_runModel);   // the status line's "took ..." starts here
            _schedulePrefetchOnIdle = false;
            _fullText = string.Empty;
            // The superseded source is deliberately NOT disposed: the detached loop is
            // still polling its token, and a disposed source can throw from those
            // polls. Garbage collection reclaims it.
            _cts = new CancellationTokenSource();
            _runFromCache = false;   // the new run earns its own verdict
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);
            StartElapsedClock();    // v1.19.35: the elapsed seconds tick in the status line from the first moment

            // Reset armed the fresh-run flag: this run skips the cache lookup so
            // the wiped digest cannot replay. The arm survives failed runs (every
            // retry stays cache-free) and is spent only when a digest lands.
            var request = new SummaryRequest(
                _filePath, _documentId, first, last, _targetWords, _language, _genre,
                BypassCache: _freshNextRun);
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
                                // v1.19.35: the progress word carries the live
                                // clock - the elapsed seconds tick beside it
                                // until the digest lands and the line becomes
                                // the counts + "took" verdict.
                                _progressBase = update.Text;
                                StatusText.Text = _progressBase + ElapsedSuffix();
                            }

                            break;
                        case "delta":
                            AppendDelta(update.Text, gen);
                            break;
                        case "done":
                            if (gen == _generation)
                            {
                                // A digest landed: Reset's fresh-run arm is spent. A run
                                // that errored keeps the arm - its retries stay cache-free.
                                _freshNextRun = false;
                                _runFromCache = update.FromCache;
                                if (update.RawRange.Length > 0)
                                {
                                    // The pass's own unabridged extraction: the floating
                                    // action popup's Explain grounds itself in these pages.
                                    _cachedRangeRawText = update.RawRange;
                                    _cachedRangeFirst = _runFirstPage;
                                    _cachedRangeLast = _runLastPage;
                                }

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
                    StopElapsedClock();
                    // v1.19.35: the voice follows the card's final state here
                    // too. Without this the dots kept pulsing over the finished
                    // digest: UpdateEmptyState ran at "done" while _generating
                    // still held, took the generating branch, and nothing after
                    // the flip ever told the dots to stop.
                    UpdateEmptyState();
                    // v1.19.26: the 30s prefetch clock arms HERE - it never
                    // fired before, because FinishSuccess asked while this
                    // run was still "generating" and SchedulePrefetch rightly
                    // refused it.
                    if (_schedulePrefetchOnIdle)
                    {
                        _schedulePrefetchOnIdle = false;
                        SchedulePrefetch();
                    }
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
            // count remains. v1.19.34: "factual" now also means a cache serve
            // names no generation time - a digest that came out of the range
            // cache in two seconds never took two seconds to GENERATE, and
            // printing its lookup wall clock read as a lie (v1.19.34 report).
            StatusText.Text = VerificationStatusLine()
                + (_runFromCache ? string.Empty : DurationSuffix(DateTime.UtcNow - _runStartedUtc));
            SaveDigest();       // the digest survives the window, the app, the session
            PingSummaryReady(); // v1.19.40: the reader looked away; the ding says it is ready
            _cts?.Dispose();
            _cts = null;
            _schedulePrefetchOnIdle = true;   // v1.19.26: the finally arms it - _generating still holds here
        }

        // ------------------------------------------------------------------
        // The live clock + the underground run (v1.19.35)
        // ------------------------------------------------------------------

        // While a run generates, the status line's progress word gains a
        // ticking elapsed clock - "  |  42s", the same wall clock the finished
        // line reports as "took 42s". The reader asked to SEE the wait, not
        // just be told about it afterwards.
        private string ElapsedSuffix()
            => "  |  " + Features.AI.AiChatText.FormatDuration(DateTime.UtcNow - _runStartedUtc);

        private void StartElapsedClock()
        {
            StopElapsedClock();
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (!_generating)
                {
                    StopElapsedClock();
                    return;
                }

                StatusText.Text = _progressBase + ElapsedSuffix();
            };
            _elapsedTimer.Start();
            StatusText.Text = _progressBase + ElapsedSuffix();
        }

        private void StopElapsedClock()
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
        }

        // A reopened navigator adopts the generation its closed predecessor
        // left running: the card returns to the busy state, the progress word
        // and the elapsed clock pick up where the closed window left them, and
        // a 400ms poll mirrors the underground run until it lands - then the
        // digest, its verification line and the buffer queue arrive exactly as
        // if the window had never been away.
        private void AdoptOrphanRun()
        {
            SummaryWindow? orphan = _orphanRun;
            if (orphan is null || !orphan._generating || !orphan.DocumentPathEquals(_filePath))
            {
                if (orphan is not null && !orphan._generating)
                {
                    _orphanRun = null;   // the run ended while nobody watched; its digest is already saved
                }

                return;
            }

            _orphanOwner = orphan;
            _generating = true;
            _runStartedUtc = orphan._runStartedUtc;
            _runModel = orphan._runModel;
            _runFirstPage = orphan._runFirstPage;
            _runLastPage = orphan._runLastPage;
            _runFromCache = false;
            _fullText = orphan._fullText;
            _flushPending = false;
            _progressBase = orphan._progressBase;
            DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
            Overlay(null);                  // the busy voice: dots over the card
            SetBusy(true);
            SetModelLabel(_runModel);
            StartElapsedClock();
            _orphanPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _orphanPoll.Tick += (_, _) => PollOrphanRun();
            _orphanPoll.Start();
        }

        private void PollOrphanRun()
        {
            SummaryWindow? orphan = _orphanOwner;
            if (orphan is null)
            {
                StopOrphanWatch(cancel: false);
                return;
            }

            if (!orphan._generating)
            {
                // The underground run is over: take what it produced.
                if (ReferenceEquals(_orphanRun, orphan))
                {
                    _orphanRun = null;
                }

                StopOrphanWatch(cancel: false);
                _generating = false;
                SetBusy(false);
                StopElapsedClock();
                if (orphan._fullText.Length > 0)
                {
                    _fullText = orphan._fullText;
                    _runFromCache = orphan._runFromCache;
                    DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                    StatusText.Text = VerificationStatusLine()
                        + (_runFromCache ? string.Empty : DurationSuffix(DateTime.UtcNow - _runStartedUtc));
                    UpdateEmptyState();
                    SchedulePrefetch();     // the landing arms the queue, as a live landing would
                }
                else
                {
                    // It ended in notext or error: the verdict the closed
                    // window painted on itself is the verdict this card shows.
                    string verdict = orphan.OverlayText.Visibility == Visibility.Visible
                        ? orphan.OverlayText.Text
                        : string.Empty;
                    Overlay(verdict);       // an empty verdict restores the invite
                }

                return;
            }

            // Still running: mirror the progress word and keep the clock honest.
            _progressBase = orphan._progressBase;
            StatusText.Text = _progressBase + ElapsedSuffix();
        }

        // The slot's leftover: a run for a book nobody is watching. A fresh
        // Start retires it - the reader's newest ask owns the one generation
        // lane (PageSummarizer's gate), and an invisible digest has no right
        // to make the visible one wait behind it.
        private static void RetireStaleOrphan()
        {
            if (_orphanRun is { } orphan && orphan._generating)
            {
                orphan._generating = false;
                orphan._generation++;           // its continuations lose the right to repaint
                try { orphan._cts?.Cancel(); } catch (ObjectDisposedException) { }
            }

            _orphanRun = null;
        }

        // Stop watching the underground run. cancel: true also puts the run
        // down (a fresh Start or Reset owns the card now); cancel: false lets
        // it finish into its own save.
        private void StopOrphanWatch(bool cancel)
        {
            _orphanPoll?.Stop();
            _orphanPoll = null;
            SummaryWindow? orphan = _orphanOwner;
            _orphanOwner = null;
            if (cancel && orphan is not null && orphan._generating)
            {
                orphan._generating = false;     // the card it was painting belongs to this window now
                orphan._generation++;           // its continuations lose the right to repaint
                try { orphan._cts?.Cancel(); } catch (ObjectDisposedException) { }
                if (ReferenceEquals(_orphanRun, orphan))
                {
                    _orphanRun = null;
                }
            }
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

        // v1.19.25: the generation's wall clock, appended right after the
        // word count the status line ends with - "  |  took 42s", minutes
        // and seconds past the full minute.
        private string DurationSuffix(TimeSpan elapsed)
            => "  |  " + string.Format(_loc("Str_SummaryTook"), Features.AI.AiChatText.FormatDuration(elapsed));

        // v1.19.32: the bottom-right word - "Model: {0}" in the reader's
        // language, or silence when there is nothing on screen to name.
        private void SetModelLabel(string? model)
        {
            ModelText.Text = string.IsNullOrWhiteSpace(model)
                ? string.Empty
                : string.Format(_loc("Str_AiModelUsed"), model);
        }

        // Reset: the fourth generation owner. It stops any live run, clears the
        // card and returns the navigator to idle/ready - and NOTHING else: the
        // selected range stays exactly where the reader left it, waiting for
        // Start. A reset that also yanked the anchor back to page one would
        // punish a reader who only wanted a clean slate of TEXT, not of place.
        private void ResetAll()
        {
            StopOrphanWatch(cancel: true);   // a reset ends the underground run too, if one was watched
            _generation++;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _generating = false;
            _cts = null;
            DismissActionPopup();   // a reset empties the card: the popup goes too
            _fullText = string.Empty;
            _flushPending = false;
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            StatusText.Text = string.Empty;
            SetModelLabel(null);    // no digest on screen, no model to name
            _runModel = string.Empty;
            InvalidatePrefetch();   // the reset was manual: the buffer goes too
            SaveDigest();       // the reset was the reader's action: forget the digest
            // The wipe is real, not cosmetic: the range's cached digests die with
            // the card - both the range on screen and the range the deleted digest
            // was generated from (they differ when the anchor moved after the run).
            // Start would otherwise replay the very digest the reader deleted. The
            // fresh-run arm covers a lost wipe race either way.
            _freshNextRun = true;
            string wipeDoc = _documentId;
            int wipeFirst = _startPage;
            int wipeLast = RangeEnd();
            int runFirst = _runFirstPage, runLast = _runLastPage;
            _ = Task.Run(() =>
            {
                SummaryCache.DeleteRange(wipeDoc, wipeFirst, wipeLast);
                if (runFirst != wipeFirst || runLast != wipeLast)
                {
                    SummaryCache.DeleteRange(wipeDoc, runFirst, runLast);
                }
            });
            SetBusy(false);
        }

        // ------------------------------------------------------------------
        // The digest area's voice (v1.19.34): the sidechat's empty state,
        // mirrored - the robot, a title and a nudge while the card holds
        // nothing, three pulsing dots while a run generates. The panel is a
        // centered child of the digest grid, so every resize recenters it
        // through the layout system itself - no size is measured by hand.
        // ------------------------------------------------------------------

        private System.Windows.Media.Animation.Storyboard? _dotsStory;

        private void UpdateEmptyState()
        {
            try
            {
                bool overlaySpeaking = OverlayText.Visibility == Visibility.Visible;
                bool generating = _generating;
                bool hasDigest = _fullText.Length > 0;

                if (overlaySpeaking || (hasDigest && !generating))
                {
                    // A message owns the card, or a digest fills it: the voice
                    // yields. (A generating card with text already painted -
                    // the streaming path - keeps quiet too.)
                    EmptyState.Visibility = Visibility.Collapsed;
                    SetDotsRunning(false);
                    return;
                }

                EmptyState.Visibility = Visibility.Visible;
                if (generating)
                {
                    // The wait: dots under the dimmed robot, the title and the
                    // nudge stepped aside.
                    EmptyTitle.Visibility = Visibility.Collapsed;
                    EmptySubtitle.Visibility = Visibility.Collapsed;
                    BusyDots.Visibility = Visibility.Visible;
                    SetDotsRunning(true);
                }
                else
                {
                    // The invite: the full greeting over an empty card.
                    EmptyTitle.Visibility = Visibility.Visible;
                    EmptySubtitle.Visibility = Visibility.Visible;
                    BusyDots.Visibility = Visibility.Collapsed;
                    SetDotsRunning(false);
                }
            }
            catch
            {
                // a voice that cannot speak must never break the window
            }
        }

        // Three dots, each fading in and out in turn: opacity 0.25 -> 1 ->
        // 0.25 over 1.2s, the second dot 0.2s behind, the third 0.4s. One
        // storyboard, RepeatForever, restarted only when the dots wake.
        private void SetDotsRunning(bool run)
        {
            if (run && _dotsStory is null)
            {
                var story = new System.Windows.Media.Animation.Storyboard();
                Ellipse[] dots = { BusyDot1, BusyDot2, BusyDot3 };
                for (int i = 0; i < dots.Length; i++)
                {
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1.0,
                        new System.Windows.Duration(TimeSpan.FromSeconds(0.6)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromSeconds(i * 0.2)
                    };
                    System.Windows.Media.Animation.Storyboard.SetTarget(anim, dots[i]);
                    System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim,
                        new PropertyPath(OpacityProperty));
                    story.Children.Add(anim);
                }
                _dotsStory = story;
            }

            if (_dotsStory is null)
            {
                return;
            }

            if (run)
            {
                _dotsStory.Begin(this, true);
            }
            else
            {
                _dotsStory.Stop(this);
                foreach (Ellipse dot in new[] { BusyDot1, BusyDot2, BusyDot3 })
                {
                    dot.Opacity = 1.0;
                }
            }
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
            GenreCombo.IsEnabled = !busy;
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
            // v1.19.34: whatever the card just became, the voice follows.
            UpdateEmptyState();
        }

        // ------------------------------------------------------------------
        // The post-digest queue (v1.19.30): recap first, then the buffer
        // ------------------------------------------------------------------

        // The moment a digest lands - a fresh run, a served buffer, a restored
        // card - the navigator works for the pages ahead. With Recap mode on,
        // the stretch on screen condenses FIRST (its "generating" and "done"
        // words live in the same status line the buffer speaks through), and
        // the buffer for the NEXT stretch starts the moment the recap is done;
        // with Recap off, the buffer starts right away. No clock, no
        // unexplained wait. Any manual range move or new run invalidates the
        // queue mid-flight, and a queue that wakes up stale just stands down.

        private void SchedulePrefetch()
        {
            InvalidatePrefetch();
            if (_closed || _generating || RangeEnd() >= _pageCount)
            {
                return;     // the book has no next stretch (or a run is already live)
            }

            int queueGen = _postQueueGeneration;
            _ = RunPostDigestQueueAsync(queueGen);
        }

        private async Task RunPostDigestQueueAsync(int queueGen)
        {
            if (RecapController.Enabled)
            {
                int recapFirst = _runFirstPage > 0 ? _runFirstPage : _startPage;
                int recapLast = _runLastPage >= recapFirst ? _runLastPage : RangeEnd();
                BufferStatusText.Text = _loc("Str_SummaryRecapBusy");
                bool recapOk = await Features.Summary.RecapController.TryCondenseQuietlyAsync(
                    _filePath, _pageCount, recapFirst, recapLast, _recapConfigProvider, _loc);
                if (_closed || _generating || queueGen != _postQueueGeneration)
                {
                    return;   // the reader moved on while the recap condensed
                }
                // "is done" is a word the success earns; a failed condensation
                // says nothing and lets the buffer's own words follow at once.
                // The word lingers a breath before the buffer's take over - a
                // status that flashes past is a status never read.
                if (recapOk)
                {
                    BufferStatusText.Text = _loc("Str_SummaryRecapDone");
                    await Task.Delay(1500);
                    if (_closed || _generating || queueGen != _postQueueGeneration)
                    {
                        return;
                    }
                }
            }

            if (_closed || _generating || queueGen != _postQueueGeneration)
            {
                return;
            }
            // v1.19.34: the buffer's own switch. Off, the prefetch never
            // starts - the recap above keeps its own toggle's mind, and the
            // reader who silenced the buffer hears nothing more from it.
            if (!BufferEnabled)
            {
                return;
            }
            StartPrefetch();
        }

        // The buffer's persisted choice ("summary.buffer.enabled"): ON unless
        // the reader turned it off, remembered across app closing and opening
        // the same way the Discord switch remembers - its own setting, read
        // wherever the buffer would arm, no window needed to answer.
        internal static bool BufferEnabled
        {
            get
            {
                try
                {
                    return AppDataPaths.GetSetting("summary.buffer.enabled") != "0";
                }
                catch
                {
                    return true;
                }
            }
        }

        private static void SetBufferEnabled(bool on)
        {
            try
            {
                AppDataPaths.SetSetting("summary.buffer.enabled", on ? "1" : "0");
            }
            catch
            {
                // best-effort persistence; a read-only disk must not flip a switch
            }
        }

        // The ping's persisted choice ("summary.ping.enabled"): ON unless the
        // reader turned it off, remembered across app closing and opening the
        // same way the buffer switch remembers (v1.19.40). Read wherever a
        // digest lands - including underground, where the window is closed and
        // nobody is watching - so the choice holds without this window awake.
        internal static bool PingEnabled
        {
            get
            {
                try
                {
                    return AppDataPaths.GetSetting("summary.ping.enabled") != "0";
                }
                catch
                {
                    return true;
                }
            }
        }

        private static void SetPingEnabled(bool on)
        {
            try
            {
                AppDataPaths.SetSetting("summary.ping.enabled", on ? "1" : "0");
            }
            catch
            {
                // best-effort persistence; a read-only disk must not flip a switch
            }
        }

        // v1.19.41: the ping itself - the app's own bundled ding, played
        // directly the moment a digest lands. v1.19.40 borrowed the OS
        // scheme's notification sound (SystemSounds.Asterisk), and the floor
        // report came back flat: nothing was ever heard. A machine whose
        // sound scheme is "No Sounds" - or whose Asterisk event lost its
        // mapping - plays exactly nothing, silently. So the sound now
        // travels with the app: a half-second two-tone ding embedded as a
        // resource, handed to SoundPlayer once and replayed from memory.
        // No scheme, no registry, no theme can silence it; a machine that
        // cannot play at all stays silent instead of throwing. Every
        // landing rings - a serve from the range cache is a ready summary
        // all the same.
        private static System.IO.MemoryStream? _pingStream;   // kept alive: SoundPlayer re-reads it on every play
        private static System.Media.SoundPlayer? _pingPlayer;

        private static void PingSummaryReady()
        {
            if (!PingEnabled) return;
            try
            {
                if (_pingPlayer is null)
                {
                    var sri = Application.GetResourceStream(
                        new Uri("pack://application:,,,/Resources/ping.wav"));
                    if (sri?.Stream is null) return;
                    _pingStream = new System.IO.MemoryStream();
                    sri.Stream.CopyTo(_pingStream);
                    _pingStream.Position = 0;
                    _pingPlayer = new System.Media.SoundPlayer(_pingStream);
                    _pingPlayer.Load();
                }

                _pingPlayer.Play();
            }
            catch
            {
                // no audio device, no sound - never a crash for a courtesy
            }
        }

        // The buffer switch: the Recap and Discord switches' exact twin - the
        // same iOS style, the same press-not-click flip taken in the tunnel -
        // with a bold "Buffer" label on its left, the pair riding the
        // navigator row right of the Discord pair (v1.19.34). Checked is the
        // persisted state; unchecked silences the prefetch until it is
        // turned back on.
        private FrameworkElement BuildBufferToggle()
        {
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("TestModeToggle"),
                IsChecked = BufferEnabled,
                ToolTip = _loc("Str_TT_BufferToggle")
            };
            toggle.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                toggle.IsChecked = toggle.IsChecked != true;
            };
            toggle.Checked += (_, _) => SetBufferEnabled(true);
            toggle.Unchecked += (_, _) => SetBufferEnabled(false);

            var label = new TextBlock
            {
                Text = _loc("Str_Lbl_BufferToggle"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = _loc("Str_TT_BufferToggle")
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(label);
            row.Children.Add(toggle);
            return row;
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
            _prefetchStartedUtc = DateTime.UtcNow;
            _prefetchDuration = null;
            _prefetchText = null;
            _prefetchCts = new CancellationTokenSource();
            CancellationToken ct = _prefetchCts.Token;
            var request = new SummaryRequest(
                _filePath, _documentId, first, last, _targetWords, _language, _genre, BypassCache: false);
            AiProviderConfig config = _configProvider();
            Func<string, string> loc = _loc;
            _prefetchFromCache = false;
            // v1.19.25: the bottom-right word - the buffer is generating.
            BufferStatusText.Text = _loc("Str_SummaryBufferBusy");
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
                            // v1.19.34: remember whether this digest was a
                            // cache serve - the "took" word stays honest.
                            _prefetchFromCache = update.FromCache;
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
                t =>
                {
                    _prefetchText = t.IsCompletedSuccessfully ? t.Result : null;
                    // v1.19.28: the buffered stretch's own wall clock - the
                    // word count the reader sees when they move to it carries
                    // "took Ns" beside it, exactly where a fresh run puts it.
                    _prefetchDuration = t.IsCompletedSuccessfully
                        ? DateTime.UtcNow - _prefetchStartedUtc
                        : null;
                    // v1.19.25: "ready" when the digest landed. v1.19.27: a
                    // dead flight says so instead of going silent - a word
                    // that simply vanished read as a feature that never ran.
                    // Cancellation stays silent: the invalidation that killed
                    // the flight (navigation, a new run) owns the word.
                    BufferStatusText.Text = t.IsCompletedSuccessfully
                        ? _loc("Str_SummaryBufferReady")
                        : t.IsFaulted ? _loc("Str_SummaryBufferFailed")
                        : string.Empty;
                },
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
                DismissActionPopup();   // the stretch changed: the anchor is stale
                Overlay(null);
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                StatusText.Text = VerificationStatusLine()
                    + (_prefetchFromCache || _prefetchDuration is not TimeSpan bufferedTook
                        ? string.Empty
                        : DurationSuffix(bufferedTook));   // v1.19.28: a real generation says
                                                           // how long it took; v1.19.34: a
                                                           // cache serve claims nothing
                StopElapsedClock();
                UpdateEmptyState();   // the dots stand down: a digest fills the card
                SaveDigest();
                InvalidatePrefetch();
                SchedulePrefetch();   // v1.19.26: the served stretch arms the next buffer
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
            DismissActionPopup();   // the digest area is about to repaint
            // v1.19.35: the attached flight gets the live clock too, wound to
            // the flight's own start - the elapsed counts the real wait.
            _runStartedUtc = _prefetchStartedUtc;
            _progressBase = string.Format(
                _loc("Str_SummaryPreparing"), _prefetchFirst, _prefetchLast);
            StartElapsedClock();
            StatusText.Text = _progressBase + ElapsedSuffix();
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
                    StopElapsedClock();
                    UpdateEmptyState();     // the dots stand down with the flight
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
            StopElapsedClock();
            string? text = flight is Task<string> typed && typed.IsCompletedSuccessfully
                ? typed.Result
                : _prefetchText;
            if (!string.IsNullOrEmpty(text))
            {
                _fullText = text;
                _runFirstPage = _prefetchFirst;
                _runLastPage = _prefetchLast;
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                StatusText.Text = VerificationStatusLine()
                    + (_prefetchFromCache
                        ? string.Empty
                        : DurationSuffix(DateTime.UtcNow - _prefetchStartedUtc));
                SaveDigest();
                UpdateEmptyState();
            }
            else
            {
                Overlay(_loc("Str_SummaryNoText"));
            }

            InvalidatePrefetch();
            SchedulePrefetch();   // v1.19.26: this stretch landed - the next buffer arms
        }

        // Kill the clock, the flight and the buffer. Every manual navigation,
        // parameter change, reset, new run and close funnels through here, so
        // stale text can never surface.
        private void InvalidatePrefetch()
        {
            _postQueueGeneration++;   // v1.19.30: a queue caught mid-recap stands down
            try { _prefetchCts?.Cancel(); } catch (ObjectDisposedException) { }
            _prefetchCts?.Dispose();
            _prefetchCts = null;
            _prefetchFlight = null;
            _prefetchText = null;
            _prefetchDuration = null;
            _prefetchFirst = 0;
            _prefetchLast = 0;
            BufferStatusText.Text = string.Empty;
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

        // The Ping switch: the Recap switch's exact twin - the same iOS style
        // (the 40px track, the 16px white thumb gliding 18px in 150ms, the
        // style mirrored verbatim in this window's resources), the same
        // press-not-click flip taken in the tunnel so no stray drag ever turns
        // a tap into a window move - with a bold "Ping" label on its right,
        // the pair riding the navigator row ahead of Recap's (v1.19.40).
        // Checked is the persisted state; a digest that lands while checked
        // rings the system notification sound, wherever the reader went.
        private FrameworkElement BuildPingToggle()
        {
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("TestModeToggle"),
                IsChecked = PingEnabled,
                ToolTip = _loc("Str_TT_Ping")
            };
            toggle.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                toggle.IsChecked = toggle.IsChecked != true;
            };
            toggle.Checked += (_, _) => SetPingEnabled(true);
            toggle.Unchecked += (_, _) => SetPingEnabled(false);

            var label = new TextBlock
            {
                Text = _loc("Str_Lbl_Ping"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = _loc("Str_TT_Ping")
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

        // The Recap switch: the AI tester's iOS-style toggle (the exact same
        // 40px track, the same 16px white thumb gliding 18px in 150ms - the
        // style is mirrored verbatim in this window's resources) beside a bold
        // "Recap" label. The pair rides the body's toggle rail, directly
        // above the range stepper it aligns with. Checked is the
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

        // The Discord switch: the Recap switch's exact twin - the same iOS
        // style (the 40px track, the 16px white thumb gliding 18px in 150ms,
        // the style mirrored verbatim in this window's resources), the same
        // press-not-click flip taken in the tunnel so no stray drag ever
        // turns a tap into a window move - with a bold "Discord" label on
        // its left, the pair riding the navigator row right of the forward
        // arrow: the two switch pairs bookend the stepper, the Recap pair's
        // label facing in from the left, the Discord pair's from the right
        // (v1.19.15). Checked the native IPC presence on and broadcasts the
        // book on screen at once; unchecked clears the profile immediately.
        // The state persists ("discord.rpc.enabled", default on) and the
        // feature answers even while this window is closed.
        private FrameworkElement BuildDiscordToggle()
        {
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("TestModeToggle"),
                IsChecked = Avalanche.Features.Discord.DiscordRpcController.Enabled,
                ToolTip = _loc("Str_TT_DiscordRpc")
            };
            toggle.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                toggle.IsChecked = toggle.IsChecked != true;
            };
            toggle.Checked += (_, _) => Avalanche.Features.Discord.DiscordRpcController.SetEnabled(true);
            toggle.Unchecked += (_, _) => Avalanche.Features.Discord.DiscordRpcController.SetEnabled(false);

            var label = new TextBlock
            {
                Text = _loc("Str_Lbl_DiscordRpc"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = _loc("Str_TT_DiscordRpc")
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(label);
            row.Children.Add(toggle);
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
                // The exact pages this digest was generated from ride beside the
                // text: a restored digest must explain itself from the pages
                // that actually produced it, never from the 1/1 defaults.
                AppDataPaths.SetSetting(
                    "summary.digest." + _documentId + ".first",
                    _runFirstPage.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting(
                    "summary.digest." + _documentId + ".last",
                    _runLastPage.ToString(CultureInfo.InvariantCulture));
                // v1.19.33: and the brain that wrote it. A digest without a
                // run behind it (the reset's empty wipe) clears the word -
                // a restored card must never inherit a stale writer.
                AppDataPaths.SetSetting(
                    "summary.digest." + _documentId + ".model",
                    _fullText.Length > 0 ? _runModel : string.Empty);
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

                // The pages that produced this digest come back with it. A
                // restored digest used to leave the run range at the 1/1
                // defaults, so Explain extracted only page 1 - usually an
                // un-OCR'd cover leaf reading as "[p. 1]" - and the model had
                // nothing real to stand on. Saved first/last win; a digest
                // saved before ranges were persisted falls back to the range
                // on screen.
                if (int.TryParse(
                        AppDataPaths.GetSetting("summary.digest." + _documentId + ".first"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int savedFirst)
                    && savedFirst >= 1 && savedFirst <= _pageCount)
                {
                    _runFirstPage = savedFirst;
                }
                else
                {
                    _runFirstPage = _startPage;
                }

                if (int.TryParse(
                        AppDataPaths.GetSetting("summary.digest." + _documentId + ".last"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int savedLast)
                    && savedLast >= _runFirstPage && savedLast <= _pageCount)
                {
                    _runLastPage = savedLast;
                }
                else
                {
                    _runLastPage = Math.Min(_startPage + _rangePages - 1, _pageCount);
                }

                _fullText = text;
                DocBox.SetValue(AiMarkdown.TextProperty, text);
                StatusText.Text = string.Format(
                    _loc("Str_SummaryCounts"), PageSummarizer.CountWords(text), text.Length);
                // v1.19.32: the restored card names its model as well.
                // v1.19.33: it names the model that actually WROTE it - the
                // one saved beside the digest - not whatever the dial says
                // today; only digests saved before this rode the dial's word.
                string savedModel = AppDataPaths.GetSetting("summary.digest." + _documentId + ".model")
                    ?? string.Empty;
                SetModelLabel(savedModel.Length > 0 ? savedModel : _configProvider().Model);
                _digestRestored = true;   // the constructor arms the buffer once the clock exists
            }
            catch
            {
                // best-effort
            }
        }

        // ------------------------------------------------------------------
        // The floating action popup (v1.18.0)
        // ------------------------------------------------------------------

        // Clicking a word in the digest - or pressing Shift over a highlighted
        // passage - anchors a narrow popup over that spot with three green,
        // string-only buttons: Search (the system browser), Define (a concise
        // lexical pass) and Explain (grounded in the raw pages of the active
        // range, never in the summary). The popup dismisses on an outside click,
        // a scroll, Escape and every range or generation reset, while a click on
        // a fresh word or passage re-anchors it on the spot; the open itself is
        // deferred off the input event, because opening a Popup mid-input makes
        // WPF's capture establishment trip over "Invalid window handle" - and a
        // popup that never captured cannot see the outside clicks that dismiss
        // it. A request that a dismissal cancels can never repaint the card
        // (generation counter). The popup is built in code like the window's
        // other composed chrome, so the digest's XAML stays untouched; the word
        // under the pointer wears a dark-green plate an adorner paints above the
        // text - padding included - that can never shift or reflow a neighbor,
        // and the word the popup opened on stays pinned under the same green
        // plate the hover wears - glyphs readable, never two layers - until
        // the popup closes or a newer word or passage takes the anchor.

        private Popup? _actionPopup;                    // built lazily on first open
        private Border? _actionRoot;                    // the rounded dark-glass face
        private TextBlock? _actionHeaderText;           // the target word / truncated excerpt
        private Button? _actionSearchBtn;
        private Button? _actionDefineBtn;
        private Button? _actionExplainBtn;
        private Button? _actionCopyBtn;
        private Border? _actionCard;                    // the collapsible result card
        private TextBlock? _actionStatus;               // the loading line
        private TextBlock? _actionError;                // the provider detail under an error
        private RichTextBox? _actionResultBox;          // the AiMarkdown-rendered answer
        private CancellationTokenSource? _actionCts;    // cancelled when the popup dismisses
        private int _actionGen;                         // bumped on dismiss/supersede
        private string _actionTarget = string.Empty;    // the cleaned text the actions run on
        private Popup? _actionOpenPending;              // the open deferred off the input event
        private DigestWordHighlightAdorner? _wordHighlights;  // the green per-word digest highlights

        private void WireActionPopup()
        {
            // The word click. PreviewMouseLeftButtonUp: the editor has not yet
            // finalized this click's selection, so a drag's result and a clean
            // click's empty selection both read their pre-click face.
            DocBox.PreviewMouseLeftButtonUp += DocBox_MouseUpForPopup;
            // Shift over a highlighted passage anchors the popup over the whole
            // selection; Escape dismisses the popup before the window's close.
            DocBox.PreviewKeyDown += DocBox_KeyDownForPopup;
            // Any scroll of the digest dismisses: the anchor lives in the
            // viewport and a scrolled-away word is no longer under the popup.
            DocBox.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => DismissActionPopup()));
            // The green word highlights ride their own adorner, attached once the
            // digest's surface is live and detached when it leaves the tree.
            DocBox.Loaded += (_, _) => AttachWordHighlights();
            DocBox.Unloaded += (_, _) => DetachWordHighlights();
        }

        private void AttachWordHighlights()
        {
            if (_wordHighlights != null || _closed)
            {
                return;
            }

            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(DocBox);
            if (layer is null)
            {
                return;
            }

            _wordHighlights = new DigestWordHighlightAdorner(DocBox);
            layer.Add(_wordHighlights);
        }

        private void DetachWordHighlights()
        {
            _wordHighlights?.Detach();
            _wordHighlights = null;
        }

        private void DocBox_KeyDownForPopup(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.LeftShift or Key.RightShift)
            {
                if (!string.IsNullOrWhiteSpace(DocBox.Selection.Text))
                {
                    // The reader Shift-selected a passage: anchor the popup over
                    // the whole highlighted stretch. Shift itself stays native.
                    Rect anchor = DocBox.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
                    OpenActionPopup(CleanPopupTarget(DocBox.Selection.Text), anchor);
                }
            }
            else if (e.Key == Key.Escape && _actionPopup is { IsOpen: true })
            {
                e.Handled = true;   // the popup, not the window, eats this Escape
                DismissActionPopup();
            }
        }

        private void DocBox_MouseUpForPopup(object sender, MouseButtonEventArgs e)
        {
            // A click on a word or a Shift passage re-anchors the popup over the
            // fresher target - including the very click that dismissed the old
            // one (the outside down closes, this up re-opens); a click off the
            // text isolates no word and the dismissal stands.
            if (!DocBox.Selection.IsEmpty)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    // The mouse half of the Shift selection: a selection
                    // completed with either Shift key held.
                    Rect anchor = DocBox.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
                    OpenActionPopup(CleanPopupTarget(DocBox.Selection.Text), anchor);
                }

                // A drag selection without Shift belongs to the reader alone.
                return;
            }

            // The plain click. TextPointer hit-testing - no Inline wrapping, so
            // selection, clipboard copy and rendering keep their native speed.
            Point pt = e.GetPosition(DocBox);
            TextPointer? hit = DocBox.GetPositionFromPoint(pt, snapToText: true);
            if (hit is null || !TryIsolateWord(hit, out TextPointer? start, out TextPointer? end, out string? word))
            {
                return;     // off the text, or whitespace/punctuation: no word to act on
            }

            Rect rect = start!.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty)
            {
                return;
            }

            // The click's own pointers ride along: when the popup opens, the
            // word is pinned under its green plate until the popup closes or
            // a newer word or passage takes the anchor.
            OpenActionPopup(CleanPopupTarget(word!), rect, start, end);
        }

        // Whitespace and punctuation bound a clicked word (the spec's own
        // definition); every other character keeps the word whole. The walk
        // crosses run edges - bold segments, citation marks - because a word
        // can span them; the pass cap keeps a pathological document looping.
        private static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && !char.IsPunctuation(c);

        private static bool TryIsolateWord(TextPointer hit, out TextPointer? start, out TextPointer? end, out string? word)
        {
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
                    break;      // a boundary sits right before the word
                }

                left = left.GetPositionAtOffset(i - run.Length) ?? left;
                if (i > 0)
                {
                    break;      // the boundary is inside this run
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
                    break;      // a boundary sits right after the word
                }

                right = right.GetPositionAtOffset(i) ?? right;
                if (i < run.Length)
                {
                    break;      // the boundary is inside this run
                }
            }

            start = left;
            end = right;
            word = left != right ? new TextRange(left, right).Text.Trim() : null;
            return !string.IsNullOrEmpty(word);
        }

        // Collapses the selection's newlines and whitespace runs into single
        // spaces so a multi-line phrase reads - and searches - as one query.
        private static string CleanPopupTarget(string raw) =>
            System.Text.RegularExpressions.Regex.Replace(raw ?? string.Empty, @"\s+", " ").Trim();

        private static string TruncateForQuery(string text) =>
            text.Length <= 200 ? text : text[..200].TrimEnd();

        private static string TruncateForDefine(string text) =>
            text.Length <= 160 ? text : text[..160].TrimEnd();

        private static string TruncateForExplain(string text) =>
            text.Length <= 4000 ? text : text[..4000].TrimEnd();

        private void OpenActionPopup(string target, Rect anchor, TextPointer? pinStart = null, TextPointer? pinEnd = null)
        {
            if (_closed || target.Length == 0 || anchor.IsEmpty)
            {
                return;
            }

            DismissActionPopup();   // a second open re-anchors cleanly
            _actionTarget = target;

            Popup popup = _actionPopup ??= BuildActionPopup();
            _actionHeaderText!.Text = target.Length > 44 ? target[..44] + "…" : target;
            SetPopupStatus(string.Empty);
            _actionError!.Visibility = Visibility.Collapsed;
            _actionError!.Text = string.Empty;
            _actionResultBox!.Visibility = Visibility.Collapsed;
            _actionResultBox!.SetValue(AiMarkdown.TextProperty, string.Empty);
            if (_actionCopyBtn is { } copy)
            {
                copy.Visibility = Visibility.Collapsed;
            }

            SetActionButtonsEnabled(true);
            _actionGen++;

            // Above the word by default; near the viewport's top, below. The
            // inflated rect buys an 8px air gap without moving the anchor.
            popup.Placement = anchor.Y < 240 ? PlacementMode.Bottom : PlacementMode.Top;
            popup.PlacementRectangle = anchor.Y < 240
                ? new Rect(anchor.X, anchor.Y, Math.Max(anchor.Width, 40), anchor.Height + 8)
                : new Rect(anchor.X, anchor.Y - 8, Math.Max(anchor.Width, 40), anchor.Height + 8);

            // IsOpen is set OFF the input event. Opening synchronously inside the
            // Shift KeyDown (or the mouse-up that bred it) makes WPF establish the
            // popup's mouse capture mid-input - ScreenToClient then trips over
            // "Invalid window handle" (Win32Exception), the capture never lands,
            // and an uncaptured popup can no longer see the outside clicks that
            // should dismiss it. The one-tick deferral lets the input stage
            // finish first; a dismissal or a newer open in between (the pending
            // handle plus the generation) aborts the stale one, and a popup that
            // still cannot open is skipped rather than allowed to crash.
            _actionOpenPending = popup;
            int gen = _actionGen;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_closed || _actionOpenPending != popup || _actionGen != gen)
                {
                    return;     // dismissed, reset, or superseded in the same tick
                }

                _actionOpenPending = null;
                try
                {
                    // The clicked word keeps its green plate while the popup
                    // lives: the pin rides the exact pointers the click
                    // isolated. A passage open pins nothing - the native
                    // selection already marks the passage - and drops any
                    // word pin a previous open left behind.
                    if (pinStart is { } ps && pinEnd is { } pe && _wordHighlights is { } highlights)
                    {
                        highlights.Pin(ps, pe);
                    }
                    else
                    {
                        _wordHighlights?.ClearPin();
                    }

                    popup.IsOpen = true;
                    PlayActionPopupOpen(_actionRoot!);
                }
                catch
                {
                    // A decoration: a popup that cannot open never takes the
                    // window down - and never leaves a pin behind either.
                    _wordHighlights?.ClearPin();
                }
            }));
        }

        private static void PlayActionPopupOpen(UIElement root)
        {
            // A short fade-and-rise: WindowFx.PlayOpenPop targets windows, so
            // the popup's border carries its own two-sided entrance here.
            var slide = new TranslateTransform(0, 6);
            root.RenderTransform = slide;
            root.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                0, 1, TimeSpan.FromMilliseconds(120)));
            slide.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }

        private Popup BuildActionPopup()
        {
            // Header: the target (word or truncated excerpt) - no close
            // chip; an outside click, Escape, or a scroll dismisses the
            // popup. The band hugs the text and the hairline rides close,
            // so the popup fits what is actually in it.
            _actionHeaderText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 6, 12, 6),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(_actionHeaderText);

            var hairline = new Rectangle { Height = 1, Margin = new Thickness(10, 0, 10, 2), Opacity = 0.85 };
            hairline.SetResourceReference(Shape.FillProperty, "CardBorderBrush");

            _actionSearchBtn = MakePopupActionBtn("search", "Str_SummaryPopupSearch", "Str_SummaryPopupSearchTT");
            _actionDefineBtn = MakePopupActionBtn("define", "Str_SummaryPopupDefine", "Str_SummaryPopupDefineTT");
            _actionExplainBtn = MakePopupActionBtn("explain", "Str_SummaryPopupExplain", "Str_SummaryPopupExplainTT");
            // Three equal faces sharing the row: star columns split the row's
            // width between the gutters, each button stretches to fill its cell,
            // and the two fixed 6px gutters keep the spacing even.
            var actions = new Grid { Margin = new Thickness(10, 7, 10, 4) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_actionSearchBtn, 0);
            Grid.SetColumn(_actionDefineBtn, 2);
            Grid.SetColumn(_actionExplainBtn, 4);
            actions.Children.Add(_actionSearchBtn);
            actions.Children.Add(_actionDefineBtn);
            actions.Children.Add(_actionExplainBtn);

            // The collapsible result card: loading line, provider detail under
            // an error, the AiMarkdown answer with selectable text and copy.
            _actionStatus = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            _actionStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _actionError = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 3, 0, 0) };
            _actionError.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _actionResultBox = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = 12.5,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 280,
                Visibility = Visibility.Collapsed
            };
            _actionResultBox.SetResourceReference(Control.FontFamilyProperty, "UiFont");
            _actionResultBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AiMarkdown.SetParagraphAlignment(_actionResultBox, TextAlignment.Left);

            var copyGlyph = new TextBlock { Text = "\uE8C8", FontFamily = UiKit.IconFont, FontSize = 12 };
            copyGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _actionCopyBtn = new Button
            {
                Style = (Style)FindResource("SumTitleBtn"),
                Width = 24,
                Height = 24,
                Content = copyGlyph,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 0, 0, 4),
                Visibility = Visibility.Collapsed
            };
            _actionCopyBtn.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                string text = _actionResultBox!.GetValue(AiMarkdown.TextProperty) as string ?? string.Empty;
                if (text.Length > 0)
                {
                    try { Clipboard.SetText(text); } catch { /* the clipboard can be held */ }
                }
            };

            var resultRow = new Grid();
            resultRow.Children.Add(_actionResultBox);
            var cardActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            cardActions.Children.Add(_actionCopyBtn);
            resultRow.Children.Add(cardActions);
            _actionCard = new Border
            {
                Visibility = Visibility.Collapsed,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(10, 9, 10, 10)
            };
            _actionCard.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
            _actionCard.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            _actionCard.Child = new StackPanel { Children = { _actionStatus, _actionError, resultRow } };

            var root = new Border
            {
                Width = 240,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 5,
                    Direction = 270,
                    Opacity = 0.45
                }
            };
            root.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F21E1E24")!;
            root.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            root.Child = new StackPanel { Children = { header, hairline, actions, _actionCard } };
            // Escape tunnels from the popup's own hwnd here - a dismiss, not a
            // window close.
            root.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DismissActionPopup();
                }
            };

            var popup = new Popup
            {
                AllowsTransparency = true,      // rounded corners + shadow need the layered hwnd
                StaysOpen = false,              // any outside click dismisses
                PlacementTarget = DocBox,
                PopupAnimation = PopupAnimation.None,
                Child = root
            };
            _actionRoot = root;             // PlayActionPopupOpen animates this face
            popup.Closed += (_, _) => OnActionPopupClosed();
            _actionPopup = popup;
            return popup;
        }

        private Button MakePopupActionBtn(string action, string labelKey, string tooltipKey)
        {
            var label = new TextBlock
            {
                Text = _loc(labelKey),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            var btn = new Button
            {
                Style = (Style)FindResource("SumActionBtn"),
                Content = label,
                Padding = new Thickness(10, 0, 10, 0),
                Height = 32,    // a notch taller than the style's 28: the popup breathes
                ToolTip = _loc(tooltipKey)
            };
            // All three wear the AI buttons' own dark green (the Start face) -
            // strings only, white and bold; the hover just dims the face (the
            // style's opacity trigger) so nothing around the button ever shifts.
            btn.Background = PopupGreen();
            btn.BorderBrush = PopupGreenEdge();
            btn.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; RunPopupAction(action); };
            return btn;
        }

        // The popup's green - the AI buttons' own dark green (Start's face).
        private static System.Windows.Media.Brush PopupGreen() =>
            (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString("#FF1B5E20")!;

        private static System.Windows.Media.Brush PopupGreenEdge() =>
            (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString("#FF0F3D14")!;

        private void OnActionPopupClosed()
        {
            // Runs for every close - an outside click, a dismissal, a reset,
            // the window closing. Cancels any in-flight card request and
            // releases the pinned word's plate. A stale close that arrives
            // after a newer open is ignored.
            if (_actionPopup is { IsOpen: true })
            {
                return;
            }

            _actionGen++;
            try { _actionCts?.Cancel(); } catch (ObjectDisposedException) { }
            _actionCts?.Dispose();
            _actionCts = null;
            _wordHighlights?.ClearPin();    // the plate was the popup's anchor; it dies with the popup
        }

        private void DismissActionPopup()
        {
            _actionOpenPending = null;      // a scheduled open dies with the dismissal
            if (_actionPopup is { IsOpen: true })
            {
                _actionPopup.IsOpen = false;    // Closed does the rest
            }
        }

        private void SetPopupStatus(string text)
        {
            _actionStatus!.Text = text;
            _actionStatus!.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetActionButtonsEnabled(bool enabled)
        {
            if (_actionSearchBtn is { } search)
            {
                search.IsEnabled = enabled;
            }

            if (_actionDefineBtn is { } define)
            {
                define.IsEnabled = enabled;
            }

            if (_actionExplainBtn is { } explain)
            {
                explain.IsEnabled = enabled;
            }
        }

        private async void RunPopupAction(string action)
        {
            if (_closed || _actionPopup is not { IsOpen: true } || _actionTarget.Length == 0)
            {
                return;
            }

            if (action == "search")
            {
                // The app's own browser carries the selection (v1.19.25): the
                // encoded query opens as a new tab of the built-in browser -
                // the pane comes forward, the reader never leaves the app.
                // The popup goes with the attention that left it.
                string query = Uri.EscapeDataString(TruncateForQuery(_actionTarget));
                string url = $"https://www.google.com/search?q={query}";
                DismissActionPopup();
                WebSearchRequested?.Invoke(url);
                return;
            }

            // Define / Explain: expand the card, park the buttons, and await
            // the answer on the dispatcher (every network await yields - zero
            // UI freeze). A newer request or a dismissal invalidates this one
            // through the generation counter.
            try { _actionCts?.Cancel(); } catch (ObjectDisposedException) { }
            _actionGen++;
            int gen = _actionGen;
            _actionCts = new CancellationTokenSource();
            CancellationToken ct = _actionCts.Token;
            _actionCard!.Visibility = Visibility.Visible;
            _actionError!.Visibility = Visibility.Collapsed;
            _actionError!.Text = string.Empty;
            _actionResultBox!.Visibility = Visibility.Collapsed;
            _actionResultBox!.SetValue(AiMarkdown.TextProperty, string.Empty);
            if (_actionCopyBtn is { } copy)
            {
                copy.Visibility = Visibility.Collapsed;
            }

            SetPopupStatus(_loc(action == "define" ? "Str_SummaryPopupDefining" : "Str_SummaryPopupExplaining"));
            SetActionButtonsEnabled(false);
            AiProviderConfig config = _configProvider();
            string target = _actionTarget;
            try
            {
                string result = action == "define"
                    ? await PageSummarizer.DefineTermAsync(config, TruncateForDefine(target), _language, ct)
                    : await ExplainFromRangeAsync(config, target, ct);
                if (gen != _actionGen || _closed || _actionPopup is not { IsOpen: true })
                {
                    return;     // dismissed or superseded while waiting
                }

                if (string.IsNullOrWhiteSpace(result))
                {
                    ShowPopupError(null);
                }
                else
                {
                    SetPopupStatus(string.Empty);
                    _actionResultBox!.Visibility = Visibility.Visible;
                    _actionResultBox!.SetValue(AiMarkdown.TextProperty, result);
                    if (_actionCopyBtn is { } copyShown)
                    {
                        copyShown.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dismissal or a newer request cancelled this one: the card is
                // gone or already repurposed, nothing to repaint.
            }
            catch (Exception ex)
            {
                if (gen == _actionGen && _actionPopup is { IsOpen: true })
                {
                    ShowPopupError(PageSummarizer.FriendlyError(ex));
                }
            }
            finally
            {
                if (gen == _actionGen)
                {
                    SetActionButtonsEnabled(true);
                }
            }
        }

        // The Explain pipeline: the author's own pages, not the summary. The
        // raw markdown of the active range is served from the cache the last
        // summary pass filled (_cachedRangeRawText); an empty or stale cache
        // (the range moved, a restored digest, a prefetch-served one) fetches
        // through ExtractRangeAsync once and refills the cache.
        private async Task<string> ExplainFromRangeAsync(AiProviderConfig config, string target, CancellationToken ct)
        {
            // Defensive range check: a degenerate or unrestored run range must
            // never explain from a lone page-1 cover leaf. The pages the
            // window is SHOWING are the honest fallback, not the 1/1 defaults.
            int first = _runFirstPage;
            int last = _runLastPage;
            if (first <= 0 || last < first ||
                (first == 1 && last == 1 && _pageCount > 1 && _rangePages > 1))
            {
                first = _startPage;
                last = RangeEnd();
                _runFirstPage = first;
                _runLastPage = last;
            }

            string raw;
            if (!string.IsNullOrEmpty(_cachedRangeRawText)
                && _cachedRangeFirst == first && _cachedRangeLast == last)
            {
                raw = _cachedRangeRawText;
            }
            else
            {
                raw = await PageSummarizer.ExtractRangeAsync(_filePath, first, last, ct);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return string.Empty;    // no text layer on these pages
                }

                _cachedRangeRawText = raw;
                _cachedRangeFirst = first;
                _cachedRangeLast = last;
            }

            return await PageSummarizer.ExplainExcerptAsync(
                config, TruncateForExplain(target), raw, first, last, ct);
        }

        private void ShowPopupError(string? detail)
        {
            SetPopupStatus(_loc("Str_SummaryPopupError"));
            if (string.IsNullOrEmpty(detail))
            {
                _actionError!.Visibility = Visibility.Collapsed;
                _actionError!.Text = string.Empty;
            }
            else
            {
                _actionError!.Text = detail;
                _actionError!.Visibility = Visibility.Visible;
            }

            _actionResultBox!.Visibility = Visibility.Collapsed;
            _actionResultBox!.SetValue(AiMarkdown.TextProperty, string.Empty);
            if (_actionCopyBtn is { } copy)
            {
                copy.Visibility = Visibility.Collapsed;
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
        // The F key, forwarded while the navigator holds the focus: toggle the
        // recap companion over the stretch on screen. A caret inside an
        // editable field (the range start box) keeps its letters.
        private void TryRecapHotkeyFromNavigator(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F
                || Keyboard.Modifiers != ModifierKeys.None
                || Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false })
            {
                return;
            }

            e.Handled = true;
            (Owner as MainWindow)?.ToggleRecapCompanion();
        }

        // The reading keys, forwarded while the navigator holds the focus.
        // Bare W/S and the keyboard UP/DOWN arrows scroll the digest
        // (smoothly); bare A/D and the keyboard LEFT/RIGHT arrows are the
        // stepper arrows - the keys and their letter twins are the same
        // hand. A modifier disqualifies the chord, a caret in the start
        // field keeps its letters, an open combo keeps its own arrow
        // selection, and the editor never sees any of them.
        private void TryReadingNavigatorKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None
                || Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false }
                || Keyboard.FocusedElement is System.Windows.Controls.ComboBox)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.W or Key.Up: e.Handled = SmoothScrollDigest(-1); break;
                case Key.S or Key.Down: e.Handled = SmoothScrollDigest(+1); break;
                case Key.A or Key.Left: MoveRange(-1); e.Handled = true; break;
                case Key.D or Key.Right: MoveRange(+1); e.Handled = true; break;
            }
        }

        // The digest scroll rides a proxy property: the animation eases the
        // value and every step lands on the reader's RichTextBox - TextBoxBase
        // has no smooth scroll of its own. A held animation is replaced by the
        // next press from the live offset, so rapid W/S chain instead of fight.
        private static readonly DependencyProperty DigestOffsetProperty =
            DependencyProperty.Register("DigestOffset", typeof(double), typeof(SummaryWindow),
                new PropertyMetadata(0.0, (d, e) =>
                    ((SummaryWindow)d).DocBox.ScrollToVerticalOffset((double)e.NewValue)));

        private bool SmoothScrollDigest(int direction)
        {
            double extent = DocBox.ExtentHeight;
            double viewport = DocBox.ViewportHeight;
            if (extent <= viewport + 0.5)
            {
                return false;   // the page fits: the key belongs to no one
            }

            double from = DocBox.VerticalOffset;
            double target = Math.Clamp(
                from + (direction * Math.Max(viewport * 0.25, 60.0)), 0.0, extent - viewport);
            if (Math.Abs(target - from) < 0.5)
            {
                return true;    // parked at an edge: swallow, nothing to move
            }

            BeginAnimation(DigestOffsetProperty, new System.Windows.Media.Animation.DoubleAnimation(
                from, target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            });
            return true;
        }

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
