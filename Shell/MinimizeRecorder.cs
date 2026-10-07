using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Avalanche
{
    // v1.19.45: five reports on the same wound and the fifth said what the
    // fourth said - so from this build on, the window manager talks. Every
    // minimize command, every activation, every ledger stamp and every
    // verdict lands in %TEMP%\Avalanche\minimize-trace.log, one line per
    // event, cheapest possible write, failures swallowed whole. If the
    // wound outlives this fix, the next diagnosis starts from evidence
    // instead of archaeology.
    internal static class MinimizeRecorder
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventMinimizeStart = 0x0016;
        private const uint EventMinimizeEnd = 0x0017;
        private const uint EventObjectDestroy = 0x8001;

        private static readonly object _gate = new object();
        private static readonly WinEventProcDelegate _proc = OnWinEvent;
        private static bool _hooked;

        internal static void Log(string evt, string detail)
        {
            try
            {
                lock (_gate)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "Avalanche");
                    string path = Path.Combine(dir, "minimize-trace.log");
                    var info = new FileInfo(path);
                    // 512 KB cap: a flight recorder, not an archive - when it
                    // outgrows itself it starts over.
                    if (info.Exists && info.Length > 512 * 1024)
                        File.WriteAllText(path, "[truncated]\r\n");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(path,
                        $"{DateTime.Now:HH:mm:ss.fff}|{Environment.TickCount64}|{evt}|{detail}\r\n");
                }
            }
            catch { /* a flight recorder that cannot fly is no one's emergency */ }
        }

        // One call from the main window's constructor. WINEVENT_OUTOFCONTEXT
        // hooks scoped to our own process id: minimize start/end, top-level
        // window destroys, and the moments our own windows take the system
        // foreground. The UI thread's message loop pumps them.
        internal static void Install()
        {
            if (_hooked) return;
            try
            {
                uint pid = (uint)Environment.ProcessId;
                SetWinEventHook(EventMinimizeStart, EventMinimizeEnd, IntPtr.Zero, _proc, pid, 0, 0);
                SetWinEventHook(EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _proc, pid, 0, 0);
                SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, pid, 0, 0);
                _hooked = true;
                Log("recorder.installed", $"pid={pid}");
            }
            catch { /* hooks are a courtesy; the stamps still land */ }
        }

        private static void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (idObject != 0) return;   // OBJID_WINDOW only
                string evtName = evt switch
                {
                    EventSystemForeground => "winevent.foreground",
                    EventMinimizeStart => "winevent.minimizeStart",
                    EventMinimizeEnd => "winevent.minimizeEnd",
                    EventObjectDestroy => "winevent.destroy",
                    _ => $"winevent.{evt}",
                };
                string cls = "";
                if (hwnd != IntPtr.Zero)
                {
                    var sb = new StringBuilder(256);
                    _ = GetClassName(hwnd, sb, 256);
                    cls = sb.ToString();
                }
                Log(evtName, $"hwnd=0x{hwnd.ToInt64():X} cls={cls}");
            }
            catch { /* the recorder never becomes the emergency */ }
        }

        private delegate void WinEventProcDelegate(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProcDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
