// Features/Summary/RecapWindow.xaml.cs — the Recap companion: a memory bridge,
// not another console.
//
// Owned by MainWindow, single instance (RecapController keeps the reference),
// dressed exactly like the summary navigator - DialogChrome rounded card,
// themed title bar, close chip, hit-testable corner resize grips, pop-in
// entrance, pop-flavored fade close, Escape-close - but with ZERO controls:
// no range chips, no word ceilings, no steppers, no reset. The body is one
// reading surface, and the only things that ever change on it are the page
// number in the title and the 3-4 sentence paragraph inside.
//
// RecapController drives everything: NavigateTo retargets the window (the
// title follows, the body parks on the "Condensing page..." line),
// ShowRecapText paints a finished condensation (only when it is still for the
// stretch on screen), ShowRecapEmpty and ShowRecapFailed paint the two honest
// verdicts. ShowActivated is false - the window must never steal the reader's
// focus just because they turned a page. Placement persists across sessions
// (recap.win.* settings), debounced while the window lives, final on close.

namespace Avalanche.Features.Summary
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using Avalanche.Controls;
    using Avalanche.Features.AI;
    using Avalanche.Services;

    public partial class RecapWindow : Window
    {
        private readonly string _filePath;
        private readonly int _pageCount;
        private readonly Func<string, string> _loc;

        // Every TextBlock the chrome hands over (the blurred shadow copy and
        // the crisp one): rewritten together on each page turn, so the shadow
        // never ghosts the old title behind the new text.
        private readonly List<TextBlock> _titleTexts = new();

        private bool _closed;
        private int _firstPage = 1;     // the stretch currently on screen (1-based)
        private int _lastPage = 1;

        public RecapWindow(MainWindow owner, string filePath, int pageCount, Func<string, string> loc)
        {
            InitializeComponent();
            _filePath = filePath;
            _pageCount = Math.Max(1, pageCount);
            _loc = loc;

            // fade:false - the window owns its own two-sided choreography (the
            // pop-in entrance plus the pop-flavored fade close), exactly like
            // the navigator; Configure's generic close-only fade would stack a
            // second Closing handler onto the same window.
            DialogChrome.Configure(this, owner, resizable: true, fade: false);
            // The companion appears uninvited (a page turn summoned it): it
            // must never steal the focus the reader's hands are still using.
            ShowActivated = false;
            WindowFx.EnableFadeClose(this, WindowFx.PopMs, pop: true);
            Opacity = 0;
            Loaded += (_, _) => WindowFx.PlayOpenPop(this);
            // Borderless windows (WindowStyle.None) have no native resize border
            // - the same WindowChrome the navigator uses restores edge resizing
            // without a grip.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(12),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            var frame = DialogChrome.Frame(this, owner, TitleText(1, 1), Close, BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    BottomSeparator = true,
                    CloseButtonSize = 24,
                    CloseCreated = DressCloseChip,
                    TitleTextCreated = block => _titleTexts.Add(block)
                });
            // Hit-testable corner grips, matching the navigator: the transparent
            // halo corners otherwise pass clicks straight through the rounded card.
            var root = new Grid();
            root.Children.Add(frame);
            root.Children.Add(CornerGrip(HorizontalAlignment.Left, System.Windows.Shell.ResizeGripDirection.BottomLeft));
            root.Children.Add(CornerGrip(HorizontalAlignment.Right, System.Windows.Shell.ResizeGripDirection.BottomRight));
            Content = root;
            Title = TitleText(1, 1);

            Closed += (_, _) =>
            {
                _closed = true;
                PersistPlacement();
            };

            RestorePlacement();

            // Placement follows the window live (debounced), the navigator's own
            // pattern: a killed app still finds the window where the reader left it.
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

        /// <summary>True when this window recaps the given document (the
        /// controller dismisses a window left over from another book).</summary>
        public bool DocumentPathEquals(string path)
        {
            return string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The first page of the stretch currently on screen (1-based);
        /// the controller's stale-paint guard reads it alongside the range.</summary>
        public (int First, int Last) CurrentRange => (_firstPage, _lastPage);

        // The page turn: retarget the window (the title follows) and park the
        // body on the loading line until the condensation - or a cache hit -
        // replaces it.
        public void NavigateTo(int first, int last)
        {
            _firstPage = first;
            _lastPage = last;
            string title = TitleText(first, last);
            Title = title;
            foreach (TextBlock block in _titleTexts)
            {
                block.Text = title;
            }

            Overlay(_loc("Str_Recap_Loading"));
        }

        // A finished condensation for the stretch on screen. A result for a
        // DIFFERENT stretch (the reader turned pages while it condensed) is
        // dropped here: the controller already painted this window's page.
        public void ShowRecapText(int first, int last, string text)
        {
            if (_closed || _firstPage != first || _lastPage != last || string.IsNullOrEmpty(text))
            {
                return;
            }

            Overlay(null);
            DocBox.SetValue(AiMarkdown.TextProperty, text);
            ApplyReadingRhythm();
            DocBox.ScrollToHome();
        }

        // The page has no usable text layer (a scanned leaf, a full-page
        // diagram): the honest verdict, not a fabricated paragraph.
        public void ShowRecapEmpty(int first, int last)
        {
            if (_closed || _firstPage != first || _lastPage != last)
            {
                return;
            }

            Overlay(_loc("Str_Recap_Empty"));
        }

        // The request died (provider down, network gone): the error line, in
        // the same voice the navigator uses for its own failures.
        public void ShowRecapFailed(int first, int last, string message)
        {
            if (_closed || _firstPage != first || _lastPage != last)
            {
                return;
            }

            Overlay(string.Format(
                CultureInfo.InvariantCulture, _loc("Str_SummaryError"), message));
        }

        // The bar's text: "Recap — Page {0}" for the common single-page turn,
        // "Recap — Pages {0}–{1}" when a stretch ever needs naming.
        private string TitleText(int first, int last)
        {
            return last > first
                ? string.Format(CultureInfo.InvariantCulture, _loc("Str_Recap_RangeTitle"), first, last)
                : string.Format(CultureInfo.InvariantCulture, _loc("Str_Recap_Title"), first);
        }

        // 1.6x line height: the recap is read in one glance, and the air
        // between the lines is what lets three sentences breathe. Re-applied
        // after every paint - AiMarkdown builds a fresh FlowDocument each
        // time, and the new document carries the default single spacing.
        private void ApplyReadingRhythm()
        {
            if (DocBox.Document is FlowDocument doc)
            {
                doc.LineHeight = DocBox.FontSize * 1.6;
            }
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

        // Invisible but hit-testable corner handle: the navigator's own grip -
        // a near-transparent fill keeps the layered window from passing clicks
        // through, and the attached ResizeGripDirection tells WindowChrome which
        // non-client behaviour (both-axis corner resize) the grip maps to.
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

        // The close mark wears the same bordered chip the navigator's close
        // mark wears: same face, same stroke-drawn X, same air to the card's
        // corner. Drawn rectangles instead of MDL2 glyphs - the font's ink
        // rides high in its line box; rectangles center exactly.
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

            close.Content = x;
            close.Style = (Style)FindResource("RecapCloseBtn");
            var chromeMargin = Application.Current.TryFindResource("DialogCaptionButtonsMargin") is Thickness m
                ? m
                : new Thickness(0);
            close.Margin = new Thickness(chromeMargin.Left, chromeMargin.Top, Math.Max(chromeMargin.Right, 5), chromeMargin.Bottom);
        }

        // ------------------------------------------------------------------
        // Placement persistence (recap.win.*)
        // ------------------------------------------------------------------

        private void RestorePlacement()
        {
            try
            {
                if (TryGetSetting("recap.win.w", out double width) && width >= 320 && width <= 1200)
                {
                    Width = width;
                }

                if (TryGetSetting("recap.win.h", out double height) && height >= 200 && height <= 1600)
                {
                    Height = height;
                }

                if (TryGetSetting("recap.win.left", out double left) && TryGetSetting("recap.win.top", out double top))
                {
                    // CenterOwner would clobber explicitly set Left/Top at Show()
                    // (CalculateWindowLocation overwrites both) - Manual hands the
                    // position back to the saved coordinates, the navigator's fix.
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
                AppDataPaths.SetSetting("recap.win.w", Width.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("recap.win.h", Height.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("recap.win.left", Left.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("recap.win.top", Top.ToString(CultureInfo.InvariantCulture));
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
