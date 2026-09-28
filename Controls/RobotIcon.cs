using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Avalanche.Controls
{
    /// <summary>
    /// Vector robot icon. Segoe MDL2 Assets / Fluent Icons have no robot glyph
    /// (the AI toolbar button and avatar previously borrowed E8A5, which renders
    /// as a generic diagnostic glyph), so the icon is drawn as geometry instead.
    /// Eyes and mouth are even-odd holes, so whatever is behind the icon shows
    /// through and the face reads correctly on the tinted avatar circle, the
    /// toolbar and the pane header in every theme.
    /// The geometry lives in a 24x24 design space and is uniformly scaled to the
    /// control's render size (centered when non-square).
    /// </summary>
    public sealed class RobotIcon : Control
    {
        private const double DesignSize = 24.0;

        private static readonly GeometryGroup RobotGeometry = CreateRobotGeometry();

        private static GeometryGroup CreateRobotGeometry()
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            // Head (rounded) with antenna stem + ball and two side ears.
            // The figures below must not overlap EACH OTHER (even-odd would
            // punch a hole where two solids intersect); they only need to sit
            // flush against the head, which stays a solid figure.
            g.Children.Add(new RectangleGeometry(new Rect(4.0, 7.0, 16.0, 12.0), 3.5, 3.5));      // head
            g.Children.Add(new EllipseGeometry(new Point(12.0, 2.2), 1.35, 1.35));                // antenna ball
            g.Children.Add(new RectangleGeometry(new Rect(11.42, 3.6, 1.16, 3.45), 0.58, 0.58));  // antenna stem
            g.Children.Add(new RectangleGeometry(new Rect(2.0, 10.6, 1.95, 4.6), 1.0, 1.0));      // left ear
            g.Children.Add(new RectangleGeometry(new Rect(20.05, 10.6, 1.95, 4.6), 1.0, 1.0));    // right ear
            // Holes (inside the head, so even-odd turns them into cut-outs).
            g.Children.Add(new EllipseGeometry(new Point(8.8, 12.3), 1.6, 1.6));                  // left eye
            g.Children.Add(new EllipseGeometry(new Point(15.2, 12.3), 1.6, 1.6));                 // right eye
            g.Children.Add(new RectangleGeometry(new Rect(9.2, 15.7, 5.6, 1.3), 0.65, 0.65));     // mouth
            g.Freeze();
            return g;
        }

        public RobotIcon()
        {
            // Purely decorative: never a tab stop and never focusable.
            Focusable = false;
            IsTabStop = false;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsNaN(Width) ? DesignSize : Width;
            double h = double.IsNaN(Height) ? DesignSize : Height;
            return new Size(Math.Min(w, availableSize.Width), Math.Min(h, availableSize.Height));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (RobotGeometry is null || RenderSize.Width <= 0 || RenderSize.Height <= 0)
                return;

            var brush = Foreground;
            if (brush is null)
                brush = Application.Current?.TryFindResource("PrimaryBrush") as Brush ?? Brushes.SteelBlue;

            double s = Math.Min(RenderSize.Width, RenderSize.Height) / DesignSize;
            if (double.IsNaN(s) || s <= 0)
                return;

            double tx = (RenderSize.Width - DesignSize * s) / 2.0;
            double ty = (RenderSize.Height - DesignSize * s) / 2.0;
            dc.PushTransform(new TranslateTransform(tx, ty));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(brush, null, RobotGeometry);
            dc.Pop();
            dc.Pop();
        }
    }
}
