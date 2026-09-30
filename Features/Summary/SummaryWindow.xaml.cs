// Features/Summary/SummaryWindow.xaml.cs — the floating page-summary companion.
//
// Single owned window (MainWindow keeps one instance), themed chrome via
// DialogChrome (rounded card, themed title bar, Escape-close, fade). Generation
// is driven by PageSummarizer's async update stream: deltas throttle-flush into
// the AiMarkdown RichTextBox, page tags build the coverage chips, and chip /
// range hints jump the reader via the injected _jumpToPage callback.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
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
        private readonly Action<int> _jumpToPage;          // 0-based page index
        private readonly Func<AiProviderConfig> _configProvider;
        private readonly Func<string, string> _loc;

        private CancellationTokenSource? _cts;
        private bool _generating;
        private string _fullText = string.Empty;
        private bool _fromCache;
        private int _rangeFirst = 1, _rangeLast = 1;
        private bool _flushPending;
        private bool _closed;
        private readonly DispatcherTimer _hintTimer;
        private DateTime _startedAt;

        public SummaryWindow(
            MainWindow owner,
            string filePath,
            string documentId,
            int pageCount,
            Func<int> currentPageProvider,
            Action<int> jumpToPage,
            Func<AiProviderConfig> configProvider,
            Func<string, string> loc)
        {
            InitializeComponent();
            _filePath = filePath;
            _documentId = documentId;
            _pageCount = pageCount;
            _currentPageProvider = currentPageProvider;
            _jumpToPage = jumpToPage;
            _configProvider = configProvider;
            _loc = loc;

            DialogChrome.Configure(this, owner, resizable: true);
            Content = DialogChrome.Frame(this, owner, "Avalanche - " + loc("Str_SummaryTitle"), Close, BodyRoot);
            Title = "Avalanche - " + loc("Str_SummaryTitle");

            PagesLabel.Text = loc("Str_SummaryPages");
            ToLabel.Text = loc("Str_SummaryTo");
            DepthBox.Items.Add(loc("Str_SummaryDepthCompact"));
            DepthBox.Items.Add(loc("Str_SummaryDepthStandard"));
            DepthBox.Items.Add(loc("Str_SummaryDepthDeep"));
            DepthBox.SelectedIndex = 1;
            PinBtn.ToolTip = loc("Str_SummaryPin");
            PinBtn.Content = (char)0xE718; // Segoe MDL2: Pin
            GoBtn.Content = loc("Str_SummaryGo");
            StopBtn.Content = loc("Str_SummaryStop");
            CopyBtn.Content = loc("Str_SummaryCopy");
            SaveBtn.Content = loc("Str_SummarySave");
            RegenBtn.Content = loc("Str_SummaryRegen");
            CoverageHint.Text = loc("Str_SummaryUntouched");

            int current = currentPageProvider();
            int first = current >= 0 ? current + 1 : 1;
            int last = Math.Min(first + 1, Math.Max(1, pageCount));
            FromBox.Text = first.ToString(CultureInfo.InvariantCulture);
            ToBox.Text = last.ToString(CultureInfo.InvariantCulture);

            GoBtn.Click += (_, _) => StartGeneration(bypassCache: false);
            StopBtn.Click += (_, _) => _cts?.Cancel();
            RegenBtn.Click += (_, _) => StartGeneration(bypassCache: true);
            CopyBtn.Click += (_, _) =>
            {
                try
                {
                    if (_fullText.Length > 0)
                    {
                        Clipboard.SetText(_fullText);
                    }
                }
                catch
                {
                    // clipboard can be held by another process
                }
            };
            SaveBtn.Click += (_, _) => _ = SaveMarkdownAsync();
            PinBtn.Click += (_, _) => Topmost = PinBtn.IsChecked == true;
            RerunBtn.Click += (_, _) =>
            {
                int page = Math.Min(Math.Max(currentPageProvider() + 1, 1), Math.Max(1, pageCount));
                FromBox.Text = page.ToString(CultureInfo.InvariantCulture);
                ToBox.Text = Math.Min(page + 1, Math.Max(1, pageCount)).ToString(CultureInfo.InvariantCulture);
                StartGeneration(bypassCache: false);
            };

            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _hintTimer.Tick += (_, _) => UpdateHint();
            _hintTimer.Start();

            Closed += (_, _) =>
            {
                _closed = true;
                _hintTimer.Stop();
                _cts?.Cancel();
                PersistPlacement();
            };

            RestorePlacement();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => StartGeneration(false)));
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

            _generating = true;
            _rangeFirst = first;
            _rangeLast = last;
            _fromCache = false;
            _fullText = string.Empty;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);
            BuildChips();
            _startedAt = DateTime.UtcNow;

            var request = new SummaryRequest(
                _filePath, _documentId, first, last, TargetWords(), bypassCache);
            try
            {
                await foreach (SummaryUpdate update in PageSummarizer.GenerateAsync(
                                   request, _configProvider(), _loc, _cts.Token))
                {
                    switch (update.Kind)
                    {
                        case "progress":
                            StatusText.Text = update.Text;
                            break;
                        case "delta":
                            AppendDelta(update.Text);
                            break;
                        case "done":
                            _fromCache = update.FromCache;
                            if (update.Text.Length > 0)
                            {
                                _fullText = update.Text;
                            }

                            DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                            FinishSuccess();
                            break;
                        case "notext":
                            Overlay(_loc("Str_SummaryNoText"));
                            break;
                        case "error":
                            Overlay(string.Format(_loc("Str_SummaryError"), update.Text));
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
                DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                StatusText.Text = _loc("Str_SummaryStopped");
            }
            catch (Exception ex)
            {
                Overlay(string.Format(_loc("Str_SummaryError"), PageSummarizer.FriendlyError(ex)));
            }
            finally
            {
                _generating = false;
                SetBusy(false);
                BuildChips();
            }
        }

        private void AppendDelta(string delta)
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
                if (_generating)
                {
                    DocBox.SetValue(AiMarkdown.TextProperty, _fullText);
                    DocBox.ScrollToEnd();
                }
            }));
        }

        private void FinishSuccess()
        {
            Overlay(null);
            int words = PageSummarizer.CountWords(_fullText);
            string tail = _fromCache
                ? _loc("Str_SummaryCached")
                : Math.Max(1, (int)(DateTime.UtcNow - _startedAt).TotalSeconds) + "s";
            StatusText.Text = string.Format(
                _loc("Str_SummaryDone"), words, _rangeLast - _rangeFirst + 1, tail);
            _cts?.Dispose();
            _cts = null;
        }

        private int TargetWords()
        {
            return DepthBox.SelectedIndex switch
            {
                0 => 400,
                2 => 2000,
                _ => 1000
            };
        }

        private void SetBusy(bool busy)
        {
            GoBtn.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            RegenBtn.Visibility = !busy && _fullText.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
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
        // Coverage chips + reader sync hint
        // ------------------------------------------------------------------

        private void BuildChips()
        {
            ChipsPanel.Children.Clear();
            if (_rangeLast < _rangeFirst)
            {
                return;
            }

            var covered = _fullText.Length == 0
                ? new System.Collections.Generic.HashSet<int>()
                : PageSummarizer.CoveredPages(_fullText, _rangeFirst, _rangeLast);
            for (int page = _rangeFirst; page <= _rangeLast; page++)
            {
                bool has = covered.Contains(page);
                var chip = new Button
                {
                    Content = page.ToString(CultureInfo.InvariantCulture),
                    Style = (Style)FindResource("ChipBtn"),
                    Opacity = has ? 1.0 : 0.45,
                    Background = ResourceBrush("PaneBrush"),
                    Foreground = ResourceBrush("TextBrush"),
                    BorderBrush = ResourceBrush("CardBorderBrush"),
                    ToolTip = _loc("Str_SummaryJumpHint")
                };
                int target = page;
                chip.Click += (_, _) => _jumpToPage(target - 1);
                ChipsPanel.Children.Add(chip);
            }
        }

        private Brush ResourceBrush(string key)
        {
            return TryFindResource(key) as Brush ?? Brushes.Transparent;
        }

        private void UpdateHint()
        {
            if (_generating || _closed)
            {
                return;
            }

            int current = _currentPageProvider();
            if (current < 0)
            {
                return;
            }

            int page = current + 1;
            if (page >= _rangeFirst && page <= _rangeLast)
            {
                RerunBtn.Visibility = Visibility.Collapsed;
                return;
            }

            StatusText.Text = string.Format(_loc("Str_SummaryOutOfRange"), page, _rangeFirst, _rangeLast);
            RerunBtn.Content = string.Format(
                _loc("Str_SummaryRerunHere"), page, Math.Min(page + 1, _pageCount));
            RerunBtn.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Export
        // ------------------------------------------------------------------

        private async Task SaveMarkdownAsync()
        {
            if (_fullText.Length == 0)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown|*.md|Text|*.txt",
                FileName = $"Summary_p{_rangeFirst}-{_rangeLast}.md"
            };
            if (dialog.ShowDialog(this) == true)
            {
                await File.WriteAllTextAsync(dialog.FileName, _fullText);
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
