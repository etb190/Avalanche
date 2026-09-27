using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Avalanche
{
    /// <summary>
    /// Where every flyout opens: the bottom corner of the content pane beside the rail.
    /// (From KillerUI/Shell/FlyoutPlacement.cs - the family flyout standard.)
    ///
    /// That rail-adjacent corner is the answer because of what bounds it, and all three matter:
    ///   - it is INSIDE the window, so a flyout never hangs over the desktop;
    ///   - it is ABOVE the footer, so the status bar is never covered;
    ///   - it is clear of the icon rail, so the rail buttons are never covered.
    /// The content pane is the one element bounded by all three at once, so flyouts are positioned
    /// against IT - not against the button, and not by any built-in placement mode.
    ///
    /// WHY NOT PlacementMode.Right / Top / etc: a Popup is its own top-level window, and WPF's
    /// built-in modes only ever avoid the SCREEN edge. They do not know the app window exists, let
    /// alone the footer or the rail. "Right of the button" opened flyouts over the desktop when the
    /// rail sat near the window's right edge; "Top" opened them over the status bar. Hours went
    /// into re-tuning offsets before it was clear no built-in mode can express the requirement.
    /// The requirement: do not obscure the icons, do not obscure the status bar, and put the
    /// flyout against the rail-adjacent corner of the content pane. (2026-07-30)
    ///
    /// WIRING (once, before the flyouts open):
    ///     FlyoutPlacement.UsePane(pane, railOnRight);  // the element the document content sits on
    /// then, each time a flyout opens:
    ///     FlyoutPlacement.Attach(themeMenu, themeButton);
    ///     themeMenu.IsOpen = true;
    ///
    /// The flyout's own card carries a 6px margin for its drop shadow (FlyoutCard in
    /// MainWindow.xaml), so pinning flush to the corner leaves the VISIBLE card sitting neatly just
    /// inside it. Do not add an inset here.
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>The content pane. Set once; every flyout positions against it.</summary>
        private static FrameworkElement? _pane;
        private static bool _alignRight;

        internal static void UsePane(FrameworkElement pane, bool alignRight)
        {
            _pane = pane;
            _alignRight = alignRight;
        }

        internal static void Attach(Popup popup, UIElement _)
        {
            popup.PlacementTarget = _pane;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => PaneCorner(popupSize, targetSize, _alignRight);
        }

        internal static void Attach(ContextMenu menu, UIElement _)
        {
            menu.PlacementTarget = _pane;
            menu.Placement = PlacementMode.Custom;
            // The shared ContextMenu style compensates ordinary pointer-anchored menus for its
            // enlarged shadow halo. Rail flyouts use exact pane-corner coordinates instead, so
            // clear those global offsets and account for the halo in BottomLeftOfPane.
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => PaneCorner(popupSize, targetSize, _alignRight);
        }

        /// <summary>
        /// Coordinates are relative to the pane's top-left. The horizontal coordinate mirrors
        /// between pane edges, while y puts the flyout's bottom above the footer.
        /// </summary>
        internal static CustomPopupPlacement[] PaneCorner(
            Size popupSize, Size targetSize, bool alignRight)
        {
            // ContextMenu's template now reserves 22px left, 18px top, and 26px bottom for its
            // shadow. Position the VISIBLE card at the same 6px pane inset used before that halo
            // grew. On the right, mirror the same inset against the pane's right edge.
            double x = alignRight ? targetSize.Width - popupSize.Width + 16 : -16;
            double y = targetSize.Height - popupSize.Height + 20;

            // A flyout taller than the pane would otherwise start above it and run over the
            // toolbar; pin it to the pane's top instead and let it use the height it has.
            if (y < 0) y = 0;

            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        }
    }
}
