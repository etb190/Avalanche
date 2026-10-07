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
    //
    // v1.19.44: the fourth report finally named the mechanism. The popup's X
    // button dismissed it cleanly; only the CLICK-AWAY minimized the app.
    // The click-away ACTIVATES the main window first - the popup dies
    // mid-click, its Deactivated -> Close - so every guard reading the world
    // before the command arrives (field, grace window, FloatInCharge) sees
    // the foreground already ours and steps aside. No pre-command veto can
    // catch an ordering that destroys its own evidence. So the ledger keeps
    // the two facts that SURVIVE it: a float died a breath ago
    // (NoteFloatDismissed - its deactivation opens an echo window), and a
    // minimize the reader aimed by hand (NoteDeliberateMinimize - the
    // caption button and the themed system menu; their command is
    // indistinguishable from a shell toggle by the time it lands, only the
    // stamp tells them apart). The WndProc veto keeps its first-line seat;
    // a restore net in OnStateChanged (Shell/WindowChrome.cs) becomes the
    // last line: a minimize landing inside a float's echo window without a
    // deliberate stamp is undone, whatever channel carried it, and a
    // shielded event stands the shield down for a beat so the reader's
    // next minimize is their own.
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

        // The echo window (how long after a float's death a minimize is still
        // read as that click's echo) and the shield cooldown (how long a
        // shielded event stands the shield down). The cooldown spans the echo
        // window, so a shielded first minimize can bounce at most once - the
        // reader's next one is always their own.
        private const long FloatEchoMs = 600;
        private const long ShieldCooldownMs = 800;

        private static long _floatDismissedTick = long.MinValue / 2;
        private static long _deliberateMinimizeTick = long.MinValue / 2;
        private static long _shieldCooldownUntilTick = long.MinValue / 2;

        // Any float exit stamps this: the X, Escape, the click-away itself,
        // the owner's death - and the deactivation that starts them, because
        // the deactivation IS the click-away's landing for floats that linger
        // past it. The echo window opens with the death, not the activation.
        internal static void NoteFloatDismissed() => _floatDismissedTick = Environment.TickCount64;

        // A minimize the reader aimed by hand on this window's own chrome -
        // the caption button, the themed system menu's Minimize. Carried as a
        // stamp the restore net honors no matter how fresh the float echo is.
        internal static void NoteDeliberateMinimize() => _deliberateMinimizeTick = Environment.TickCount64;

        // A shielded event - a vetoed command or an undone minimize - stands
        // the shield down briefly, so the next minimize is the reader's own.
        internal static void NoteShieldedMinimizeUndo()
            => _shieldCooldownUntilTick = Environment.TickCount64 + ShieldCooldownMs;

        internal static bool FloatDismissedRecently
            => Environment.TickCount64 - _floatDismissedTick < FloatEchoMs;

        private static bool DeliberateMinimizeFresh
            => Environment.TickCount64 - _deliberateMinimizeTick < FloatEchoMs;

        private static bool ShieldSuspended
            => Environment.TickCount64 < _shieldCooldownUntilTick;

        // The restore net's whole question: a minimize arriving inside a
        // float's echo window - while one held the foreground, or a breath
        // after one died - with no deliberate stamp and no suspended shield,
        // is a click-away heard at the wrong window. Undo it.
        internal static bool ShouldRestoreMinimize()
            => !DeliberateMinimizeFresh && !ShieldSuspended
               && (FloatInCharge || FloatDismissedRecently);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
