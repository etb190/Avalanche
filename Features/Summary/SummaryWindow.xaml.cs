// Features/Summary/SummaryWindow.xaml.cs — the floating page-summary companion.
//
// Single owned window (MainWindow keeps one instance), themed chrome via
// DialogChrome (rounded card, themed title bar, Escape-close, fade). The body
// is a reading navigator, not a form:
//   * a start-page stepper at the top (arrow buttons + a page-number field),
//   * range chips (1p..60p) that generate [start, start + range] on click,
//   * a word-limit dropdown telling the model how long the digest should be,
//   * a summary-language dropdown; Arabic flips the digest right-to-left.
// Finishing a digest advances the start page to the end of the range just
// read, so clicking the same chip again reads the next stretch of the book.
// Generation is driven by PageSummarizer's async update stream: deltas
// throttle-flush into the justified AiMarkdown RichTextBox. Nothing runs
// automatically - a range chip (or Enter in the page field) is explicit.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
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
        private static readonly int[] RangeChoices = { 1, 5, 20, 40, 60 };
        private static readonly int[] WordChoices = { 500, 750, 1000, 1250, 1500, 2000 };
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
        private int _generation;        // bumped by close so stale continuations can't repaint

        private int _rangePages = 20;   // pages per digest: the selected range chip's value
        private int _targetWords = 1000;
        private string _language = "English";

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

            // Stepper: the arrows nudge the start page (repeat while held); Enter in
            // the field generates with the selected range, like a chip click would.
            NavPrevBtn.Click += (_, _) => StepStart(-1);
            NavNextBtn.Click += (_, _) => StepStart(+1);
            StartBox.TextChanged += OnStartTextChanged;
            StartBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    StartGeneration();
                }
            };
            StartBox.GotKeyboardFocus += (_, _) => StartBox.SelectAll();

            // Range chips: label from Strings, page count from Tag. A click checks the
            // chip (accent state) and immediately generates [start, start + range].
            foreach (var chip in new[] { RangeChip1, RangeChip5, RangeChip20, RangeChip40, RangeChip60 })
            {
                int pages = int.Parse((string)chip.Tag, CultureInfo.InvariantCulture);
                chip.Content = loc("Str_SummaryR" + pages + "p");
                chip.Checked += (_, _) => OnRangeChanged(pages);
                chip.Click += (_, _) => StartGeneration();
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
                    ApplyReadingDirection();
                }
            };

            LoadPreferences();
            SelectChip(_rangePages);
            SelectCombo(WordsCombo, _targetWords);
            SelectCombo(LangCombo, _language);
            ApplyReadingDirection();

            int current = _currentPageProvider();
            SetStartPage(current >= 0 ? current + 1 : 1);

            Closed += (_, _) =>
            {
                _closed = true;
                _generation++;      // a run cancelled by the close can't repaint either
                _cts?.Cancel();
                PersistPlacement();
            };

            RestorePlacement();
        }

        /// <summary>True when this window already summarizes the given document
        /// (MainWindow reuses the instance instead of opening a second one).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Reading navigator
        // ------------------------------------------------------------------

        // Range, word ceiling and language survive window reopenings and documents:
        // a reading session keeps its shape until the reader changes it.
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
        }

        private void SelectChip(int pages)
        {
            foreach (var chip in new[] { RangeChip1, RangeChip5, RangeChip20, RangeChip40, RangeChip60 })
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

        // Arrows move the start page by one and clamp to the document; held down they
        // repeat (RepeatButton), so flipping through a long book is quick.
        private void StepStart(int direction)
        {
            int current = ParseStart();
            if (current < 1)
            {
                current = 1;
            }

            SetStartPage(current + direction);
        }

        private void SetStartPage(int page)
        {
            StartBox.Text = Math.Clamp(page, 1, _pageCount).ToString(CultureInfo.InvariantCulture);
            UpdateRangeHint();
        }

        private int ParseStart()
        {
            return int.TryParse(StartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }

        // The field takes digits only (a 5-digit cap covers any real document);
        // anything else is stripped as it is typed, and the range hint follows.
        private void OnStartTextChanged(object sender, TextChangedEventArgs e)
        {
            string clean = new string((StartBox.Text ?? string.Empty).Where(char.IsDigit).ToArray());
            if (clean.Length > 5)
            {
                clean = clean[..5];
            }

            if (clean != StartBox.Text)
            {
                StartBox.Text = clean;
                StartBox.CaretIndex = clean.Length;
                return;     // the re-entrant TextChanged updates the hint
            }

            UpdateRangeHint();
        }

        // Left of the stepper: the digest a range chip would generate right now,
        // following the start field and the selected chip live.
        private void UpdateRangeHint()
        {
            int first = ParseStart();
            if (first < 1 || first > _pageCount)
            {
                PagesLabel.Text = _loc("Str_SummaryPages");
                return;
            }

            int last = Math.Min(first + _rangePages, _pageCount);
            PagesLabel.Text = string.Format(
                CultureInfo.InvariantCulture, "{0} {1} \u2013 {2}", _loc("Str_SummaryPages"), first, last);
        }

        private void OnRangeChanged(int pages)
        {
            _rangePages = pages;
            AppDataPaths.SetSetting("summary.range", pages.ToString(CultureInfo.InvariantCulture));
            UpdateRangeHint();
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
            if (_generating || _closed)
            {
                return;
            }

            int first = ParseStart();
            if (first < 1 || first > _pageCount)
            {
                Overlay(string.Format(_loc("Str_SummaryInvalidRange"), _pageCount));
                return;
            }

            // The chip is the length: [start, start + N], clipped at the document.
            int last = Math.Min(first + _rangePages, _pageCount);
            int gen = ++_generation;    // a later close bumps this: stale continuations stop repainting
            _generating = true;
            _fullText = string.Empty;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            DocBox.SetValue(AiMarkdown.TextProperty, string.Empty);
            Overlay(null);
            SetBusy(true);

            var request = new SummaryRequest(
                _filePath, _documentId, first, last, _targetWords, _language, BypassCache: false);
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
                                // Reading chain: this stretch is read; the next click of a
                                // chip starts where this digest ended.
                                SetStartPage(last);
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
                // The only cancel path is the window closing, which bumps the
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
                // dump after a close is still evidence.
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

        // While a run is in flight the whole navigator quiets down; Escape (close)
        // and the window chrome stay live.
        private void SetBusy(bool busy)
        {
            NavPrevBtn.IsEnabled = !busy;
            NavNextBtn.IsEnabled = !busy;
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
