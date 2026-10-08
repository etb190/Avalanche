namespace Avalanche
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using Avalanche.Services;

    // Controls/ExtractPagesPrompt.xaml.cs - the Extract Pages prompt.
    //
    // v1.19.58: the extract button stopped assuming the thumbnail selection.
    // Every click now opens this card first - a recap-styled ask with one
    // input - and the reader names the pages in the free grammar the old
    // save-first flow never offered: single pages and inclusive a-b ranges
    // in any mix and any order ("452-684, 4, 8, 54-68"), each range keeping
    // its first and last page. Cancel (Escape, the red close mark) answers
    // nothing; the Extract answer is validated inline (a red field and an
    // honest line, never a second popup) and only a valid spec moves on to
    // the save dialog - which the caller seeds with the Desktop as the
    // starting place.
    //
    // Dressed exactly like the recap companion - DialogChrome rounded card,
    // themed title bar, close chip, pop-in entrance, pop-flavored fade
    // close, Escape-close - with the widgets built from the shared UiKit
    // faces so every theme (98SE included) draws them correctly.
    //
    // v1.19.59: the Extract answer finally survived the close. The pop close
    // IS a cancelled close, and WPF resets DialogResult to null whenever a
    // close is cancelled - so the v1.19.58 prompt set DialogResult=true only
    // to watch the fade's deferral wipe it back to null, and ShowDialog came
    // home empty: the card faded away and no save dialog ever appeared. The
    // themed FileDialog hit the same wall first and its answer is borrowed
    // whole: record the verdict in _pendingResult, assign DialogResult only
    // in the fade's completion callback, where nothing cancels the close.
    // The prompt also learned to remember where the reader parked it
    // (extract.win.left/top, the recap's placement recipe): centered on the
    // owner until the first move, the saved corner back on every open.

    public partial class ExtractPagesPrompt : Window
    {
        private static readonly char[] RangeDashes = ['-', '\u2013', '\u2014'];

        private readonly int _pageCount;
        private readonly TextBox _spec;
        private readonly TextBlock _error;

        // The validated answer: zero-based, deduped, ascending. Meaningful
        // only after ShowDialog() returns true.
        public IReadOnlyList<int> SelectedPages { get; private set; } = Array.Empty<int>();

        public ExtractPagesPrompt(Window owner, int pageCount, string prefill)
        {
            InitializeComponent();
            _pageCount = Math.Max(1, pageCount);
            Title = L("Str_Extract_Title");

            // fade:false - the window owns its own two-sided choreography
            // (the pop-in entrance plus the pop-flavored fade close), the
            // recap's recipe; Configure's generic close-only fade would
            // stack a second Closing handler onto the same window.
            DialogChrome.Configure(this, owner, resizable: false, fade: false);
            Opacity = 0;

            // The prompt remembers where the reader parked it (extract.win.*):
            // centered on the owner until the first move, the saved corner back
            // on every open, the final resting place written back on close.
            RestorePlacement();
            Closed += (_, _) => PersistPlacement();

            // ── the body: label, input, hint, error line, buttons ──
            var body = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };

            body.Children.Add(UiKit.GroupLabel(L("Str_Extract_Label")));

            // The verdict line is built before the input that arms it: the
            // TextChanged handler below dereferences it, and building first
            // is what keeps the nullable analysis as honest as the runtime.
            _error = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _error.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");

            _spec = UiKit.Field();
            _spec.FontSize = 13;
            // An edit re-arms the field: the red border and the verdict step
            // aside while the reader rewrites the spec.
            _spec.TextChanged += (_, _) =>
            {
                if (_error.Visibility != Visibility.Visible) return;
                _error.Visibility = Visibility.Collapsed;
                _spec.BorderBrush = UiKit.Brush("CardBorderBrush");
            };
            body.Children.Add(_spec);

            var hint = new TextBlock
            {
                Text = L("Str_Extract_Example"),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            };
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            body.Children.Add(hint);

            body.Children.Add(_error);

            var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
            cancel.Click += (_, _) => Answer(false);
            var extract = UiKit.Make(L("Str_Extract_Button"), accent: true);
            extract.IsDefault = true;
            extract.Click += (_, _) => Commit();
            body.Children.Add(UiKit.ButtonRow(cancel, extract));

            BodyRoot.Children.Add(body);

            // The chrome: recap card, recap close chip, Escape answers cancel.
            Content = DialogChrome.Frame(this, owner, Title,
                () => Answer(false), BodyRoot,
                titleBarExtras: new DialogChrome.TitleBarExtras
                {
                    CloseButtonSize = 24,
                    CloseCreated = DressCloseChip
                });

            if (!string.IsNullOrWhiteSpace(prefill)) _spec.Text = prefill;

            Loaded += (_, _) =>
            {
                WindowFx.PlayOpenPop(this);
                _spec.Focus();
                _spec.SelectAll();
            };
        }

        private void Commit()
        {
            var pages = new List<int>();
            if (!TryParseSpec(_spec.Text, _pageCount, pages, out string errorKey, out object[] errorArgs))
            {
                _error.Text = errorArgs.Length > 0
                    ? string.Format(CultureInfo.CurrentCulture, L(errorKey), errorArgs)
                    : L(errorKey);
                _error.Visibility = Visibility.Visible;
                _spec.BorderBrush = UiKit.Brush("DangerRed", Brushes.IndianRed);
                return;
            }

            SelectedPages = pages;
            Answer(true);
        }

        // ------------------------------------------------------------------
        // The spec grammar
        // ------------------------------------------------------------------

        // Comma-separated tokens, each a single 1-based page or an inclusive
        // "a-b" range (the dash may be ASCII, en or em); whitespace is free
        // and empty tokens are skipped, so "452-684, 4, 8, 54-68" and every
        // lazier variant read the same. The answer is one list of zero-based
        // indices, deduped and ascending, so overlapping specs never extract
        // a page twice. Anything that does not parse - or falls outside the
        // document - comes back with the key of the honest inline verdict.
        internal static bool TryParseSpec(string spec, int pageCount, List<int> pages,
            out string errorKey, out object[] errorArgs)
        {
            pages.Clear();
            errorKey = "";
            errorArgs = Array.Empty<object>();
            if (string.IsNullOrWhiteSpace(spec))
            {
                errorKey = "Str_Extract_BadSpec";
                return false;
            }

            foreach (string raw in spec.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;

                int dash = token.IndexOfAny(RangeDashes);
                if (dash < 0)
                {
                    if (!TryPage(token, out int page))
                    {
                        errorKey = "Str_Extract_BadSpec";
                        return false;
                    }
                    if (page < 1 || page > pageCount)
                    {
                        errorKey = "Str_Extract_OutOfRange";
                        errorArgs = [pageCount];
                        return false;
                    }
                    pages.Add(page - 1);
                    continue;
                }

                string left = token[..dash].Trim();
                string right = token[(dash + 1)..].Trim();
                if (!TryPage(left, out int first) || !TryPage(right, out int last) || first > last)
                {
                    errorKey = "Str_Extract_BadSpec";
                    return false;
                }
                if (first < 1 || last > pageCount)
                {
                    errorKey = "Str_Extract_OutOfRange";
                    errorArgs = [pageCount];
                    return false;
                }
                for (int page = first; page <= last; page++) pages.Add(page - 1);
            }

            if (pages.Count == 0)
            {
                errorKey = "Str_Extract_BadSpec";
                return false;
            }

            pages.Sort();
            for (int i = pages.Count - 1; i > 0; i--)
            {
                if (pages[i] == pages[i - 1]) pages.RemoveAt(i);
            }
            return true;
        }

        // A token is one page number: ASCII digits only (every culture reads
        // those the same), no sign, no fraction, and a value int can hold -
        // so a fat-fingered overflow answers "unreadable" rather than lying.
        private static bool TryPage(string token, out int page)
        {
            page = 0;
            if (token.Length == 0 || token.Length > 10 || !token.All(char.IsAsciiDigit)) return false;
            if (!ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)) return false;
            if (value > (ulong)int.MaxValue) return false;
            page = (int)value;
            return true;
        }

        // The inverse, for the prefill: a live thumbnail selection collapses
        // into the same grammar - consecutive runs become a-b stretches, the
        // rest singles - so the old selection flow survives as "confirm what
        // you selected".
        internal static string SpecFromPages(IEnumerable<int> zeroBasedPages)
        {
            List<int> ordered = zeroBasedPages.Distinct().OrderBy(page => page).ToList();
            var parts = new List<string>();
            int index = 0;
            while (index < ordered.Count)
            {
                int first = index;
                while (index + 1 < ordered.Count && ordered[index + 1] == ordered[index] + 1) index++;
                parts.Add(first == index
                    ? (ordered[first] + 1).ToString(CultureInfo.InvariantCulture)
                    : FormattableString.Invariant($"{ordered[first] + 1}-{ordered[index] + 1}"));
                index++;
            }
            return string.Join(", ", parts);
        }

        // The close mark wears the same bordered chip the recap's close mark
        // wears: same face, same stroke-drawn X, same air to the card's
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
            close.Style = (Style)FindResource("ExtractCloseBtn");
            var chromeMargin = Application.Current.TryFindResource("DialogCaptionButtonsMargin") is Thickness m
                ? m
                : new Thickness(0);
            close.Margin = new Thickness(chromeMargin.Left, chromeMargin.Top, Math.Max(chromeMargin.Right, 5), chromeMargin.Bottom);
        }

        // ------------------------------------------------------------------
        // The answer held through the fade
        // ------------------------------------------------------------------

        // WPF resets DialogResult to null whenever a close is cancelled, and
        // the pop close IS a cancelled close: the fade holds the real close
        // back until the animation lands. Assigning DialogResult inside
        // Commit therefore wrote true onto a value the deferral wiped back to
        // null, and ShowDialog returned "cancelled" for an answered prompt -
        // the v1.19.58 dead click. The themed FileDialog hit the same wall
        // first (Controls/FileDialog.xaml.cs) and its pattern is borrowed
        // whole: the verdict is recorded here, and only the fade's completion
        // - the one moment nothing will cancel the close - assigns it.
        private bool? _pendingResult;
        private bool _fading;
        private bool _allowClose;

        // Every exit answers: Extract=true; Cancel, the close mark, Escape
        // and Alt+F4=false. The first voice wins - a second click during the
        // 130ms fade cannot rewrite the verdict already flying.
        private void Answer(bool ok)
        {
            if (_pendingResult is not null) return;
            _pendingResult = ok;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_allowClose) return;   // the post-fade close - let it through
            e.Cancel = true;           // hold the real close until the fade finishes
            if (_fading) return;       // already fading - repeat triggers stand down
            _fading = true;
            WindowFx.PlayClosePop(this, WindowFx.PopMs, () =>
            {
                _allowClose = true;
                DialogResult = _pendingResult ?? false;   // the setter closes; nothing cancels it now
            });
        }

        // ------------------------------------------------------------------
        // Placement persistence (extract.win.*)
        // ------------------------------------------------------------------

        private void RestorePlacement()
        {
            try
            {
                if (TryGetSettingDouble("extract.win.left", out double left)
                    && TryGetSettingDouble("extract.win.top", out double top))
                {
                    // CenterOwner would clobber an explicit Left/Top at show
                    // time - Manual hands the position back to the saved
                    // coordinates (the recap companion's fix), clamped to the
                    // virtual desktop so a retired monitor never swallows
                    // the prompt.
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
                AppDataPaths.SetSetting("extract.win.left", Left.ToString(CultureInfo.InvariantCulture));
                AppDataPaths.SetSetting("extract.win.top", Top.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // best-effort
            }
        }

        private static bool TryGetSettingDouble(string name, out double value)
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

        private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    }
}
