// Features/AI/AiTestWindow.xaml.cs — the AI context & verification test companion.
//
// Owned, single-instance (MainWindow keeps one reference), and dressed exactly
// like the summary navigator: DialogChrome rounded card + themed title bar +
// close chip + hit-testable corner resize grips, pop-in entrance, fade close,
// Escape-close. The body is a probe console: pick a page span, run the probe,
// read the evidence - the verdict banner (PASS / FAIL / WARNING), the token
// audit grid (tokens read vs. estimated, characters sent, response speed) and
// the boundary rows (the sentences the model recalled against the ones
// extracted straight from the PDF, with fuzzy-match scores).
//
// Runs never touch the summary's state: the probe goes straight to the
// OpenAI-compatible endpoint through AiContextTester, so a test can be
// executed between (or during, within provider limits) summary runs without
// disturbing the reader's digest.

namespace Avalanche.Features.AI
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using Avalanche.Controls;
    using Avalanche.Services;

    public partial class AiTestWindow : Window
    {
        private readonly string _filePath;
        private readonly int _pageCount;
        private readonly Func<AiProviderConfig> _configProvider;
        private readonly Func<string, string> _loc;

        private CancellationTokenSource? _cts;
        private bool _running;
        private bool _closed;       // placement persistence stops when the window is gone
        private int _generation;    // bumped by supersede/close: stale continuations can't repaint

        public AiTestWindow(
            MainWindow owner,
            string filePath,
            int pageCount,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            InitializeComponent();
            _filePath = filePath;
            _pageCount = Math.Max(1, pageCount);
            _configProvider = configProvider;
            _loc = loc;

            // fade:false - the window owns its own two-sided choreography (the pop-in
            // entrance plus the pop-flavored fade close), exactly like the navigator;
            // Configure's generic close-only fade would stack a second Closing handler.
            DialogChrome.Configure(this, owner, resizable: true, fade: false);
            WindowFx.EnableFadeClose(this, WindowFx.PopMs, pop: true);
            Opacity = 0;
            Loaded += (_, _) => WindowFx.PlayOpenPop(this);
            // Borderless windows have no native resize border - the same WindowChrome
            // the navigator uses restores edge resizing without a grip.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(12),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            var frame = DialogChrome.Frame(this, owner, "Avalanche", Close, BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    BottomSeparator = true,
                    CloseButtonSize = 24,
                    CloseCreated = DressCloseChip
                });
            // Hit-testable corner grips, matching the navigator: the transparent halo
            // corners otherwise pass clicks straight through the rounded card.
            var root = new Grid();
            root.Children.Add(frame);
            root.Children.Add(CornerGrip(HorizontalAlignment.Left, System.Windows.Shell.ResizeGripDirection.BottomLeft));
            root.Children.Add(CornerGrip(HorizontalAlignment.Right, System.Windows.Shell.ResizeGripDirection.BottomRight));
            Content = root;
            Title = "Avalanche";

            // Span chips reshare the summary navigator's localized labels (20p/60p/100p);
            // the fourth chip is the whole document.
            Chip20.Content = _loc("Str_SummaryR20p");
            Chip60.Content = _loc("Str_SummaryR60p");
            Chip100.Content = _loc("Str_SummaryR100p");
            ChipAll.Content = _loc("Str_AiTest_AllPages");

            // Custom-range mode: the toggle swaps the preset chips for one field
            // that takes the stretch as a [start]-[end] pair, e.g. 84-120. It
            // seeds with the whole document (page 1 through the last page) and
            // accepts digits and the dash only - anything else dies at the input
            // or at the paste gate, and the run validates the pair against the
            // document before spending a request.
            RangeToggle.ToolTip = _loc("Str_AiTest_CustomRange");
            RangeBox.Text = "1-" + _pageCount.ToString(CultureInfo.InvariantCulture);
            RangeToggle.Checked += (_, _) => ApplyRangeMode();
            RangeToggle.Unchecked += (_, _) => ApplyRangeMode();
            RangeBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(IsRangeChar);
            // A click on the field must replace, not place: focusing selects the
            // whole pair ("84-120"), and the swallowed first click cannot park
            // the caret behind the selection - the summary field's own manner.
            RangeBox.GotFocus += (_, _) => RangeBox.SelectAll();
            RangeBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!RangeBox.IsKeyboardFocused)
                {
                    RangeBox.Focus();
                    e.Handled = true;
                }
            };
            System.Windows.DataObject.AddPastingHandler(RangeBox, (_, e) =>
            {
                if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is string pasted &&
                    !pasted.All(IsRangeChar))
                {
                    e.CancelCommand();
                }
            });

            // The probe's shape comes back with the window: the mode last used
            // (span chips or the [start]-[end] field) and the stretch last typed -
            // across reopenings AND restarts, like the placement below.
            if (AppDataPaths.GetSetting("aitest.mode") == "range")
            {
                RangeToggle.IsChecked = true;   // fires Checked -> ApplyRangeMode
            }

            string? savedRange = AppDataPaths.GetSetting("aitest.range");
            if (!string.IsNullOrWhiteSpace(savedRange))
            {
                RangeBox.Text = savedRange;
            }

            ModelValue.Text = _configProvider().Model ?? string.Empty;
            DocValue.Text = System.IO.Path.GetFileName(filePath);
            DocValue.ToolTip = filePath;

            RunBtn.Click += async (_, _) => await RunProbeAsync().ConfigureAwait(true);
            StatusText.Text = _loc("Str_AiTest_Waiting");

            RestorePlacement();
            RestoreResult();    // the last receipt reopens with the window

            // Placement follows the window live (debounced), the navigator's own
            // pattern: a killed app still finds the window where the reader left
            // it, and a reopened one comes back to the same spot.
            var placementTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
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

        /// <summary>True when this window probes the given document (MainWindow closes
        /// the window when the document switches - a stale probe would audit the
        /// wrong file).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called by the summary navigator's beaker chip: switch to range
        /// mode seeded with the navigator's current stretch and start the probe at
        /// once. A probe already in flight is cancelled; the new run starts as soon
        /// as the old one unwinds (superseded generations never repaint).</summary>
        public async void StartRangeTest(int first, int last)
        {
            first = Math.Clamp(first, 1, _pageCount);
            last = Math.Clamp(last, first, _pageCount);
            RangeToggle.IsChecked = true;       // ApplyRangeMode swaps the faces
            RangeBox.Text = first.ToString(CultureInfo.InvariantCulture)
                + "-" + last.ToString(CultureInfo.InvariantCulture);

            int gen = _generation;
            if (_running)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (_running && gen == _generation && wait.ElapsedMilliseconds < 2000)
                {
                    await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(true);
                }
            }

            if (gen != _generation)
            {
                return;     // the window closed while the old run unwound
            }

            await RunProbeAsync().ConfigureAwait(true);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _closed = true;
            PersistPlacement();     // the window's place survives restarts
            try
            {
                AppDataPaths.SetSetting("aitest.range", RangeBox.Text);
            }
            catch
            {
                // best-effort
            }
            _generation++;
            // Cancel only, never dispose: the detached probe still polls the token,
            // and a disposed source can throw from those polls. GC reclaims it.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts = null;
        }

        // ------------------------------------------------------------------
        // The probe run
        // ------------------------------------------------------------------

        private async System.Threading.Tasks.Task RunProbeAsync()
        {
            if (_running)
            {
                return;
            }

            // Span mode reads the checked preset chip (from page 1); range mode
            // reads the start/end pair. A malformed range never starts a run -
            // the status line says what the fields accept instead.
            (int first, int last)? range = ResolveProbeRange();
            if (range is null)
            {
                return;
            }

            int first = range.Value.first;
            int last = range.Value.last;

            // The stretch that ran is the stretch remembered (the restored field
            // opens with it next time).
            try
            {
                AppDataPaths.SetSetting("aitest.range", RangeBox.Text);
            }
            catch
            {
                // best-effort
            }

            _running = true;
            int gen = ++_generation;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            ResetResults();
            StatusText.Text = _loc("Str_AiTest_Running");
            try
            {
                AiTestProbeResult probe = await AiContextTester.RunProbeAsync(
                    _configProvider(), _filePath, first, last, progress: null, _cts.Token)
                    .ConfigureAwait(true);
                if (gen != _generation)
                {
                    return;     // the window closed (or a newer run started) meanwhile
                }

                if (!probe.Ok)
                {
                    ShowVerdict(
                        "#B3660F",
                        _loc(probe.Error == "notext" ? "Str_SummaryNoText" : "Str_AiTest_Waiting"));
                    StatusText.Text = _loc(probe.Error == "notext" ? "Str_SummaryNoText" : "Str_AiTest_Waiting");
                    return;
                }

                RenderResult(probe);
            }
            catch (OperationCanceledException)
            {
                if (gen == _generation)
                {
                    StatusText.Text = _loc("Str_SummaryStopped");
                }
            }
            catch (Exception ex)
            {
                if (gen == _generation)
                {
                    StatusText.Text = string.Format(
                        _loc("Str_AiTest_Failed"), AiContextTester.FriendlyError(ex));
                }
            }
            finally
            {
                if (gen == _generation)
                {
                    _running = false;
                    SetBusy(false);
                }
            }
        }

        // Span mode: the checked chip (tag 20/60/100/-1). Range mode: the one
        // [start]-[end] field, validated against the document - null (with the
        // status line explaining) when it does not name a real stretch of pages.

        // The range field's alphabet: digits and the dash (minus or en-dash), with
        // spaces tolerated around the dash - anything else is dead on arrival.
        private static bool IsRangeChar(char c)
        {
            return char.IsAsciiDigit(c) || c == '-' || c == '\u2013' || c == ' ';
        }

        
        private (int first, int last)? ResolveProbeRange()
        {
            if (RangeToggle.IsChecked != true)
            {
                int span = 100;
                foreach (RadioButton chip in new[] { Chip20, Chip60, Chip100, ChipAll })
                {
                    if (chip.IsChecked == true && int.TryParse(chip.Tag as string, out int value))
                    {
                        span = value;    // -1 = the whole document
                        break;
                    }
                }

                return (1, Math.Min(span <= 0 ? _pageCount : span, _pageCount));
            }

            string raw = RangeBox.Text.Trim().Replace('\u2013', '-');
            int dash = raw.IndexOf('-');
            if (dash <= 0 ||
                !int.TryParse(raw[..dash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
                !int.TryParse(raw[(dash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end) ||
                start < 1 || end < start || end > _pageCount)
            {
                StatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    _loc("Str_AiTest_BadRange"),
                    _pageCount);
                return null;
            }

            return (start, end);
        }

        // The mode toggle's two faces: span mode shows the preset chips, range
        // mode swaps them for the start/end pair. Flipping the switch never
        // wipes what the reader typed into the fields.
        private void ApplyRangeMode()
        {
            bool custom = RangeToggle.IsChecked == true;
            RangePanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            RangeInputPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            // The captions hold their posts either way - Pages on the left, Range
            // on the right - and the mode in force reads one step brighter than
            // the one at rest.
            ModePagesLabel.SetResourceReference(TextBlock.ForegroundProperty,
                custom ? "MutedTextBrush" : "TextBrush");
            ModeRangeLabel.SetResourceReference(TextBlock.ForegroundProperty,
                custom ? "TextBrush" : "MutedTextBrush");
            // The mode is state, not a whim: it survives the window and the app.
            try
            {
                AppDataPaths.SetSetting("aitest.mode", custom ? "range" : "pages");
            }
            catch
            {
                // best-effort
            }
        }

        private void RenderResult(AiTestProbeResult probe)
        {
            // Token audit grid - the mathematical core: what the provider reports
            // reading against what the extracted pages estimate.
            TokensReadValue.Text = probe.TokensKnown
                ? probe.PromptTokens.ToString("N0", CultureInfo.CurrentCulture)
                : "—";
            TokensSentValue.Text = probe.TokensEstimated.ToString("N0", CultureInfo.CurrentCulture);
            CharsSentValue.Text = probe.CharsSent.ToString("N0", CultureInfo.CurrentCulture);
            SpeedValue.Text = probe.TokensPerSecond > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0:0.0} s · {1:0.0} tok/s",
                    probe.Seconds,
                    probe.TokensPerSecond)
                : string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", probe.Seconds);

            // Boundary rows: expected (extracted from the PDF) vs. recalled (what the
            // model quoted), with the fuzzy-match score beside each recalled quote.
            FirstExpectedValue.Text = string.IsNullOrEmpty(probe.FirstExpected) ? "—" : probe.FirstExpected;
            LastExpectedValue.Text = string.IsNullOrEmpty(probe.LastExpected) ? "—" : probe.LastExpected;
            FirstRecalledValue.Text = string.IsNullOrEmpty(probe.FirstRecalled) ? "—" : probe.FirstRecalled;
            LastRecalledValue.Text = string.IsNullOrEmpty(probe.LastRecalled) ? "—" : probe.LastRecalled;
            FirstMatchValue.Text = string.IsNullOrEmpty(probe.FirstRecalled)
                ? string.Empty
                : string.Format(_loc("Str_AiTest_MatchScore"), probe.FirstMatch);
            LastMatchValue.Text = string.IsNullOrEmpty(probe.LastRecalled)
                ? string.Empty
                : string.Format(_loc("Str_AiTest_MatchScore"), probe.LastMatch);

            // The verdict banner: green when the full context is proven, red when
            // Ollama truncated the document, amber when the tokens look complete but
            // a boundary sentence mismatched.
            string verdictKey;
            string color;
            switch (probe.Verdict)
            {
                case "pass":
                    verdictKey = "Str_AiTest_Pass";
                    color = "#1B5E20";
                    break;
                case "fail":
                    verdictKey = "Str_AiTest_Fail";
                    color = "#B3261E";
                    break;
                default:
                    // The banner names WHICH boundary mismatched - a failed first
                    // boundary under a "last boundary mismatch" headline sent
                    // readers hunting for a truncation that never happened.
                    if (probe.FirstMatch < 80 && probe.LastMatch >= 80)
                    {
                        verdictKey = "Str_AiTest_WarnFirst";
                    }
                    else if (probe.LastMatch < 80 && probe.FirstMatch >= 80)
                    {
                        verdictKey = "Str_AiTest_WarnLast";
                    }
                    else
                    {
                        verdictKey = "Str_AiTest_Warn";
                    }

                    color = "#B3660F";
                    break;
            }

            ShowVerdict(color, string.Format(_loc(verdictKey), probe.SeenPercent));

            // The status line keeps the raw numbers: the honest receipt of the run.
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} / {1:N0} tok · {2:N0} ch · {3:0.0} s",
                probe.PromptTokens,
                probe.TokensEstimated,
                probe.CharsSent,
                probe.Seconds);

            // The receipt is now the tester's persisted state: closing the window
            // or the app keeps it, and the next open reads it straight back.
            PersistResult(probe);
        }

        private void ShowVerdict(string colorHex, string text)
        {
            VerdictBanner.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(colorHex));
            VerdictText.Text = text;
            VerdictBanner.Visibility = Visibility.Visible;
        }

        private void ResetResults()
        {
            VerdictBanner.Visibility = Visibility.Collapsed;
            TokensReadValue.Text = "—";
            TokensSentValue.Text = "—";
            CharsSentValue.Text = "—";
            SpeedValue.Text = "—";
            FirstExpectedValue.Text = "—";
            FirstRecalledValue.Text = "—";
            LastExpectedValue.Text = "—";
            LastRecalledValue.Text = "—";
            FirstMatchValue.Text = string.Empty;
            LastMatchValue.Text = string.Empty;
        }

        // While a probe is in flight the mode switch, the range field and Run
        // quiet down; closing the window (or launching a range test from the
        // navigator) is how a run ends early.
        private void SetBusy(bool busy)
        {
            RangeToggle.IsEnabled = !busy;
            RangePanel.IsEnabled = !busy;
            RangeInputPanel.IsEnabled = !busy;
            RunBtn.IsEnabled = !busy;
        }


        // ------------------------------------------------------------------
        // Placement persistence (the summary navigator's, transplanted)
        // ------------------------------------------------------------------

        // The test window is the reader's companion, not a dialog: it reopens
        // exactly where they dragged it, across window reopenings AND app
        // restarts - the summary navigator's own placement pattern, its own
        // aitest.win.* settings keeping the two windows independent.
        private void RestorePlacement()
        {
            try
            {
                if (TryGetSetting("aitest.win.w", out double width) && width >= 400 && width <= 1600)
                {
                    Width = width;
                }

                if (TryGetSetting("aitest.win.h", out double height) && height >= 400 && height <= 2200)
                {
                    Height = height;
                }

                if (TryGetSetting("aitest.win.left", out double left) && TryGetSetting("aitest.win.top", out double top))
                {
                    // DialogChrome.Configure leaves WindowStartupLocation.CenterOwner
                    // in place, and WPF applies the startup location at Show()
                    // REGARDLESS of explicitly set Left/Top - the same clobber the
                    // navigator's restore hit. Manual hands the position back to the
                    // saved coordinates.
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
                AppDataPaths.SetSetting("aitest.win.w", Width.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("aitest.win.h", Height.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("aitest.win.left", Left.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("aitest.win.top", Top.ToString(CultureInfo.InvariantCulture));
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

        /// <summary>Right-clicking the toolbar's AI Test button calls this: the window
        /// returns to its default spot (centered on the owner) - position ONLY, the
        /// size stays as the reader left it. The saved coordinates are dropped, so
        /// future opens center again too.</summary>
        public void ResetPosition()
        {
            try
            {
                AppDataPaths.SetSetting("aitest.win.left", string.Empty);
                AppDataPaths.SetSetting("aitest.win.top", string.Empty);

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

        // ------------------------------------------------------------------
        // Result persistence (the placement pattern, for the probe's receipt)
        // ------------------------------------------------------------------

        // The verdict and its evidence outlive the window AND the app: a JSON
        // receipt under aitest.result carries the whole rendered result, tagged
        // with the document it audited. A fresh successful test overwrites it;
        // closes, reopenings and restarts read the same numbers straight back.
        private sealed record PersistedProbe(
            string Doc, string Verdict, int Seen, long Prompt, bool Known,
            long Estimated, int Chars, double Seconds, double Tps,
            string Fe, string Le, string? Fr, string? Lr, int Fm, int Lm,
            int First, int Last);

        private void PersistResult(AiTestProbeResult probe)
        {
            try
            {
                AppDataPaths.SetSetting("aitest.result", System.Text.Json.JsonSerializer.Serialize(
                    new PersistedProbe(
                        Doc: _filePath, Verdict: probe.Verdict, Seen: probe.SeenPercent,
                        Prompt: probe.PromptTokens, Known: probe.TokensKnown,
                        Estimated: probe.TokensEstimated, Chars: probe.CharsSent,
                        Seconds: probe.Seconds, Tps: probe.TokensPerSecond,
                        Fe: probe.FirstExpected, Le: probe.LastExpected,
                        Fr: probe.FirstRecalled, Lr: probe.LastRecalled,
                        Fm: probe.FirstMatch, Lm: probe.LastMatch,
                        First: probe.FirstPage, Last: probe.LastPage)));
            }
            catch
            {
                // best-effort
            }
        }

        private void RestoreResult()
        {
            try
            {
                string? raw = AppDataPaths.GetSetting("aitest.result");
                if (string.IsNullOrEmpty(raw))
                {
                    return;
                }

                var saved = System.Text.Json.JsonSerializer.Deserialize<PersistedProbe>(raw);
                if (saved is null ||
                    !string.Equals(saved.Doc, _filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;     // no receipt yet, or it audited a different document
                }

                RenderResult(new AiTestProbeResult(
                    Ok: true, Error: null, FirstPage: saved.First, LastPage: saved.Last,
                    FirstExpected: saved.Fe, LastExpected: saved.Le,
                    FirstRecalled: saved.Fr, LastRecalled: saved.Lr,
                    FirstMatch: saved.Fm, LastMatch: saved.Lm,
                    PromptTokens: saved.Prompt, TokensKnown: saved.Known,
                    TokensEstimated: saved.Estimated, CharsSent: saved.Chars,
                    Seconds: saved.Seconds, TokensPerSecond: saved.Tps,
                    SeenPercent: saved.Seen, Truncated: saved.Verdict == "fail",
                    Verdict: saved.Verdict));
            }
            catch
            {
                // a damaged receipt must never keep the window from opening
            }
        }

        // ------------------------------------------------------------------
        // Chrome helpers (the navigator's own, transplanted)
        // ------------------------------------------------------------------

        // Invisible but hit-testable corner handle: a near-transparent fill keeps the
        // layered window from passing clicks through, and the attached
        // ResizeGripDirection tells WindowChrome which corner behaviour the grip maps to.
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

        // The close mark wears the same container as the navigator's: the title-chip
        // face (pane fill, hairline border, hover) with a drawn X at the same stroke
        // the summary window draws its marks.
        private void DressCloseChip(Button close)
        {
            const double t = 1.6;
            var x = new Grid { Width = 10, Height = 10 };
            foreach (double angle in new[] { 45d, -45d })
            {
                var bar = new Rectangle { Width = 10, Height = t, RadiusX = t / 2, RadiusY = t / 2 };
                bar.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
                bar.RenderTransform = new RotateTransform(angle);
                x.Children.Add(bar);
            }

            close.Style = (Style)FindResource("TestCloseBtn");
            close.Content = x;
            // The summary navigator's exact close-mark placement: guaranteed air
            // between the chip and the window's right edge (most themes leave
            // DialogCaptionButtonsMargin at zero) - same margin, same position,
            // same alignment as the navigator the window was cloned from.
            var chromeMargin = Application.Current.TryFindResource("DialogCaptionButtonsMargin") is Thickness m
                ? m
                : new Thickness(0);
            close.Margin = new Thickness(chromeMargin.Left, chromeMargin.Top, Math.Max(chromeMargin.Right, 5), chromeMargin.Bottom);
        }
    }
}
