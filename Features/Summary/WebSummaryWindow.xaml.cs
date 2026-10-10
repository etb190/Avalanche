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

    public partial class WebSummaryWindow : Window
    {
        // The window's fixed vocabularies: no word ceiling, no language, no genre
        // table - the web digest speaks the nonfiction classic at 1,000 words and
        // answers in the page's own tongue.
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

            // The prompt dropdown: exactly one voice - the nonfiction classic.
            // The label rides in every Strings file already; the canonical id in
            // Tag is what the digest pass reads.
            PromptCombo.Items.Add(new ComboBoxItem
            {
                Content = loc("Str_Genre_Nonfiction"),
                Tag = Genre
            });
            PromptCombo.SelectedIndex = 0;

            // The typewriter's painting clock - 20ms, proportionally faster when
            // the backlog grows; started by every run, self-stopping when drained.
            _typeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _typeTimer.Tick += (_, _) => TypeTimerTick();

            DocBox.FontSize = _digestFont;
            UpdateEmptyState();

            Closed += (_, _) =>
            {
                _closed = true;
                _generation++;      // a stale continuation can't repaint either
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                StopElapsedClock();
            };
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
                                       page.Text, TargetWords, Genre, runConfig, _loc, token))
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
