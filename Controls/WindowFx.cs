using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Avalanche
{

    // Fade a window out on close: cancel the first close, animate opacity to 0, then close for real.
    // DialogResult is set before Closing fires, so it survives the deferral.
    internal static class WindowFx
    {
        public const int FadeMs = 150;

        // The reading navigator's beats: it asked for smooth and fast on BOTH ends.
        public const int PopMs = 130;      // close: fade + settle-away
        public const int PopOpenMs = 140;  // open: fade + grow-to-rest

        public static void EnableFadeClose(Window w, int ms = FadeMs, bool pop = false)
        {
            bool fading = false;
            bool readyToClose = false;
            w.Closing += (s, e) =>
            {
                if (readyToClose) return;  // our own post-fade Close - let it through
                e.Cancel = true;           // hold off the real close until the fade finishes
                if (fading) return;        // already fading - ignore repeat triggers
                fading = true;
                var anim = new DoubleAnimation(w.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (_, _) => { readyToClose = true; w.Close(); };

                if (pop)
                {
                    // The navigator's exit: fade AND settle away - the card eases
                    // down to 98.5% while it dissolves, so the close reads as a
                    // motion, not a blink. EaseIn holds still a beat and does the
                    // vanishing late, which is what "smooth" reads as going out.
                    var scale = new ScaleTransform(1, 1);
                    w.RenderTransform = scale;
                    w.RenderTransformOrigin = new Point(0.5, 0.33);
                    var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                    anim.EasingFunction = ease;
                    var shrink = new DoubleAnimation(1, 0.985, new Duration(TimeSpan.FromMilliseconds(ms)))
                    {
                        EasingFunction = ease
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                }

                w.BeginAnimation(UIElement.OpacityProperty, anim);
            };
        }

        // The matching entrance: fade in while the card grows from 96.5% to rest,
        // easing out so most of the motion lands in the first frames - fast, but
        // with a visible settle instead of an abrupt appearance. The transform is
        // removed on completion so the live window carries no scale (pixel snapping
        // stays exact while it is dragged or resized), and the opacity animation
        // hold is released so later code can set Opacity freely.
        public static void PlayOpenPop(Window w, int ms = PopOpenMs)
        {
            var scale = new ScaleTransform(0.965, 0.965);
            w.RenderTransform = scale;
            w.RenderTransformOrigin = new Point(0.5, 0.33);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
            var grow = new DoubleAnimation(0.965, 1, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
            fade.Completed += (_, _) => { w.BeginAnimation(UIElement.OpacityProperty, null); w.Opacity = 1; };
            grow.Completed += (_, _) => w.RenderTransform = null;
            w.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }
}
