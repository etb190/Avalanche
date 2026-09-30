// Features/Summary/SummaryWindow.xaml.cs — the floating page-summary companion.
//
// Single owned window (MainWindow keeps one instance), themed chrome via
// DialogChrome (rounded card, themed title bar, Escape-close, fade). Generation
// is driven by PageSummarizer's async update stream: deltas throttle-flush into
// the AiMarkdown RichTextBox. Nothing runs automatically - Start and Reset
// are explicit, Reset clears back to the opening state.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using System.Windows.Threading;
    using Avalanche.Controls;
    using Avalanche.Features.AI;
    using Avalanche.Services;

    public partial class SummaryWindow : Window
    {
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
        private int _generation;        // bumped by Reset/close so stale continuations can't repaint
        private bool _bypassOnce;       // Reset arms the next Start to ignore any cached digest

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
            _pageCount = pageCount;
            _currentPageProvider = currentPageProvider;
            _configProvider = configProvider;
            _loc = loc;

            DialogChrome.Configure(this, owner, resizable: true);
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
            // The title bar carries only the Avalanche wordmark - the body speaks for itself.
            var frame = DialogChrome.Frame(this, owner, "Avalanche", Close, BodyRoot);
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

            PagesLabel.Text = loc("Str_SummaryPages");
            ToLabel.Text = loc("Str_SummaryTo");
            DepthCompactBtn.Content = loc("Str_SummaryDepthCompact");
            DepthStandardBtn.Content = loc("Str_SummaryDepthStandard");
            DepthDeepBtn.Content = loc("Str_SummaryDepthDeep");
            DepthStandardBtn.IsChecked = true;
            GoBtn.Content = loc("Str_SummaryStart");
            ResetBtn.Content = loc("Str_Tf_Reset");
            ResetBtn.Click += (_, _) => ResetAll();

            int current = currentPageProvider();
            int first = current >= 0 ? current + 1 : 1;
            int last = Math.Min(first + 1, Math.Max(1, pageCount));
            FromBox.Text = first.ToString(CultureInfo.InvariantCulture);
            ToBox.Text = last.ToString(CultureInfo.InvariantCulture);

            GoBtn.Click += (_, _) => StartGeneration(bypassCache: false);

            Closed += (_, _) =>
            {
                _closed = true;
                _generation++;      // a run cancelled by the close can't repaint either
                _cts?.Cancel();
                PersistPlacement();
            };

            RestorePlacement();
        }

        /// <summary>Clears the window back to its opening state: cancels a running
        /// generation, wipes the digest and status, and restores the default range.
        /// Nothing is generated automatically - Start is always explicit.</summary>
        private void ResetAll()
        {
            // Invalidate the in-flight run first: its continuations check the generation
            // token and no longer repaint the cleared card (status, digest, overlays).
            _generation++;
            _cts?.Cancel();
            _bypassOnce = true;     // the next Start runs fresh instead of replaying a cached digest

            _fullText = string.Empty;
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            StatusText.Text = string.Empty;
            int current = _currentPageProvider();
            int first = current >= 0 ? current + 1 : 1;
            FromBox.Text = first.ToString(CultureInfo.InvariantCulture);
            ToBox.Text = Math.Min(first + 1, Math.Max(1, _pageCount)).ToString(CultureInfo.InvariantCulture);
            DepthStandardBtn.IsChecked = true;
        }

        /// <summary>True when this window already summarizes the given document
        /// (MainWindow reuses the instance instead of opening a second one).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Generation
        // ------------------------------------------------------------------

        private async void StartGeneration(bool bypassCache)
        {
            if (_generating || _closed)
            {
                return;
            }

            if (!int.TryParse(FromBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)
                || !int.TryParse(ToBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int last)
                || first < 1 || last > _pageCount || first > last)
            {
                Overlay(string.Format(_loc("Str_SummaryInvalidRange"), _pageCount));
                return;
            }

            int gen = ++_generation;    // a later Reset bumps this: stale continuations stop repainting
            _generating = true;
            _fullText = string.Empty;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);

            bool bypass = bypassCache || _bypassOnce;
            _bypassOnce = false;
            var request = new SummaryRequest(
                _filePath, _documentId, first, last, TargetWords(), bypass);
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
                // The only cancel path is Reset (or window close), which bumps the
                // generation and clears the card; only repaint when still current.
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
                // dump after a Reset is still evidence.
                DiagnosticsBundle.Dump("summary exception: " + ex.GetType().Name);
                if (gen == _generation)
                {
                    Overlay(string.Format(_loc("Str_SummaryError"), PageSummarizer.FriendlyError(ex)));
                }
            }
            finally
            {
                _generating = false;
                SetBusy(false);
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
            // The status line stays factual: word and character count, nothing else.
            StatusText.Text = string.Format(
                _loc("Str_SummaryCounts"), PageSummarizer.CountWords(_fullText), _fullText.Length);
            _cts?.Dispose();
            _cts = null;
        }

        private int TargetWords()
        {
            if (DepthCompactBtn.IsChecked == true)
            {
                return 400;
            }

            return DepthDeepBtn.IsChecked == true ? 2000 : 1000;
        }

        // While a run is in flight Start goes quiet; Reset (which cancels) stays live.
        private void SetBusy(bool busy)
        {
            GoBtn.IsEnabled = !busy;
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
