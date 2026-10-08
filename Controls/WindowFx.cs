using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Avalanche
{

    // Fade a window out on close: cancel the first close, animate opacity to 0, then close for real.
    // The deferral is safe for the modeless windows that use it - but a MODAL window must not
    // assign DialogResult before the fade: WPF resets DialogResult to null whenever a close is
    // cancelled, and the deferral IS a cancelled close. The themed FileDialog learned this first
    // (its _pendingResult is assigned only in the fade's completion) and the extract prompt runs
    // the same deferral over PlayClosePop below.
    internal static class WindowFx
    {
        public const int FadeMs = 150;

        // The reading navigator's beats: it asked for smooth and fast on BOTH ends.
        public const int PopMs = 130;      // close: fade + settle-away
        public const int PopOpenMs = 140;  // open: fade + grow-to-rest

        // WPF forbids RenderTransform on the Window itself - Window.CoerceRenderTransform
        // rejects every value with "Transform is not valid for Window". The pop scale
        // therefore plays on the window's content root: the navigator is custom-chromed,
        // so its content is the whole visible window and scaling it reads as scaling
        // the window.
        private static FrameworkElement? ScaleRoot(Window w) => w.Content as FrameworkElement;

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
                    var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                    anim.EasingFunction = ease;
                    var shrink = new DoubleAnimation(1, 0.985, new Duration(TimeSpan.FromMilliseconds(ms)))
                    {
                        EasingFunction = ease
                    };
                    if (ScaleRoot(w) is FrameworkElement root)
                    {
                        var scale = new ScaleTransform(1, 1);
                        root.RenderTransform = scale;
                        root.RenderTransformOrigin = new Point(0.5, 0.33);
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                    }
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
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            // The fade is armed FIRST: a window born at Opacity 0 becomes
            // visible even if the scale step below were ever skipped - the
            // entrance never depends on the transform for its visibility.
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
            fade.Completed += (_, _) => { w.BeginAnimation(UIElement.OpacityProperty, null); w.Opacity = 1; };
            w.BeginAnimation(UIElement.OpacityProperty, fade);

            if (ScaleRoot(w) is FrameworkElement root)
            {
                var scale = new ScaleTransform(0.965, 0.965);
                root.RenderTransform = scale;
                root.RenderTransformOrigin = new Point(0.5, 0.33);
                var grow = new DoubleAnimation(0.965, 1, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
                grow.Completed += (_, _) => root.RenderTransform = null;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        // The pop exit, offered WITHOUT the Closing hook. EnableFadeClose's own
        // completion callback has no seat for the caller's last word, and a modal
        // window needs one: its DialogResult may only be assigned once the fade
        // has landed and no close can be cancelled anymore (a cancelled close
        // resets DialogResult to null - see the class comment). The caller drives
        // the deferral in its OnClosing and hands this the motion; `completed`
        // fires when the window may finally close for real.
        public static void PlayClosePop(Window w, int ms, Action completed)
        {
            var anim = new DoubleAnimation(w.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) => completed();

            if (ScaleRoot(w) is FrameworkElement root)
            {
                var shrink = new DoubleAnimation(1, 0.985, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                var scale = new ScaleTransform(1, 1);
                root.RenderTransform = scale;
                root.RenderTransformOrigin = new Point(0.5, 0.33);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            }

            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }
}
