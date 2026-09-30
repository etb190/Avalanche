// Shell/SurfaceHealth.cs — BLACK-SCREEN V2: closed-loop self-healing.
//
// V1 (SurfaceResurrection.cs) reacted to power/minimize EVENTS and kicked the
// surface on a fixed schedule. That is not enough: some triggers are invisible
// to user-mode code (GPU TDR, HDR toggle, driver quirk), so the window could
// still go black with no event ever firing.
//
// V2 closes the loop:
//   1. PROBE   — every 10 s, capture the window's own rendered output
//                (PrintWindow + PW_RENDERFULLCONTENT) and compute pixel stats.
//   2. DETECT  — if the capture is statistically all-black while the window is
//                visible, the surface is dead. No guessing about causes.
//   3. ESCALATE— run 5 recovery mechanisms from weakest to strongest, re-probing
//                after each, and stop the moment the surface is proven alive:
//                  step 1  RedrawWindow synchronous full repaint
//                  step 2  render-target nudge (height +1 then restore)
//                  step 3  RenderOptions.ProcessRenderMode SoftwareOnly->Default
//                  step 4  RootVisual detach + re-attach (rebuilds composition)
//                  step 5  window hide + show (forces DWM re-acquisition)
//   4. RECORD  — every trigger, detection and outcome goes to
//                %LOCALAPPDATA%\SurfaceHealth\surface-health.log (black-box).
//
// Integration checklist (verify against real repo when restored):
//   1. Align the namespace below with Shell/SurfaceResurrection.cs.
//   2. MainWindow SourceInitialized:        StartSurfaceHealth();
//   3. SurfaceResurrection.MarkDisplayWake: NotifySurfaceEvent(reason);
//      SurfaceResurrection.OnWindowRestoredFromMinimize: NotifySurfaceEvent("restore from minimize");
//   4. MainWindow OnClosed:                 ShutdownSurfaceHealth();
//   5. Optional: SurfaceHealthLog.SetSink(line => existingLogger.Debug(line));
//   6. Optional escape hatch in App startup: "--sw-render" =>
//      RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;  (100% immune
//      to device loss; trades GPU compositing for CPU rendering)

namespace Avalanche
{
    using System;
    using System.Runtime.InteropServices;
    using System.Windows;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;
    using Avalanche.Services;

    public partial class MainWindow
    {
        // ---- state ----------------------------------------------------------
        private DispatcherTimer? _shSweepTimer;
        private DateTime _shStartupAt = DateTime.UtcNow;
        private bool _shLadderRunning;
        private int _shLadderStep;
        private int _shBurstGen;
        private int _shProbeCount;
        private DateTime _shCooldownUntil = DateTime.MinValue;
        private double _shBaselineMean;
        private bool _shHasBaseline;

        // GDI capture buffer cache — recreated only when window size changes.
        private IntPtr _shCapDc = IntPtr.Zero;
        private IntPtr _shCapBmp = IntPtr.Zero;
        private IntPtr _shCapBits = IntPtr.Zero;
        private int _shCapW;
        private int _shCapH;

        private static readonly double[] ShBurstDelaysSec = { 1, 4, 10, 20, 40 };

        private const int ShRdwInvalidate = 0x0001;
        private const int ShRdwErase = 0x0004;
        private const int ShRdwAllChildren = 0x0080;
        private const int ShRdwUpdateNow = 0x0100;
        private const int ShRdwFrame = 0x0400;
        private const uint ShPwRenderFullContent = 0x0002;
        private const int ShSwHide = 0;
        private const int ShSwShow = 5;
        private const uint ShSwpNoSize = 0x0001;
        private const uint ShSwpNoMove = 0x0002;
        private const uint ShSwpNoZOrder = 0x0004;
        private const uint ShSwpNoActivate = 0x0010;
        private const uint ShSwpNoCopyBits = 0x0100;

        // ---- public API -----------------------------------------------------

        /// <summary>Call once from SourceInitialized. Starts the 10 s probe sweep.</summary>
        public void StartSurfaceHealth()
        {
            if (_shSweepTimer != null)
            {
                return;
            }

            _shStartupAt = DateTime.UtcNow;
            _shSweepTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _shSweepTimer.Tick += (sender, e) => ShSweepTick();
            _shSweepTimer.Start();
            ShStartHangWatch();
            bool software = Application.Current is App && App.SoftwareRenderingForced;
            SurfaceHealthLog.Log(software
                ? "SurfaceHealth sweep started (probe every 10s); render mode: SOFTWARE - " +
                  "device-loss bug class structurally impossible, probes are a backstop only"
                : "SurfaceHealth sweep started (probe every 10s, escalation ladder armed); " +
                  "render mode: HARDWARE");
            SurfaceHealthLog.Log("UI-thread hang watchdog armed (probe every 5s, 10s timeout)");
        }

        /// <summary>
        /// Call from every event that plausibly precedes surface loss (monitor
        /// power on, system resume, restore from minimize, display changed).
        /// Schedules verification probes so a dead surface is caught within
        /// seconds even if the event itself was harmless.
        /// </summary>
        public void NotifySurfaceEvent(string reason)
        {
            SurfaceHealthLog.Log("trigger: " + reason);
            int gen = ++_shBurstGen;
            foreach (double delaySec in ShBurstDelaysSec)
            {
                double at = delaySec;
                ShRunOnce(TimeSpan.FromSeconds(delaySec), () => ShBurstProbe(gen, at));
            }
        }

        /// <summary>Call from OnClosed. Stops timers and frees the GDI buffer.</summary>
        public void ShutdownSurfaceHealth()
        {
            if (_shSweepTimer != null)
            {
                _shSweepTimer.Stop();
                _shSweepTimer = null;
            }

            ShStopHangWatch();
            ShFreeCapture();
            SurfaceHealthLog.Log("SurfaceHealth shutdown");
        }

        // ---- UI-thread hang watchdog -----------------------------------------
        // The probe sweep runs ON the dispatcher, so it is blind to the failure mode
        // where the UI thread itself is wedged (frozen, often black window; the user
        // ends up killing the process from Task Manager). A dedicated background
        // thread probes the dispatcher from the outside: every 5 s it posts a no-op
        // at Send priority and waits up to 10 s. A missed probe is logged with the
        // ongoing stall duration, and recovery is logged when the thread answers.
        private Thread? _shHangWatch;
        private volatile bool _shHangStop;

        private void ShStartHangWatch()
        {
            if (_shHangWatch != null)
            {
                return;
            }

            _shHangStop = false;
            _shHangWatch = new Thread(ShHangWatchLoop)
            {
                IsBackground = true,
                Name = "SurfaceHealth.HangWatch"
            };
            _shHangWatch.Start();
        }

        private void ShStopHangWatch()
        {
            _shHangStop = true;
            _shHangWatch = null; // background thread; exits between sleeps
        }

        private void ShHangWatchLoop()
        {
            Dispatcher dispatcher = Dispatcher;
            while (!_shHangStop)
            {
                Thread.Sleep(5000);
                if (_shHangStop)
                {
                    return;
                }

                try
                {
                    DateTime submitted = DateTime.UtcNow;
                    var probe = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send).Task;
                    if (!probe.Wait(TimeSpan.FromSeconds(10)))
                    {
                        DateTime stallStart = submitted;
                        double loggedAt = 0;
                        bool evidenceDumped = false;
                        while (!_shHangStop)
                        {
                            Thread.Sleep(2000);
                            if (probe.IsCompleted)
                            {
                                break;
                            }

                            double stalledFor = (DateTime.UtcNow - stallStart).TotalSeconds;
                            if (stalledFor - loggedAt >= 30)
                            {
                                loggedAt = stalledFor;
                                SurfaceHealthLog.Log(string.Format(
                                    "UI THREAD UNRESPONSIVE for {0:F0}s and counting (stall began {1:HH:mm:ss})",
                                    stalledFor, stallStart.ToLocalTime()));
                                if (!evidenceDumped)
                                {
                                    // The dispatcher is wedged: no sweep, no clipboard,
                                    // no dialogs. The background thread still has file
                                    // I/O - drop the evidence on the Desktop before
                                    // the user force-kills the process.
                                    evidenceDumped = true;
                                    DiagnosticsBundle.Dump(
                                        "UI thread unresponsive " + (int)stalledFor + "s");
                                }
                            }
                        }

                        if (_shHangStop)
                        {
                            return;
                        }

                        SurfaceHealthLog.Log(string.Format(
                            "UI thread responsive again after a {0:F0}s stall",
                            (DateTime.UtcNow - stallStart).TotalSeconds));
                    }
                }
                catch
                {
                    // dispatcher shutting down - the main thread will stop this loop
                }
            }
        }

        // ---- sweep + burst ---------------------------------------------------

        private void ShSweepTick()
        {
            if ((DateTime.UtcNow - _shStartupAt).TotalSeconds < 8)
            {
                return; // let the first render settle before judging
            }

            if (_shLadderRunning || DateTime.UtcNow < _shCooldownUntil)
            {
                return;
            }

            SurfaceHealthProbeResult r = ShProbeSurface();
            if (!r.Ok)
            {
                return; // window legitimately not renderable right now
            }

            _shProbeCount++;
            if (ShIsBlack(r))
            {
                ShBeginEscalation("sweep", r);
                return;
            }

            ShMaybeBaseline(r);
            if (_shProbeCount % 45 == 0)
            {
                SurfaceHealthLog.Log(string.Format(
                    "heartbeat: probe #{0} healthy (mean={1:F1} max={2} bright={3:P2})",
                    _shProbeCount, r.Mean, r.Max, r.BrightFrac));
            }
        }

        private void ShBurstProbe(int gen, double atSec)
        {
            if (gen != _shBurstGen || _shLadderRunning || DateTime.UtcNow < _shCooldownUntil)
            {
                return;
            }

            SurfaceHealthProbeResult r = ShProbeSurface();
            if (r.Ok && ShIsBlack(r))
            {
                ShBeginEscalation("wake-burst+" + atSec + "s", r);
            }
        }

        // ---- detection -------------------------------------------------------

        private bool ShIsBlack(SurfaceHealthProbeResult r)
        {
            // Absolute rule: a visible window whose entire capture is ~pure black
            // is a dead surface. A healthy dark-themed window still has text and
            // accents well above this floor.
            if (r.Mean < 5.0 && r.Max < 30)
            {
                return true;
            }

            // Baseline-relative rule: catches themes whose background is nearly
            // black by comparing against the window's own healthy mean.
            if (_shHasBaseline && r.Mean < _shBaselineMean * 0.15 && r.BrightFrac < 0.005)
            {
                return true;
            }

            return false;
        }

        private void ShMaybeBaseline(SurfaceHealthProbeResult r)
        {
            if (r.Mean < 8.0 || r.BrightFrac < 0.005)
            {
                return; // not a sane reference frame
            }

            if (!_shHasBaseline)
            {
                _shHasBaseline = true;
                _shBaselineMean = r.Mean;
                SurfaceHealthLog.Log(string.Format(
                    "baseline captured: mean={0:F1} bright={1:P2}", r.Mean, r.BrightFrac));
            }
            else if (_shProbeCount % 30 == 0)
            {
                _shBaselineMean = r.Mean; // keep up with theme/layout changes
            }
        }

        // ---- escalation ladder -----------------------------------------------

        private void ShBeginEscalation(string cause, SurfaceHealthProbeResult r)
        {
            if (_shLadderRunning)
            {
                return;
            }

            SurfaceHealthLog.Log(string.Format(
                "BLACK SURFACE DETECTED (cause={0} mean={1:F1} max={2} bright={3:P2}) — escalation started",
                cause, r.Mean, r.Max, r.BrightFrac));
            // Evidence now, not later: the UI thread is alive at this point, so the
            // bundle lands on the Desktop AND the clipboard (copy:true keeps it
            // alive even if the user force-kills the frozen-looking app).
            DumpSurfaceEvidence("black surface: " + cause);
            _shLadderRunning = true;
            _shLadderStep = 0;
            ShAdvanceLadder();
        }

        /// <summary>Writes the diagnostics bundle and copies it to the clipboard.
        /// UI thread only; the Desktop file survives even when the clipboard and
        /// the process do not.</summary>
        private void DumpSurfaceEvidence(string reason)
        {
            try
            {
                string bundle = DiagnosticsBundle.Dump(reason);
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, bundle), true);
                SurfaceHealthLog.Log("diagnostics bundle copied to clipboard");
            }
            catch
            {
                // clipboard can fail (locked session, RDP); the Desktop file remains
            }
        }

        private void ShAdvanceLadder()
        {
            if (!_shLadderRunning)
            {
                return;
            }

            // Verify the previous step's effect before escalating further.
            if (_shLadderStep > 0)
            {
                SurfaceHealthProbeResult r = ShProbeSurface();
                if (r.Ok && !ShIsBlack(r))
                {
                    _shLadderRunning = false;
                    _shHasBaseline = false; // recapture after re-render
                    SurfaceHealthLog.Log("SURFACE RECOVERED after ladder step " + _shLadderStep);
                    return;
                }
            }

            if (_shLadderStep >= 5)
            {
                _shLadderRunning = false;
                _shCooldownUntil = DateTime.UtcNow.AddMinutes(5);
                SurfaceHealthLog.Log(
                    "CRITICAL: all 5 recovery steps failed — 5 min cooldown. " +
                    "If the screen is still black, this data point (probe says alive vs user sees black) " +
                    "identifies a DWM-side cause; consider launching with --sw-render.");
                return;
            }

            int step = ++_shLadderStep;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SurfaceHealthLog.Log("ladder step " + step + ": " + ShStepName(step));

            try
            {
                switch (step)
                {
                    case 1:
                        ShNative.RedrawWindow(
                            hwnd,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            ShRdwInvalidate | ShRdwErase | ShRdwFrame | ShRdwAllChildren | ShRdwUpdateNow);
                        break;

                    case 2:
                        if (ShNative.GetWindowRect(hwnd, out ShNative.RECT rc) && !ShNative.IsIconic(hwnd))
                        {
                            int w = rc.Right - rc.Left;
                            int h = rc.Bottom - rc.Top;
                            uint flags = ShSwpNoMove | ShSwpNoZOrder | ShSwpNoActivate;
                            ShNative.SetWindowPos(hwnd, IntPtr.Zero, rc.Left, rc.Top, w, h + 1, flags);
                            ShNative.SetWindowPos(
                                hwnd,
                                IntPtr.Zero,
                                rc.Left,
                                rc.Top,
                                w,
                                h,
                                flags | ShSwpNoCopyBits);
                        }

                        break;

                    case 3:
                        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                        RenderOptions.ProcessRenderMode = RenderMode.Default;
                        InvalidateVisual();
                        UpdateLayout();
                        break;

                    case 4:
                        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
                        if (source != null)
                        {
                            Visual? root = source.RootVisual;
                            if (root != null)
                            {
                                source.RootVisual = null!; // detach: WPF permits null at runtime here
                                source.RootVisual = root;  // re-attach forces composition rebuild
                                InvalidateVisual();
                                UpdateLayout();
                            }
                        }

                        break;

                    case 5:
                        ShNative.ShowWindow(hwnd, ShSwHide);
                        ShNative.ShowWindow(hwnd, ShSwShow);
                        break;
                }
            }
            catch (Exception ex)
            {
                SurfaceHealthLog.Log("ladder step " + step + " threw: " + ex.GetType().Name + " " + ex.Message);
            }

            ShRunOnce(TimeSpan.FromMilliseconds(600), ShAdvanceLadder);
        }

        private static string ShStepName(int step)
        {
            switch (step)
            {
                case 1: return "RedrawWindow full repaint";
                case 2: return "render-target nudge (h+1/restore)";
                case 3: return "ProcessRenderMode SoftwareOnly->Default";
                case 4: return "RootVisual re-attach";
                case 5: return "hide/show window";
                default: return "step " + step;
            }
        }

        // ---- capture + analysis ----------------------------------------------

        private SurfaceHealthProbeResult ShProbeSurface()
        {
            if (!IsLoaded || WindowState == WindowState.Minimized)
            {
                return SurfaceHealthProbeResult.Fail("window not renderable (unloaded or minimized)");
            }
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return SurfaceHealthProbeResult.Fail("no hwnd yet");
            }

            if (!ShNative.GetWindowRect(hwnd, out ShNative.RECT rc))
            {
                return SurfaceHealthProbeResult.Fail("GetWindowRect failed");
            }

            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 60 || h < 60)
            {
                return SurfaceHealthProbeResult.Fail("window too small to judge");
            }

            if (!ShEnsureCapture(w, h, out string? capErr))
            {
                return SurfaceHealthProbeResult.Fail(capErr);
            }

            if (!ShNative.PrintWindow(hwnd, _shCapDc, ShPwRenderFullContent))
            {
                return SurfaceHealthProbeResult.Fail("PrintWindow returned false");
            }

            int stride = ((w * 32 + 31) / 32) * 4;
            int stepX = Math.Max(3, w / 96);
            int stepY = Math.Max(3, h / 60);
            long sum = 0;
            int samples = 0;
            int max = 0;
            int bright = 0;

            for (int y = 2; y < h - 2; y += stepY)
            {
                int rowBase = y * stride;
                for (int x = 2; x < w - 2; x += stepX)
                {
                    int off = rowBase + (x * 4);
                    byte b = Marshal.ReadByte(_shCapBits, off);
                    byte g = Marshal.ReadByte(_shCapBits, off + 1);
                    byte rByte = Marshal.ReadByte(_shCapBits, off + 2);
                    int lum = ((rByte * 299) + (g * 587) + (b * 114)) / 1000;
                    sum += lum;
                    samples++;
                    if (lum > max)
                    {
                        max = lum;
                    }

                    if (lum > 60)
                    {
                        bright++;
                    }
                }
            }

            if (samples == 0)
            {
                return SurfaceHealthProbeResult.Fail("no samples");
            }

            double mean = (double)sum / samples;
            double brightFrac = (double)bright / samples;
            return SurfaceHealthProbeResult.Healthy(mean, max, brightFrac);
        }

        private bool ShEnsureCapture(int w, int h, out string? error)
        {
            if (_shCapDc != IntPtr.Zero && _shCapW == w && _shCapH == h)
            {
                error = null;
                return true;
            }

            ShFreeCapture();

            ShNative.BITMAPINFO bmi = new ShNative.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(ShNative.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            _shCapDc = ShNative.CreateCompatibleDC(IntPtr.Zero);
            if (_shCapDc == IntPtr.Zero)
            {
                error = "CreateCompatibleDC failed";
                return false;
            }

            _shCapBmp = ShNative.CreateDIBSection(_shCapDc, ref bmi, 0, out _shCapBits, IntPtr.Zero, 0);
            if (_shCapBmp == IntPtr.Zero || _shCapBits == IntPtr.Zero)
            {
                ShFreeCapture();
                error = "CreateDIBSection failed";
                return false;
            }

            ShNative.SelectObject(_shCapDc, _shCapBmp);
            _shCapW = w;
            _shCapH = h;
            error = null;
            return true;
        }

        private void ShFreeCapture()
        {
            if (_shCapBmp != IntPtr.Zero)
            {
                ShNative.DeleteObject(_shCapBmp);
                _shCapBmp = IntPtr.Zero;
            }

            if (_shCapDc != IntPtr.Zero)
            {
                ShNative.DeleteDC(_shCapDc);
                _shCapDc = IntPtr.Zero;
            }

            _shCapBits = IntPtr.Zero;
            _shCapW = 0;
            _shCapH = 0;
        }

        // ---- helpers ----------------------------------------------------------

        private void ShRunOnce(TimeSpan delay, Action action)
        {
            var timer = new DispatcherTimer(
                delay,
                DispatcherPriority.Background,
                (sender, e) =>
                {
                    var t = (DispatcherTimer)sender!;
                    t.Stop();
                    action();
                },
                Dispatcher);
            timer.Start();
        }

        // ---- Win32 (nested class: zero collision with existing P/Invoke) ------

        private static class ShNative
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFO
            {
                public BITMAPINFOHEADER bmiHeader;
                public uint bmiColors;
            }

            [DllImport("user32.dll")]
            internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

            [DllImport("user32.dll")]
            internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

            [DllImport("user32.dll")]
            internal static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

            [DllImport("user32.dll")]
            internal static extern bool SetWindowPos(
                IntPtr hWnd,
                IntPtr hWndInsertAfter,
                int x,
                int y,
                int cx,
                int cy,
                uint flags);

            [DllImport("gdi32.dll")]
            internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            internal static extern IntPtr CreateDIBSection(
                IntPtr hdc,
                ref BITMAPINFO pbmi,
                uint iUsage,
                out IntPtr ppvBits,
                IntPtr hSection,
                uint dwOffset);

            [DllImport("gdi32.dll")]
            internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

            [DllImport("gdi32.dll")]
            internal static extern bool DeleteObject(IntPtr hObject);

            [DllImport("gdi32.dll")]
            internal static extern bool DeleteDC(IntPtr hdc);
        }
    }

    /// <summary>Outcome of one surface probe.</summary>
    internal readonly struct SurfaceHealthProbeResult
    {
        public readonly bool Ok;
        public readonly double Mean;
        public readonly int Max;
        public readonly double BrightFrac;
        public readonly string? Why;

        private SurfaceHealthProbeResult(bool ok, double mean, int max, double brightFrac, string? why)
        {
            Ok = ok;
            Mean = mean;
            Max = max;
            BrightFrac = brightFrac;
            Why = why;
        }

        public static SurfaceHealthProbeResult Fail(string? why)
        {
            return new SurfaceHealthProbeResult(false, 0, 0, 0, why);
        }

        public static SurfaceHealthProbeResult Healthy(double mean, int max, double brightFrac)
        {
            return new SurfaceHealthProbeResult(true, mean, max, brightFrac, null);
        }
    }
}
