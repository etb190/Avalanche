using System;
using System.Runtime.InteropServices;

namespace Avalanche
{
    // v1.19.43: who holds the app's foreground - the main window, or one of
    // its owned floats (the extension bubble, its options page, the digest
    // window, the recap companion)? Every float reports the moment it takes
    // the foreground; the main window reports its own. The answer drives the
    // main window's minimize veto: a taskbar toggle cut while a float was the
    // last window the reader actually activated is that float's click-away
    // heard at the app's own button, not an order to send the whole app to
    // the taskbar. v1.19.36 aimed at this with a popup field and v1.19.42
    // with a grace window - both read a state the click-away had already
    // emptied or outrun, and the report came back a third time wearing a new
    // coat: the digest window's float did it too. The ledger reads the only
    // fact no ordering can unhappen: WHICH of our windows activated last.
    // A float in charge means the shell's minimize is a dismissal; the main
    // window taking the foreground again (the veto's own Activate, a click
    // into the book) hands the app back to itself, and the next taskbar
    // click minimizes the old way.
    internal static class FloatFocusLedger
    {
        private static long _floatActivatedTick;
        private static long _mainActivatedTick;

        internal static void NoteFloatActivated() => _floatActivatedTick = Environment.TickCount64;

        internal static void NoteMainActivated() => _mainActivatedTick = Environment.TickCount64;

        // A float activated more recently than the main window - it is (or
        // was, until something else of ours took the foreground) the app's
        // active window. The fact survives the float's own death: the
        // click-away that closes the bubble activates nothing of ours, so
        // the answer stays honest until the main window itself comes forward.
        internal static bool FloatInCharge => _floatActivatedTick > _mainActivatedTick;

        // The veto's whole question. The foreground check keeps a deliberate
        // minimize passing through: the system menu's Minimize is reached
        // from THIS window, so THIS window is the foreground when its command
        // arrives - no float was in charge of that click.
        internal static bool ShouldShieldMinimize(IntPtr mainHwnd)
            => FloatInCharge && GetForegroundWindow() != mainHwnd;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
