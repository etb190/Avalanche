// Features/Summary/WebSummaryWindow.xaml.cs — the web page summarizer (v1.19.87).
//
// The page navigator's replica, stripped to the bones a web page needs:
//   * Start and Reset - the only controls that may begin or end a generation,
//   * one prompt dropdown carrying exactly one voice: the nonfiction classic,
//   * the digest card with the sidechat's empty state (robot, nudge, dots),
//   * a bottom line naming the model, the word and character counts and the
//     time the generation took,
//   * title-bar font chips (minus / plus) and the close mark.
// No range chips, no recap, no ping, no buffer arrows, no tester chip, no
// word-ceiling, no language dropdown, no pages picker. The input is the
// browser's own active tab: read fresh on every Start through the same
// extraction road the web sidechat travels, then digested by one streamed
// PageSummarizer pass (GenerateWebDigestAsync) - the typewriter paints the
// answer letter by letter, the elapsed clock ticks in the status line, and a
// superseded run is detached by a generation counter and cancelled.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using System.Windows.Threading;
    using Avalanche.Controls;
    using Avalanche.Features.AI;
    using Avalanche.Services;

    public partial class WebSummaryWindow : Window
    {
        // The window's vocabularies (v1.19.91): the word ceilings and the
        // output languages the book navigator speaks, now here too - each
        // dial remembers its pick across sessions, and the digest answers
        // in the chosen tongue.
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

        private const int TargetWords = 1000;
        private const string Genre = "nonfiction_classic";

        private readonly Func<AiProviderConfig> _configProvider;
        private readonly Func<string?> _tabIdProvider;
        private readonly Func<string, CancellationToken, Task<WebPageSnapshot?>> _pageReader;
        private readonly Func<string, string> _loc;

        private CancellationTokenSource? _cts;
        private bool _generating;
        private bool _closed;
        private int _generation;        // bumped by supersede/reset/close: stale continuations can't repaint
        private DateTime _runStartedUtc;
        private string _progressBase = string.Empty;
        private string _fullText = string.Empty;
        private string _runModel = string.Empty;
        private double _digestFont = 13;
        private int _targetWords = TargetWords;
        private string _language = "English";
        private bool _wiring;           // the constructor's restore must not write the dials' own settings

        // The typewriter: the stream pump parks deltas in _incoming; the 20ms
        // timer on the UI thread drains it into _fullText and paints the shown
        // slice - the reader watches the digest written letter by letter.
        private readonly object _incomingGate = new();
        private readonly System.Text.StringBuilder _incoming = new();
        private int _shownLength;
        private bool _sawDeltas;
        private DispatcherTimer _typeTimer = null!;
        private DispatcherTimer? _elapsedTimer;

        private System.Windows.Media.Animation.Storyboard? _dotsStory;

        public WebSummaryWindow(
            MainWindow owner,
            Func<AiProviderConfig> configProvider,
            Func<string?> tabIdProvider,
            Func<string, CancellationToken, Task<WebPageSnapshot?>> pageReader,
            Func<string, string> loc)
        {
            InitializeComponent();
            _configProvider = configProvider;
            _tabIdProvider = tabIdProvider;
            _pageReader = pageReader;
            _loc = loc;

            // fade:false - the pop entrance plus the pop-flavored fade close own
            // the choreography, exactly like the navigator.
            DialogChrome.Configure(this, owner, resizable: true, fade: false);
            WindowFx.EnableFadeClose(this, WindowFx.PopMs, pop: true);

            // The digest window is a float - while it holds the app's foreground,
            // a taskbar toggle is a click at it, not an order to minimize the app
            // (Shell/FloatFocusLedger.cs).
            Activated += (_, _) => FloatFocusLedger.NoteFloatActivated();
            Deactivated += (_, _) => FloatFocusLedger.NoteFloatDismissed();
            Closed += (_, _) => FloatFocusLedger.NoteFloatDismissed();

            // The entrance: born transparent, raised on Loaded, first painted
            // frame already animating from zero.
            Opacity = 0;
            Loaded += (_, _) => WindowFx.PlayOpenPop(this);

            // Borderless windows (WindowStyle.None) have no native resize border -
            // the same WindowChrome the navigator wears restores edge resizing.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(12),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            // The title bar: the wordmark, the digest-font pair before the close
            // mark, a close mark squared to the wordmark's height, a hairline
            // under the whole bar. No beaker here - the web window has nothing
            // to probe.
            var frame = DialogChrome.Frame(this, owner, "Avalanche", Close, BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    BottomSeparator = true,
                    CloseButtonSize = 24,
                    BeforeClose = new UIElement[]
                    {
                        TitleChip(plus: false, "Str_SummaryFontDown", () => AdjustDigestFont(-1), new Thickness(0, 0, 6, 0)),
                        TitleChip(plus: true, "Str_SummaryFontUp", () => AdjustDigestFont(+1), new Thickness(0, 0, 8, 0))
                    },
                    CloseCreated = DressCloseChip
                });
            // The lower corner squares have no visible pixels, so the OS passes
            // clicks straight through; hit-testable grips restore two-axis corner
            // resizing, matching the navigator.
            var root = new Grid();
            root.Children.Add(frame);
            root.Children.Add(CornerGrip(HorizontalAlignment.Left, System.Windows.Shell.ResizeGripDirection.BottomLeft));
            root.Children.Add(CornerGrip(HorizontalAlignment.Right, System.Windows.Shell.ResizeGripDirection.BottomRight));
            Content = root;
            Title = "Avalanche";

            // Start and Reset: the only generation owners there are.
            StartBtn.Click += (_, _) => StartGeneration();
            ResetBtn.Click += (_, _) => ResetAll();

            // The prompt dropdown (v1.19.88): the web voices the reader keeps
            // in the AI settings' prompt workshop - the nonfiction classic is
            // born with the app, every other row is their own. The canonical
            // id in Tag is what the digest pass reads.
            foreach (AiPromptDef p in PromptStore.For(PromptStore.CatWeb))
            {
                PromptCombo.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Id });
            }

            if (PromptCombo.Items.Count > 0) PromptCombo.SelectedIndex = 0;
            PromptStore.Changed += RebuildPromptItems;

            // v1.19.91: the word ceiling and the output language - the book
            // navigator's pair, remembered under the web window's own keys.
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
                    if (!_wiring)
                    {
                        AppDataPaths.SetSetting("websummary.words", words.ToString(CultureInfo.InvariantCulture));
                    }
                }
            };

            foreach (string language in LanguageChoices)
            {
                LangCombo.Items.Add(new ComboBoxItem
                {
                    Content = _loc("Str_SummaryLang" + LangKeySuffix[language]),
                    Tag = language
                });
            }

            LangCombo.SelectionChanged += (_, _) =>
            {
                if (LangCombo.SelectedItem is ComboBoxItem item && item.Tag is string language)
                {
                    _language = language;
                    if (!_wiring)
                    {
                        AppDataPaths.SetSetting("websummary.lang", language);
                    }

                    ApplyReadingDirection();
                }
            };

            // The typewriter's painting clock - 20ms, proportionally faster when
            // the backlog grows; started by every run, self-stopping when drained.
            _typeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _typeTimer.Tick += (_, _) => TypeTimerTick();

            // v1.19.91: the dials' saved picks and the window's saved place
            // and size - the same memory the book navigator keeps, under the
            // web window's own keys. The restore runs under the wiring guard
            // so a window being built never writes the choices it reads.
            _wiring = true;
            if (int.TryParse(AppDataPaths.GetSetting("websummary.words"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int savedWords)
                && Array.IndexOf(WordChoices, savedWords) >= 0)
            {
                _targetWords = savedWords;
            }

            string? savedLang = AppDataPaths.GetSetting("websummary.lang");
            if (!string.IsNullOrEmpty(savedLang) && Array.IndexOf(LanguageChoices, savedLang) >= 0)
            {
                _language = savedLang;
            }

            SelectCombo(WordsCombo, _targetWords);
            SelectCombo(LangCombo, _language);
            _wiring = false;
            ApplyReadingDirection();
            RestorePlacement();

            // Placement follows the window live (debounced), the book
            // navigator's own bargain: a killed app still finds the window
            // where the reader left it, not where it started.
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
                if (_closed) return;
                placementTimer.Stop();
                placementTimer.Start();
            };
            SizeChanged += (_, _) =>
            {
                if (_closed) return;
                placementTimer.Stop();
                placementTimer.Start();
            };

            // v1.19.92: the reader's place in the card is remembered live - a
            // debounced fraction of the scroll, saved beside the card, so a
            // killed app reopens the digest where the reading left off.
            DocBox.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler(DigestScrollChanged));

            DocBox.FontSize = _digestFont;
            // v1.19.92: the last generated card comes back with the window -
            // across the pane's switches, the window's own close and the
            // app's restarts - down to the reader's place in the scroll.
            RestoreDigestMemory();

            UpdateEmptyState();

            Closed += (_, _) =>
            {
                PromptStore.Changed -= RebuildPromptItems;
                _closed = true;
                _generation++;      // a stale continuation can't repaint either
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                StopElapsedClock();
                PersistPlacement();     // the place and size stay with the window (v1.19.91)
                PersistDigestMemory();  // the card and its place stay with the app (v1.19.92)
            };
        }

        // The workshop saved or deleted: the dropdown rebuilds in place,
        // the reader's pick rides along when it survived, the first voice
        // wins when it did not.
        private void RebuildPromptItems()
        {
            if (_closed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RebuildPromptItems)); return; }
            string keep = PromptCombo.SelectedItem is ComboBoxItem sel && sel.Tag is string tag ? tag : "";
            PromptCombo.Items.Clear();
            foreach (AiPromptDef p in PromptStore.For(PromptStore.CatWeb))
            {
                PromptCombo.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Id });
                if (!string.IsNullOrEmpty(keep) && p.Id == keep) PromptCombo.SelectedIndex = PromptCombo.Items.Count - 1;
            }

            if (PromptCombo.SelectedIndex < 0 && PromptCombo.Items.Count > 0) PromptCombo.SelectedIndex = 0;
        }

        // The dropdown's chosen voice: the canonical id riding the selected
        // item's Tag - the workshop's rows, or the born-in nonfiction classic.
        private string PromptId()
        {
            return PromptCombo.SelectedItem is ComboBoxItem item && item.Tag is string id
                ? id
                : Genre;
        }

        // ------------------------------------------------------------------
        // The generation: read the page, stream the digest, type it out
        // ------------------------------------------------------------------

        private async void StartGeneration()
        {
            if (_closed)
            {
                return;
            }

            // Supersede: a running digest is detached (its continuations lose the
            // right to repaint) and its request cancelled before the new one
            // starts - Start over Start is how a run is replaced.
            if (_generating)
            {
                _generation++;
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            }

            int gen = ++_generation;
            _generating = true;
            _runStartedUtc = DateTime.UtcNow;
            _progressBase = string.Empty;
            _fullText = string.Empty;
            _shownLength = 0;
            _sawDeltas = false;
            lock (_incomingGate) { _incoming.Clear(); }
            _cts = new CancellationTokenSource();
            _typeTimer.Start();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);
            StartElapsedClock();    // the elapsed seconds tick in the status line from the first moment

            // The run's model takes the bottom-right word before its first word
            // lands - the config the run asks is the config named.
            var runConfig = _configProvider();
            string promptId = PromptId();   // read on the UI thread - the stream below rides the pool
            _runModel = runConfig.Model ?? string.Empty;
            SetModelLabel(_runModel);

            // The page: the browser's active tab is read fresh on every Start,
            // the same road the sidechat's answers travel - nothing cached,
            // nothing shared between tabs.
            string? tabId = _tabIdProvider();
            WebPageSnapshot? page = null;
            if (!string.IsNullOrEmpty(tabId))
            {
                try { page = await _pageReader(tabId, _cts.Token); }
                catch (System.OperationCanceledException) { page = null; }
                catch { page = null; }
            }

            if (gen != _generation)
            {
                return;     // superseded while the page was being read
            }

            if (page is null || string.IsNullOrWhiteSpace(page.Text))
            {
                // A tab with no readable page answers honestly instead of
                // pretending the model read something.
                _generating = false;
                SetBusy(false);
                StopElapsedClock();
                _typeTimer.Stop();
                Overlay(_loc("Str_AiWebNoPage"));
                UpdateEmptyState();
                return;
            }

            // The stream is consumed off the UI thread: deltas park in a
            // lock-guarded buffer, the only UI work left is the typewriter
            // timer - streaming keeps running wherever the reader goes.
            var token = _cts.Token;
            await Task.Run(async () =>
            {
                try
                {
                    await foreach (SummaryUpdate update in PageSummarizer.GenerateWebDigestAsync(
                                       page.Text, _targetWords, _language, promptId, runConfig, _loc, token))
                    {
                        switch (update.Kind)
                        {
                            case "progress":
                                Post(() =>
                                {
                                    if (gen == _generation)
                                    {
                                        _progressBase = update.Text;
                                        StatusText.Text = _progressBase + ElapsedSuffix();
                                    }
                                });
                                break;
                            case "delta":
                                FeedDelta(update.Text, gen);
                                break;
                            case "done":
                                Post(() => DoneLanded(gen, update));
                                break;
                            case "notext":
                                Post(() =>
                                {
                                    if (gen == _generation)
                                    {
                                        Overlay(_loc("Str_AiWebNoPage"));
                                    }
                                });
                                break;
                            case "error":
                                Post(() =>
                                {
                                    if (gen == _generation)
                                    {
                                        Overlay(string.Format(_loc("Str_SummaryError"), update.Text));
                                    }
                                });
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
                    // Cancellation means this run was superseded (or the window
                    // closed); both bump the generation, so only repaint when
                    // still current.
                    Post(() =>
                    {
                        if (gen == _generation)
                        {
                            CancelPaint();
                            StatusText.Text = _loc("Str_SummaryStopped");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        if (gen == _generation)
                        {
                            Overlay(string.Format(_loc("Str_SummaryError"), PageSummarizer.FriendlyError(ex)));
                        }
                    });
                }
                finally
                {
                    // A superseded run must not clear the busy state of the run
                    // that replaced it - only the current generation owns the UI.
                    Post(() =>
                    {
                        if (gen == _generation)
                        {
                            _generating = false;
                            SetBusy(false);
                            StopElapsedClock();
                            UpdateEmptyState();
                        }
                    });
                }
            });
        }

        // ------------------------------------------------------------------
        // The typewriter
        // ------------------------------------------------------------------

        // The pump never touches the UI: FeedDelta parks tokens behind the gate,
        // and this 20ms dispatcher timer is the ONLY writer of the card.
        private void TypeTimerTick()
        {
            string arrived;
            lock (_incomingGate)
            {
                arrived = _incoming.ToString();
                _incoming.Clear();
            }

            if (arrived.Length > 0)
            {
                _fullText += arrived;
            }

            int total = _fullText.Length;
            if (_shownLength >= total)
            {
                if (!_generating)
                {
                    _typeTimer.Stop();
                }

                return;
            }

            // ~3 words (18 chars) of lag at a crawl - one letter per tick; a
            // deeper backlog steps proportionally faster (capped at 60 letters
            // per tick) so a finished run writes itself out in seconds.
            int backlog = total - _shownLength;
            int step = Math.Clamp((backlog + 5) / 6, 1, 60);
            _shownLength = Math.Min(total, _shownLength + step);
            DocBox.SetValue(AiMarkdown.TextProperty, _fullText[.._shownLength]);
            UpdateEmptyState();
        }

        // What the card is showing right now: the painted prefix of the text.
        private string TypewriterShown()
            => _shownLength >= _fullText.Length
                ? _fullText
                : _fullText[..Math.Max(0, _shownLength)];

        // The pump's one stop on its way past: park the delta behind the gate.
        private void FeedDelta(string delta, int gen)
        {
            if (gen != _generation)
            {
                return;
            }

            lock (_incomingGate)
            {
                _incoming.Append(delta);
            }

            _sawDeltas = true;
        }

        // Marshals a pump-thread verdict onto the UI thread, FIFO at one priority.
        private void Post(Action action)
            => Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

        // A cancelled run shows everything that arrived - no typewriter grace.
        private void CancelPaint()
        {
            lock (_incomingGate)
            {
                _fullText += _incoming.ToString();
                _incoming.Clear();
            }

            _shownLength = _fullText.Length;
            DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
            // v1.19.92: a stopped run's arrival is remembered too.
            _clockFresh = true;
            PersistDigestMemory();
        }

        // The "done" verdict, run on the UI thread: the final text replaces the
        // typed run when the two differ, and the bottom line becomes the
        // verdict - the words, the characters, the time the generation took.
        private void DoneLanded(int gen, SummaryUpdate done)
        {
            if (gen != _generation)
            {
                return;
            }

            lock (_incomingGate)
            {
                _fullText += _incoming.ToString();
                _incoming.Clear();
            }

            if (done.Text.Length > 0)
            {
                bool typedPrefix = _sawDeltas && _shownLength > 0 && _shownLength <= _fullText.Length
                    && done.Text.StartsWith(_fullText[.._shownLength], StringComparison.Ordinal);
                if (_sawDeltas && !typedPrefix)
                {
                    // The final text diverged from what was painted: the pen
                    // restarts from the top so the card never shows a splice.
                    _shownLength = 0;
                }

                _fullText = done.Text;
            }

            if (!_sawDeltas)
            {
                _shownLength = _fullText.Length;
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
            }

            Overlay(null);
            StatusText.Text = string.Format(
                    _loc("Str_SummaryCounts"),
                    PageSummarizer.CountWords(_fullText),
                    _fullText.Length)
                + DurationSuffix(DateTime.UtcNow - _runStartedUtc);
            _cts?.Dispose();
            _cts = null;
            // v1.19.92: the finished card is remembered - restart-proof.
            _clockFresh = true;
            PersistDigestMemory();
        }

        // Reset: stops any live run, clears the card and returns the window to
        // idle/ready - and nothing else: the prompt dropdown has one voice and
        // keeps it.
        private void ResetAll()
        {
            _generation++;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _generating = false;
            _cts = null;
            _fullText = string.Empty;
            _shownLength = 0;
            _sawDeltas = false;
            lock (_incomingGate) { _incoming.Clear(); }
            _typeTimer.Stop();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            StatusText.Text = string.Empty;
            SetModelLabel(null);    // no digest on screen, no model to name
            _runModel = string.Empty;
            SetBusy(false);
            StopElapsedClock();
            UpdateEmptyState();
            // v1.19.92: the reset was the reader's hand - the memory goes too.
            _clockFresh = false;
            PersistDigestMemory();
        }

        // ------------------------------------------------------------------
        // The live clock
        // ------------------------------------------------------------------

        // While a run generates, the status line's progress word gains a ticking
        // elapsed clock - "  |  42s", the same wall clock the finished line
        // reports as "took 42s".
        private string ElapsedSuffix()
            => "  |  " + string.Format(_loc("Str_SummaryTook"), AiChatText.FormatDuration(DateTime.UtcNow - _runStartedUtc));

        private string DurationSuffix(TimeSpan elapsed)
            => "  |  " + string.Format(_loc("Str_SummaryTook"), AiChatText.FormatDuration(elapsed));

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

        // ------------------------------------------------------------------
        // The card's voice, the busy state, the chrome helpers
        // ------------------------------------------------------------------

        private void UpdateEmptyState()
        {
            try
            {
                bool overlaySpeaking = OverlayText.Visibility == Visibility.Visible;
                bool generating = _generating;
                bool hasDigest = _fullText.Length > 0;

                if (overlaySpeaking || hasDigest)
                {
                    EmptyState.Visibility = Visibility.Collapsed;
                    SetDotsRunning(false);
                    return;
                }

                EmptyState.Visibility = Visibility.Visible;
                if (generating)
                {
                    EmptyTitle.Visibility = Visibility.Collapsed;
                    EmptySubtitle.Visibility = Visibility.Collapsed;
                    BusyDots.Visibility = Visibility.Visible;
                    SetDotsRunning(true);
                }
                else
                {
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

        // While a run is in flight the prompt dropdown quiets down (it starts
        // nothing). Start and Reset stay live: Start supersedes, Reset ends.
        private void SetBusy(bool busy)
        {
            PromptCombo.IsEnabled = !busy;
        }

        // Invisible but hit-testable corner handle, straight from the navigator.
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
            UpdateEmptyState();
        }

        // The bottom-right word - "Model: {0}" in the reader's language, or
        // silence when there is nothing on screen to name.
        private void SetModelLabel(string? model)
        {
            ModelText.Text = string.IsNullOrWhiteSpace(model)
                ? string.Empty
                : string.Format(_loc("Str_AiModelUsed"), model);
        }

        // The drawn minus/plus chip: rectangles center exactly, no font metrics
        // to guess - the navigator's own pair, mirrored.
        private Button TitleChip(bool plus, string tooltipKey, Action onClick, Thickness? margin = null)
        {
            const double t = 1.6;   // stroke, in control px
            var mark = new Grid { Width = 10, Height = 10 };
            var bar = new System.Windows.Shapes.Rectangle { Width = 10, Height = t, RadiusX = t / 2, RadiusY = t / 2 };
            bar.SetResourceReference(Shape.FillProperty, "TextBrush");
            mark.Children.Add(bar);
            if (plus)
            {
                var stem = new System.Windows.Shapes.Rectangle { Width = t, Height = 10, RadiusX = t / 2, RadiusY = t / 2 };
                stem.SetResourceReference(Shape.FillProperty, "TextBrush");
                mark.Children.Add(stem);
            }
            var chip = new Button
            {
                Style = (Style)FindResource("WebSumTitleBtn"),
                Content = mark,
                Margin = margin ?? new Thickness(0),
                ToolTip = _loc(tooltipKey)
            };
            chip.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return chip;
        }

        // The close mark: red at rest, red face with on-primary ink on hover -
        // the Windows close convention in the theme's own palette.
        private void DressCloseChip(Button close)
        {
            const double t = 1.6;   // same stroke as the font chips
            var x = new Grid { Width = 10, Height = 10 };
            foreach (double angle in new[] { 45d, -45d })
            {
                var bar = new System.Windows.Shapes.Rectangle { Width = 10, Height = t, RadiusX = t / 2, RadiusY = t / 2 };
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
            close.Style = (Style)FindResource("WebSumCloseBtn");
            var chromeMargin = Application.Current.TryFindResource("DialogCaptionButtonsMargin") is Thickness m
                ? m
                : new Thickness(0);
            close.Margin = new Thickness(chromeMargin.Left, chromeMargin.Top, Math.Max(chromeMargin.Right, 5), chromeMargin.Bottom);
        }

        // ------------------------------------------------------------------
        // The dials' memory and the window's place (v1.19.91)
        // ------------------------------------------------------------------

        // Arabic turns the digest area right-to-left - the book navigator's
        // own bargain, kept here for the web window's card.
        private void ApplyReadingDirection()
        {
            DocBox.FlowDirection = string.Equals(_language, "Arabic", StringComparison.Ordinal)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }

        private static void SelectCombo(ComboBox combo, object value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (Equals(item.Tag, value))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void RestorePlacement()
        {
            try
            {
                if (TryGetSetting("websummary.win.w", out double width) && width >= 320 && width <= 1200)
                {
                    Width = width;
                }

                if (TryGetSetting("websummary.win.h", out double height) && height >= 360 && height <= 1600)
                {
                    Height = height;
                }

                if (TryGetSetting("websummary.win.left", out double left) && TryGetSetting("websummary.win.top", out double top))
                {
                    // DialogChrome.Configure leaves CenterOwner in place, and WPF
                    // applies it at Show() regardless of an explicit Left/Top - the
                    // manual startup location hands the place back to the reader.
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
                AppDataPaths.SetSetting("websummary.win.w", Width.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("websummary.win.h", Height.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("websummary.win.left", Left.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("websummary.win.top", Top.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // best-effort
            }
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

        // ------------------------------------------------------------------
        // The digest's own memory (v1.19.92): the last generated card and the
        // reader's place in it survive the window's closes, the pane's
        // switches and the app's restarts - saved beside the dials' picks and
        // restored on every open until a new run or a reset replaces them.
        // ------------------------------------------------------------------

        private DispatcherTimer? _scrollSaveTimer;   // the debounced scroll writer
        private bool _clockFresh;   // a run finished (or stopped) this session - the seconds are real

        // The pane switches park the digest by HIDING it: the generated card,
        // its scroll and the window's place all stay alive behind the pane,
        // and the welcome-back raises the same window.
        public void ParkForPaneSwitch()
        {
            if (!IsVisible) return;
            PersistPlacement();
            PersistDigestMemory();
            Hide();
        }

        public void ReturnFromPark()
        {
            if (IsVisible) return;
            Show();
        }

        private void DigestScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            if (_closed || _fullText.Length == 0)
            {
                return;
            }

            if (_scrollSaveTimer is null)
            {
                _scrollSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _scrollSaveTimer.Tick += (_, _) =>
                {
                    _scrollSaveTimer.Stop();
                    PersistDigestMemory(includeClock: false);
                };
            }

            _scrollSaveTimer.Stop();
            _scrollSaveTimer.Start();
        }

        private void PersistDigestMemory(bool includeClock = true)
        {
            try
            {
                AppDataPaths.SetSetting("websummary.last.text", _fullText);
                AppDataPaths.SetSetting(
                    "websummary.last.model",
                    _fullText.Length > 0 ? _runModel : string.Empty);
                AppDataPaths.SetSetting(
                    "websummary.last.scroll",
                    _fullText.Length > 0 && DocBox.ExtentHeight - DocBox.ViewportHeight > 0
                        ? (DocBox.VerticalOffset / (DocBox.ExtentHeight - DocBox.ViewportHeight))
                            .ToString(CultureInfo.InvariantCulture)
                        : string.Empty);
                if (includeClock && _clockFresh)
                {
                    AppDataPaths.SetSetting(
                        "websummary.last.secs",
                        Math.Max(0, (DateTime.UtcNow - _runStartedUtc).TotalSeconds)
                            .ToString("0", CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void RestoreDigestMemory()
        {
            try
            {
                string text = AppDataPaths.GetSetting("websummary.last.text") ?? string.Empty;
                if (text.Length == 0)
                {
                    return;
                }

                _fullText = text;
                _shownLength = text.Length;
                DocBox.SetValue(AiMarkdown.TextProperty, text);
                StatusText.Text = string.Format(
                    _loc("Str_SummaryCounts"), PageSummarizer.CountWords(text), text.Length);
                string savedModel = AppDataPaths.GetSetting("websummary.last.model") ?? string.Empty;
                if (savedModel.Length > 0)
                {
                    _runModel = savedModel;
                    SetModelLabel(savedModel);
                }

                if (double.TryParse(
                        AppDataPaths.GetSetting("websummary.last.secs"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double secs) && secs > 0)
                {
                    StatusText.Text += DurationSuffix(TimeSpan.FromSeconds(secs));
                }

                if (double.TryParse(
                        AppDataPaths.GetSetting("websummary.last.scroll"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ratio) && ratio > 0)
                {
                    double savedRatio = Math.Min(1.0, ratio);
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () =>
                        {
                            try
                            {
                                DocBox.UpdateLayout();
                                if (DocBox.ExtentHeight - DocBox.ViewportHeight > 0)
                                {
                                    DocBox.ScrollToVerticalOffset(savedRatio * (DocBox.ExtentHeight - DocBox.ViewportHeight));
                                }
                            }
                            catch
                            {
                                // a scroll that cannot climb is nobody's emergency
                            }
                        });
                }
            }
            catch
            {
                // best-effort
            }

            UpdateEmptyState();
        }

        private void AdjustDigestFont(int direction)
        {
            _digestFont = Math.Clamp(_digestFont + direction, 10, 24);
            DocBox.FontSize = _digestFont;
            // The rendered document carries the old base face, so a bare
            // FontSize change touched nothing the reader could see. Re-render
            // at the new base: body scales together.
            if (_fullText.Length > 0)
            {
                AiMarkdown.Rebuild(DocBox, TypewriterShown());
            }
        }
    }
}
