using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Avalanche
{
    // ============================================================
    // Surface resurrection - makes the black-client-area bug structurally impossible.
    // ============================================================
    // Symptom this cures: after the app sat minimized for a while (display off / lock /
    // sleep in between), restoring it showed a pitch-black client area while the custom
    // caption buttons still drew. Root cause: DWM hands the window back a discarded or
    // device-lost redirection surface and milcore only re-presents the regions it still
    // considers dirty - small hover redraws (the caption buttons) come back, everything
    // else stays black.
    //
    // The earlier fix (SWP_FRAMECHANGED|SWP_NOCOPYBITS + InvalidateVisual on restore,
    // SystemEvents resume/display-change) reduced the frequency but missed the two real
    // triggers and never rebuilt the render target:
    //   1. Monitor power off/on fires NEITHER PowerModeChanged NOR DisplaySettingsChanged.
    //      It is only observable via WM_POWERBROADCAST(PBT_POWERSETTINGCHANGE) after
    //      RegisterPowerSettingNotification(GUID_MONITOR_POWER_ON / GUID_CONSOLE_DISPLAY_STATE).
    //   2. InvalidateVisual re-renders the WPF scene, but if milcore's D3D device was lost
    //      while the window was hidden, the dead target keeps being presented. Only a size
    //      change (WM_SIZE -> HwndTarget reallocates the target) or a render-pipeline
    //      teardown rebuilds it.
    //
    // Defense in depth - every wake trigger fans into the same staggered 4-pass schedule:
    //   pass 1 (Loaded):    full Win32 redraw + 1px size nudge + (full runs) pipeline toggle
    //   pass 2 (+350ms):    full Win32 redraw
    //   pass 3 (+1200ms):   full Win32 redraw + (full runs) pipeline toggle
    //   pass 4 (+3500ms):   full Win32 redraw - final safety net
    // "Full" runs (minimize >= 30s, or any wake event seen since minimize) additionally tear
    // the process render pipeline down to SoftwareOnly and back, forcing milcore to recreate
    // the D3D device and re-upload every bitmap from scratch. That combination leaves no
    // path back to a stale surface.
    public partial class MainWindow
    {
        private const int WM_POWERBROADCAST      = 0x0218;
        private const int PBT_APMSUSPEND         = 0x0004;
        private const int PBT_APMRESUMESUSPEND   = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        private const uint RDW_INVALIDATE  = 0x0001;
        private const uint RDW_ERASE       = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW   = 0x0100;
        private const uint RDW_FRAME       = 0x0400;

        // Values from learn.microsoft.com/en-us/windows/win32/power/power-setting-guids
        private static readonly Guid GuidMonitorPowerOn      = new("02731015-4510-4526-99E6-E5A17EBD1AEA");
        private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public int DataLength;
            public int Data;   // DWORD for the display GUIDs: 0 = off, 1 = on
        }

        [LibraryImport("user32.dll", EntryPoint = "RegisterPowerSettingNotification")]
        private static partial IntPtr RegisterPowerSettingNotification(IntPtr hwnd, in Guid powerSetting, int flags);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterPowerSettingNotification")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterPowerSettingNotification(IntPtr handle);

        [LibraryImport("user32.dll", EntryPoint = "RedrawWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RedrawWindow(IntPtr hwnd, IntPtr rectUpdate, IntPtr hrgnUpdate, uint flags);

        // ---- resurrection state ----
        private DateTime? _minimizedAt;        // when the running minimize began (long-absence gate)
        private bool _surfaceSuspect;          // a wake event occurred that could have killed the surface
        private int _resGen;                   // 0 = idle; else generation of the active run
        private bool _resFull;                 // active run includes the pipeline toggle
        private bool _resToggleDone;           // toggle already executed within the active run
        private readonly List<IntPtr> _powerNotifyHandles = new();

        // Subscribes the HWND to display power notifications so monitor off/on is visible
        // to us (SystemEvents cannot see it). Called once from SourceInitialized.
        private void RegisterDisplayWakeNotifications(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            foreach (var guid in new[] { GuidMonitorPowerOn, GuidConsoleDisplayState })
            {
                try
                {
                    var handle = RegisterPowerSettingNotification(hwnd, guid, DEVICE_NOTIFY_WINDOW_HANDLE);
                    if (handle != IntPtr.Zero) _powerNotifyHandles.Add(handle);
                }
                catch { /* diagnostics only; the other wake paths still cover us */ }
            }
        }

        private void UnregisterDisplayWakeNotifications()
        {
            foreach (var handle in _powerNotifyHandles)
            {
                try { UnregisterPowerSettingNotification(handle); }
                catch { /* window is going away anyway */ }
            }
            _powerNotifyHandles.Clear();
        }

        // WM_POWERBROADCAST arm of the wake detection. Returns true when the message was
        // consumed (we never deny anything, so consuming is equivalent to default handling).
        private bool TryHandlePowerBroadcast(int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != WM_POWERBROADCAST) return false;
            long kind = wParam.ToInt64();

            if (kind == PBT_APMRESUMEAUTOMATIC || kind == PBT_APMRESUMESUSPEND)
            {
                // System-wide sleep/resume.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => MarkDisplayWake("system resume")));
                return true;
            }

            if (kind == PBT_POWERSETTINGCHANGE)
            {
                bool displayOn = false;
                try
                {
                    var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                    if ((setting.PowerSetting == GuidMonitorPowerOn
                         || setting.PowerSetting == GuidConsoleDisplayState))
                    {
                        if (setting.Data != 0)
                            displayOn = true;                       // display is back - resurrect now
                        else
                            _surfaceSuspect = true;                 // display left; remember for the restore
                    }
                }
                catch { /* malformed payload; the resume/unlock paths still cover us */ }

                if (displayOn)
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => MarkDisplayWake("monitor powered on")));
                }
                return true;
            }

            if (kind == PBT_APMSUSPEND) { _surfaceSuspect = true; return true; }
            return true;
        }

        // Session unlock (Win+L style) - SystemEvents sees this even when the power
        // notifications above are unavailable.
        private void OnSystemSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionUnlock) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => MarkDisplayWake("session unlock")));
        }

        // Any wake event that could have destroyed the redirection surface or milcore's
        // device while our window was not rendering. While minimized we only set the flag -
        // painting now would be dropped (the window is iconic), so the restore path runs the
        // full resurrection. While visible (app was in the background during the disruption)
        // the staggered schedule starts immediately.
        private void MarkDisplayWake(string reason)
        {
            // SurfaceHealth: record the trigger and start burst-verification probes so a
            // surface that died around this event is caught and healed within seconds.
            NotifySurfaceEvent(reason);
            _surfaceSuspect = true;
            if (WindowState == WindowState.Minimized) return;
            QueueSurfaceResurrection(full: true, reason);
        }

        // Restore from minimize. Runs a full resurrection after a real absence (>= 30s) or
        // whenever a wake event was seen; a quick alt-tab only gets the cheap kicks, so
        // everyday minimize/restore stays instant.
        private void OnWindowRestoredFromMinimize()
        {
            // SurfaceHealth: burst-verify after restore - the highest-risk transition.
            NotifySurfaceEvent("restore from minimize");
            var started = _minimizedAt;
            _minimizedAt = null;
            bool longAbsence = started.HasValue
                && (DateTime.UtcNow - started.Value).TotalSeconds >= 30;
            QueueSurfaceResurrection(full: longAbsence || _surfaceSuspect, "restore from minimize");
        }

        // Schedules one staggered 4-pass resurrection run. Additional triggers while a run
        // is active only upgrade it to full (its remaining passes already cover them), and
        // stale generations are ignored inside the passes - bursts of wake events (monitor
        // on + resume + unlock in one wake) cannot stack passes.
        private void QueueSurfaceResurrection(bool full, string reason)
        {
            if (full) _surfaceSuspect = true;
            if (_resGen != 0)
            {
                _resFull |= full;
                return;
            }
            _resGen = 1;                                 // any non-zero generation marks the run active
            _resFull = full;
            _resToggleDone = false;
            int gen = _resGen;

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => RunResurrectionPass(1, gen)));
            ScheduleResurrectionPass(2, gen, 350);
            ScheduleResurrectionPass(3, gen, 1200);
            ScheduleResurrectionPass(4, gen, 3500);
        }

        // Delayed passes ride DispatcherTimer: the BeginInvoke(TimeSpan, ...) overloads all
        // require a params object[] tail, and a parameterless Action pushed through that tail
        // is not safely invocable. One-shot timers keep the passes alive across the real-time
        // gaps that defeat the restore-animation timing races.
        private void ScheduleResurrectionPass(int pass, int gen, int delayMs)
        {
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(delayMs), System.Windows.Threading.DispatcherPriority.Background,
                (sender, _) =>
                {
                    if (sender is System.Windows.Threading.DispatcherTimer t) t.Stop();
                    if (pass == 4) FinishResurrectionRun(gen);
                    else RunResurrectionPass(pass, gen);
                },
                Dispatcher);
            timer.Start();
        }

        private void RunResurrectionPass(int pass, int gen)
        {
            if (gen != _resGen) return;                          // superseded / run finished
            if (WindowState == WindowState.Minimized) return;    // iconic; the restore path repaints
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                KickSurface(hwnd);

                // The 1px nudge is the only thing that reliably rebuilds a render target whose
                // D3D device was lost while the window was hidden: WM_SIZE with a changed size
                // makes WPF's HwndTarget reallocate its composition target and re-present
                // everything. Never nudge a maximized/fullscreen window (it would fight the
                // maximize geometry) or an in-flight drag.
                if (WindowState == WindowState.Normal && !_fullScreen && !_inWindowSizeMove)
                    NudgeRenderTarget(hwnd);

                InvalidateVisual();
                UpdateLayout();
            }
            catch { /* a repaint must never crash the app */ }

            // Nuclear option on full runs: drop the whole process to software rendering and
            // back. Milcore rebuilds the device and re-uploads every bitmap (the frozen page
            // WriteableBitmaps included), so even a device-loss DWM never surfaced survives.
            // Runs once on pass 1 and (as insurance against a device loss that happened again
            // in between) once on pass 3.
            if (_resFull && (pass == 1 || pass == 3) && !_resToggleDone)
            {
                _resToggleDone = true;
                try
                {
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                    RenderOptions.ProcessRenderMode = RenderMode.Default;
                    _surfaceSuspect = false;   // surface provably rebuilt from scratch
                }
                catch { /* never crash on diagnostics */ }
            }
        }

        private void FinishResurrectionRun(int gen)
        {
            if (gen != _resGen) return;
            RunResurrectionPass(4, gen);
            _resGen = 0;
            _resFull = false;
            _resToggleDone = false;
        }

        // The cheap, always-safe kick, executed on every pass:
        //   1. RedrawWindow(RDW_UPDATENOW) synchronously pumps a full-frame paint - frame,
        //      client and every child - instead of waiting for the idle WM_PAINT cycle.
        //   2. SWP_FRAMECHANGED re-runs WM_NCCALCSIZE (our custom chrome) and SWP_NOCOPYBITS
        //      refuses the stale saved bits so Windows repaints the client from scratch
        //      instead of blitting the black surface back.
        //   3. InvalidateVisual + UpdateLayout dirty the WPF tree so the next composition
        //      pass re-presents the whole scene.
        private void KickSurface(IntPtr hwnd)
        {
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
                | SWP_FRAMECHANGED | SWP_NOCOPYBITS);
        }

        // Grows the window by 1px and back. Each change delivers WM_SIZE with a different
        // size, forcing WPF's HwndTarget to rebuild its render target. Skipped while the
        // HWND is still iconic (a restore may reach us before Win32 finishes the transition).
        private void NudgeRenderTarget(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT rc)) return;
            if (rc.top <= -30000 || rc.left <= -30000) return;   // still iconic
            int width = rc.right - rc.left;
            int height = rc.bottom - rc.top;
            if (width <= 0 || height <= 0) return;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height + 1,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS);
        }
    }
}
