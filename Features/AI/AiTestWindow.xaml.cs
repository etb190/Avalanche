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

            ModelValue.Text = _configProvider().Model ?? string.Empty;
            DocValue.Text = System.IO.Path.GetFileName(filePath);
            DocValue.ToolTip = filePath;

            RunBtn.Click += async (_, _) => await RunProbeAsync().ConfigureAwait(true);
            CancelBtn.Click += (_, _) => { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } };
            StatusText.Text = _loc("Str_AiTest_Waiting");
        }

        /// <summary>True when this window probes the given document (MainWindow closes
        /// the window when the document switches - a stale probe would audit the
        /// wrong file).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
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

            // The probe always starts at page 1: the same stretch a reader's first
            // summary would cover, deterministic across runs.
            int span = SelectedSpan();
            int last = Math.Min(span <= 0 ? _pageCount : span, _pageCount);
            const int first = 1;

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

        private int SelectedSpan()
        {
            foreach (RadioButton chip in new[] { Chip20, Chip60, Chip100, ChipAll })
            {
                if (chip.IsChecked == true && int.TryParse(chip.Tag as string, out int span))
                {
                    return span;    // -1 = the whole document
                }
            }

            return 100;
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
                    verdictKey = "Str_AiTest_Warn";
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

        // While a probe is in flight the span chips and Run quiet down; Cancel wakes
        // up as the only control that can end the run besides closing the window.
        private void SetBusy(bool busy)
        {
            RangePanel.IsEnabled = !busy;
            RunBtn.IsEnabled = !busy;
            CancelBtn.IsEnabled = busy;
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
        }
    }
}
