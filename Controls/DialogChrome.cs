using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Avalanche
{
    // Chrome for modal dialog windows: Configure (borderless window setup), Frame (the rounded card +
    // title bar + grain), and BuildTitleBar (the Avalanche wordmark + red close button).
    internal static class DialogChrome
    {
        // Optional title-bar customization a window can ask for (currently the reading
        // navigator): a hairline under the bar, buttons docked left of the close mark,
        // and a fixed square size for the close mark itself.
        internal sealed class TitleBarExtras
        {
            public bool BottomSeparator { get; init; }
            public IReadOnlyList<UIElement> BeforeClose { get; init; } = Array.Empty<UIElement>();

            // 0 leaves the theme's DialogCloseWidth/Height in charge.
            public double CloseButtonSize { get; init; }

            // Last word on the close button's face: invoked once the chrome has
            // styled and placed it, so a host can re-dress it (the reading
            // navigator puts its close mark in the same bordered chip as the
            // font buttons beside it) without forking BuildTitleBar.
            public Action<Button>? CloseCreated { get; init; }

            // Centered extras ride the wordmark's own star column, horizontally
            // centered - the bar's visual middle, between the wordmark at the far
            // left and the close-side chips (the Recap toggle lives there).
            // Deliberately OUTSIDE the CloseButtonSize sync: that loop squares
            // every BeforeClose element to the wordmark's height, and a toggle
            // row is not a square chip.
            public IReadOnlyList<UIElement> Centered { get; init; } = Array.Empty<UIElement>();

            // The title's plain text node, handed to hosts whose title follows
            // their content - the Recap companion renames its bar for every page
            // it condenses. Fired for the blurred shadow copy first and the crisp
            // copy last; a host that rewrites every TextBlock it receives keeps
            // both in step (a stale shadow would ghost the old title).
            public Action<TextBlock>? TitleTextCreated { get; init; }
        }

        // Keep generated dialog captions on the same close mark as the main window.
        // E711 renders noticeably smaller inside the 18x16 Win98 caption face; E8BB is
        // the shared chrome glyph used by the main title bar and fills that face correctly.
        public const string CloseGlyph = "";

        // Brush from the owner (then app) resources, with a safe fallback so the helper never throws.
        private static Brush Brush(Window? owner, string key, Brush fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush ?? fallback;
        private static T Value<T>(Window? owner, string key, T fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) is T value ? value : fallback;

        internal static ImageSource? GrainTexture(Window? owner)
        {
            for (Window? window = owner; window is not null; window = window.Owner)
                if (window is MainWindow main && main.GrainTexture is not null)
                    return main.GrainTexture;
            if (Application.Current?.TryFindResource("GrainTileBrush") is ImageBrush tile)
                return tile.ImageSource;
            return null;
        }

        // Builds the title bar.
        //   win       - the window being chromed (used for DragMove on the whole bar)
        //   owner      - supplies the themed brushes + the ChromeCloseButton style (pass the window's owner)
        //   fullTitle  - the complete title, e.g. "Avalanche - Transform"; the "Avalanche" part becomes the
        //                wordmark and the remainder (" - Transform") is rendered in the courier title font
        //   onClose    - invoked when the red close button is clicked (e.g. set a result then Close())
        public static Border BuildTitleBar(Window win, Window? owner, string? fullTitle, Action onClose, TitleBarExtras? extras = null)
        {
            // Transparent (not null) background so the WHOLE bar is hit-testable and acts as a drag handle.
            bool caption = Value(owner, "UseDialogCaption", false);
            var bar = new Border
            {
                Background = caption ? Brush(owner, "TitleBarBrush", Brushes.Navy) : Brushes.Transparent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            // The bar is the grab handle: open hand on hover, closed hand for the whole
            // move. DragMove runs a modal mouse loop, so the override has to span it -
            // the same family cursors the stamp canvas and annotation grips use.
            bar.Cursor = DragCursors.Open;
            bar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    DragCursors.BeginDrag();
                    try { win.DragMove(); } catch { } finally { DragCursors.EndDrag(); }
                }
            };

            var grid = new Grid
            {
                // KillerNotes uses the shared title-bar inset for its dialog caption too.  In
                // particular, the 2px top inset keeps the 16px caption button centered in the
                // 20px classic band instead of riding against its upper edge.
                Margin = caption
                    ? Value(owner, "TitleBarPadding", new Thickness(4, 2, 0, 0))
                    : new Thickness(0)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The wordmark's primary TextBlock, captured for the extras' size sync: the
            // reading navigator squares its close mark and font chips to the wordmark's
            // rendered height ("as tall as the string" on every theme and DPI).
            TextBlock? measureText = null;

            // Build the wordmark row. A DropShadowEffect applied directly to text rasterizes it and
            // disables ClearType, which reads as blurry. So we LAYER it instead: a blurred black duplicate
            // sits behind a crisp, effect-free copy - soft shadow, sharp text. `shadow` paints the duplicate.
            StackPanel BuildWordmark(bool shadow)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Brush primary   = shadow ? Brushes.Black : Brush(owner, "TextBrush", Brushes.White);
                Brush logo      = shadow ? Brushes.Black : Brush(owner, "AccentLogo", Brushes.LimeGreen);
                Brush secondary = shadow ? Brushes.Black : Brush(owner, "MutedTextBrush", Brushes.Gray);
                int kp = fullTitle?.IndexOf("Avalanche", StringComparison.Ordinal) ?? -1;
                if (kp >= 0)
                {
                    // The "Avalanche" wordmark - same face and size as the main window logo -
                    // with the dialog suffix beside it sharing one baseline.
                    var mark = new TextBlock
                    {
                        Text = "Avalanche",
                        FontFamily = UiKit.UiFont,
                        FontWeight = FontWeights.Bold,
                        FontSize = 16.5,
                        Foreground = logo,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    measureText ??= mark;
                    sp.Children.Add(mark);
                    string after = fullTitle![(kp + "Avalanche".Length)..];
                    if (!string.IsNullOrEmpty(after))
                        sp.Children.Add(new TextBlock { Text = after, FontFamily = UiKit.MonoFont, FontSize = 14, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 0, 0) });
                }
                else
                {
                    // A title without the wordmark renders as one mono TextBlock.
                    // It doubles as the measure anchor (the close chip squares to
                    // it like the wordmark's own height) and is handed to the
                    // host through TitleTextCreated.
                    var plain = new TextBlock { Text = fullTitle ?? "", FontFamily = UiKit.MonoFont, FontSize = 14, Foreground = primary, VerticalAlignment = VerticalAlignment.Center };
                    measureText ??= plain;
                    extras?.TitleTextCreated?.Invoke(plain);
                    sp.Children.Add(plain);
                }
                return sp;
            }

            var title = new Grid { Margin = caption ? new Thickness(0) : new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (caption)
            {
                var captionText = new TextBlock
                {
                    Text = fullTitle ?? "Avalanche", FontFamily = Value(owner, "ChromeFontFamily", new FontFamily("Tahoma")),
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = Brush(owner, "ChromeTextBrush", Brushes.White), VerticalAlignment = VerticalAlignment.Center
                };
                measureText ??= captionText;
                extras?.TitleTextCreated?.Invoke(captionText);
                title.Children.Add(captionText);
            }
            else
            {
                var shadowLayer = BuildWordmark(true);
                shadowLayer.Opacity = 0.5;
                shadowLayer.Effect = new BlurEffect { Radius = 2 };
                shadowLayer.RenderTransform = new TranslateTransform(0.7, 1.2);
                title.Children.Add(shadowLayer);
                title.Children.Add(BuildWordmark(false));
            }
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            // Centered extras drop into the wordmark's star column: the column
            // spans everything left of the close-side extras, so centering inside
            // it parks the element in the bar's visual middle - the "top middle
            // bar". Vertical centering matches the wordmark's own.
            if (extras is { } chromeExtras)
            {
                foreach (UIElement centered in chromeExtras.Centered)
                {
                    if (centered is FrameworkElement centeredElement)
                    {
                        centeredElement.HorizontalAlignment = HorizontalAlignment.Center;
                        centeredElement.VerticalAlignment = VerticalAlignment.Center;
                    }

                    title.Children.Add(centered);
                }
            }

            // The close glyph and its complete raised/pressed face live in ChromeCloseButton.
            // Supplying another glyph/font/background here was overriding that canonical style and
            // produced the off-centre X and the exposed title-bar pixel seen in classic dialogs.
            var close = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                // The hover face is part of the card's top-right corner. Centering a 26px
                // button in the 40px caption left a visible 7px strip above it.
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            if (owner?.TryFindResource("ChromeCloseButton") is Style chromeClose)
            {
                close.Style = chromeClose;
            }
            else
            {
                close.Content = CloseGlyph;
                close.FontFamily = UiKit.IconFont;
                close.FontSize = 10;
                close.Width = 46; close.Height = 36;
                close.Foreground = Brush(owner, "DangerRed", Brushes.Red);
                close.Background = Brushes.Transparent;
                close.BorderThickness = new Thickness(0);
                close.Cursor = Cursors.Hand;
            }
            close.SetResourceReference(FrameworkElement.WidthProperty, "DialogCloseWidth");
            close.SetResourceReference(FrameworkElement.HeightProperty, "DialogCloseHeight");
            close.SetResourceReference(FrameworkElement.MarginProperty, "DialogCaptionButtonsMargin");
            // Resizable borderless dialogs use WindowChrome. Without this exemption its resize
            // band wins the top-right hit test, turning the close button into a resize handle.
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            // Get the click before the caption's DragMove handler starts its modal mouse loop.
            close.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; onClose(); };
            // Extras dock left of the close mark: one auto column each, close shifts right.
            int extraCount = extras?.BeforeClose.Count ?? 0;
            for (int i = 0; i < extraCount; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                UIElement extra = extras!.BeforeClose[i];
                Grid.SetColumn(extra, i + 1);
                grid.Children.Add(extra);
            }

            Grid.SetColumn(close, extraCount + 1);
            grid.Children.Add(close);

            if (extras is { CloseButtonSize: > 0 } sized)
            {
                // Local sizes override the DialogCloseWidth/Height resource references.
                // What the reader perceives is the VISIBLE face: the shared
                // ChromeCloseButton template insets its face by CaptionButtonMargin, so a
                // 24px button drew a ~13px mark that read as half the wordmark's height.
                // ChromeCloseButtonSquared fills the button instead, and both the close
                // mark and the chips track the wordmark's rendered height.
                if (owner?.TryFindResource("ChromeCloseButtonSquared") is Style squaredClose)
                {
                    close.Style = squaredClose;
                }

                close.VerticalAlignment = VerticalAlignment.Center;
                double floor = Math.Max(22.0, sized.CloseButtonSize);
                void Sync()
                {
                    double h = Math.Ceiling(measureText?.ActualHeight ?? 0);
                    if (h <= 0) h = floor;
                    h = Math.Max(h, floor);
                    if (bar.Height > 0) h = Math.Min(h, bar.Height - 4);
                    close.Width = h;
                    close.Height = h;
                    foreach (UIElement extra in sized.BeforeClose)
                    {
                        if (extra is FrameworkElement fe)
                        {
                            fe.Width = h;
                            fe.Height = h;
                            fe.VerticalAlignment = VerticalAlignment.Center;
                        }
                    }
                }
                measureText?.SizeChanged += (_, _) => Sync();
                bar.Loaded += (_, _) => Sync();
            }

            // The host's last word on the close face (navigator: chip-dressed X).
            extras?.CloseCreated?.Invoke(close);

            if (extras?.BottomSeparator == true)
            {
                var host = new Grid();
                host.Children.Add(grid);
                host.Children.Add(new Border
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsHitTestVisible = false,
                    Background = Brush(owner, "CardBorderBrush", Brushes.Gray),
                    Opacity = 0.85
                });
                bar.Child = host;
            }
            else
            {
                bar.Child = grid;
            }

            return bar;
        }

        // Borderless transparent window setup shared by every dialog.
        public static void Configure(Window win, Window? owner, bool resizable = false, bool fade = true)
        {
            win.Owner = owner;
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            win.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            win.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            win.FontFamily = UiKit.UiFont;
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);
            if (fade) WindowFx.EnableFadeClose(win);
        }

        private static Border FrameRing(Window? owner, string brushKey, string thicknessKey, string? marginKey = null)
        {
            var ring = new Border
            {
                IsHitTestVisible = false,
                BorderBrush = Brush(owner, brushKey, Brushes.Transparent),
                BorderThickness = Value(owner, thicknessKey, new Thickness(0))
            };
            if (marginKey != null)
                ring.Margin = Value(owner, marginKey, new Thickness(0));
            return ring;
        }

        private static Grid WindowFrame(Window? owner)
        {
            var frame = new Grid { IsHitTestVisible = false };
            frame.Children.Add(FrameRing(owner, "WindowFrameBrush", "DialogWindowFrameThickness", "WindowFrameMargin"));
            frame.Children.Add(FrameRing(owner, "FrameInnerLightBrush", "FrameInnerLightThickness", "FrameInnerMargin"));
            frame.Children.Add(FrameRing(owner, "FrameInnerDarkBrush", "FrameInnerDarkThickness", "FrameInnerMargin"));
            frame.Children.Add(FrameRing(owner, "FrameOuterLightBrush", "FrameOuterLightThickness"));
            frame.Children.Add(FrameRing(owner, "FrameOuterDarkBrush", "FrameOuterDarkThickness"));
            return frame;
        }

        internal static UIElement WrapContent(
            Window? owner, UIElement content, Thickness? haloMargin = null)
        {
            var host = new Grid
            {
                Margin = haloMargin ?? Value(owner, "DialogHaloMargin", new Thickness(12))
            };
            var radius = Value(owner, "WindowCornerRadius", new CornerRadius(7));
            host.Children.Add(new Border
            {
                Background = Brush(owner, "WindowFrameBrush", UiKit.Brush("MenuBackgroundBrush")),
                CornerRadius = radius,
                IsHitTestVisible = false,
                Effect = UiKit.ShadowDialog()
            });
            var card = new Grid();
            card.Children.Add(new Border
            {
                Background = Brush(owner, "BackgroundBrush", UiKit.Brush("BackgroundBrush")),
                CornerRadius = radius,
                Margin = Value(owner, "DialogWindowFramePadding", new Thickness(0)),
                Child = content
            });
            card.Children.Add(WindowFrame(owner));
            // The 1px window outline every dialog was missing: same DialogFrameBrush the file
            // picker draws (defaults to AppBorderBrush, the main window's DWM border tone).
            card.Children.Add(new Border
            {
                BorderBrush = Brush(owner, "DialogFrameBrush", UiKit.Brush("MenuBorderBrush")),
                BorderThickness = Value(owner, "DialogFrameThickness", new Thickness(1)),
                CornerRadius = radius,
                IsHitTestVisible = false,
            });
            host.Children.Add(card);
            return host;
        }

        // Standard dialog: content is inset from the same five-layer frame used by KillerNotes.
        public static UIElement Frame(
            Window win, Window? owner, string title, Action onClose, UIElement body,
            Thickness? haloMargin = null, TitleBarExtras? titleBarExtras = null)
        {
            // A body declared as the window's XAML content is still the window's logical
            // child when its ctor hands it over (SummaryWindow v1.8.71 crash: "Specified
            // element is already the logical child of another element"). Disconnect it so
            // the frame can adopt it; a no-op for the code-built bodies other dialogs pass.
            if (body is FrameworkElement fe && fe.Parent is { } previous)
            {
                switch (previous)
                {
                    case Panel panel when panel.Children.Contains(fe): panel.Children.Remove(fe); break;
                    case Border border when ReferenceEquals(border.Child, fe): border.Child = null; break;
                    case Decorator decorator when ReferenceEquals(decorator.Child, fe): decorator.Child = null; break;
                    case ContentControl host when ReferenceEquals(host.Content, fe): host.Content = null; break;
                }
            }

            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; onClose(); } };

            var card = new Border
            {
                // Print Preview already used BackgroundBrush directly. The shared frame used
                // MenuBackgroundBrush, making every other generated window a different color.
                Background = Brush(owner, "BackgroundBrush", UiKit.Brush("BackgroundBrush")),
                CornerRadius = UiKit.RadWindow,
                Margin = Value(owner, "WindowFramePadding", new Thickness(0))
            };

            var root = new DockPanel();
            var titleBar = BuildTitleBar(win, owner, title, onClose, titleBarExtras);
            titleBar.Height = Value(owner, "DialogTitleBarHeight", 40.0);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(body);

            var grain = GrainTexture(owner);
            if (grain != null)
            {
                var grid = new Grid();
                double op = Application.Current?.Resources["GrainOpacity"] is double go ? go : 0.05;
                grid.Children.Add(new Border
                {
                    CornerRadius = UiKit.RadWindow, IsHitTestVisible = false, Opacity = op,
                    Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
                });
                grid.Children.Add(root);
                card.Child = grid;
            }
            else
            {
                var grid = new Grid();
                grid.Children.Add(root);
                card.Child = grid;
            }
            var framedContent = card.Child!;
            card.Child = null;
            return WrapContent(owner, framedContent, haloMargin);
        }

        internal static void AddBevels(Grid grid, Window? owner)
        {
            grid.Children.Add(new Border { IsHitTestVisible = false, BorderBrush = Brush(owner, "BevelLightBrush", Brushes.Transparent), BorderThickness = Value(owner, "BevelLightThickness", new Thickness(0)) });
            grid.Children.Add(new Border { IsHitTestVisible = false, BorderBrush = Brush(owner, "BevelDarkBrush", Brushes.Transparent), BorderThickness = Value(owner, "BevelDarkThickness", new Thickness(0)) });
        }
    }
}
