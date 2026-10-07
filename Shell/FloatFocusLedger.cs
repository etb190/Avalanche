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
    //
    // v1.19.45: the fifth report said the same thing again, so the work
    // moved to the net's own seams. The undo had been cut synchronously
    // inside the minimize's own StateChanged - a ShowWindow nested in the
    // ShowWindow still draining - and a swallowed restore reads exactly
    // like no restore. The undo rides the dispatcher now and checks its
    // work. The echo window widened past a slow click (600 -> 900ms), the
    // veto learned the echo too (a swallowed command never flickers), and
    // the shield's cooldown stopped being a door for a shell that cuts
    // the same click twice - a burst inside 150ms of the last undo
    // bounces with it; a hand cannot. And everything, finally, speaks:
    // the MinimizeRecorder (Shell/MinimizeRecorder.cs) writes every
    // stamp, every command and every verdict to the trace log, so if the
    // wound outlives this fix, the next one starts from evidence.
    internal static class FloatFocusLedger
    {
        private static long _floatActivatedTick;
        private static long _mainActivatedTick;

        internal static void NoteFloatActivated()
        {
            _floatActivatedTick = Environment.TickCount64;
            MinimizeRecorder.Log("ledger.floatActivated", "");
        }

        internal static void NoteMainActivated()
        {
            _mainActivatedTick = Environment.TickCount64;
            MinimizeRecorder.Log("ledger.mainActivated", "");
        }

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
        private const long FloatEchoMs = 900;
        private const long ShieldCooldownMs = 800;

        // v1.19.45: a shell can cut the same click twice - the toggle lands,
        // the shield bounces it, and a twin command arrives a breath later.
        // A burst of minimizes inside BurstMs of the last undo is one click
        // stuttering; a human's next press cannot land inside that window.
        private const long BurstMs = 150;
        private const int BurstLimit = 3;

        private static long _floatDismissedTick = long.MinValue / 2;
        private static long _deliberateMinimizeTick = long.MinValue / 2;
        private static long _shieldCooldownUntilTick = long.MinValue / 2;
        private static long _lastShieldUndoTick = long.MinValue / 2;
        private static int _burstCount;

        // Any float exit stamps this: the X, Escape, the click-away itself,
        // the owner's death - and the deactivation that starts them, because
        // the deactivation IS the click-away's landing for floats that linger
        // past it. The echo window opens with the death, not the activation.
        internal static void NoteFloatDismissed()
        {
            _floatDismissedTick = Environment.TickCount64;
            MinimizeRecorder.Log("ledger.floatDismissed", "");
        }

        // A minimize the reader aimed by hand on this window's own chrome -
        // the caption button, the themed system menu's Minimize. Carried as a
        // stamp the restore net honors no matter how fresh the float echo is.
        internal static void NoteDeliberateMinimize()
        {
            _deliberateMinimizeTick = Environment.TickCount64;
            MinimizeRecorder.Log("ledger.deliberate", "");
        }

        // A shielded event - a vetoed command or an undone minimize - stands
        // the shield down briefly, so the next minimize is the reader's own.
        internal static void NoteShieldedMinimizeUndo()
        {
            long now = Environment.TickCount64;
            _shieldCooldownUntilTick = now + ShieldCooldownMs;
            // v1.19.45: the burst ledger - an undo inside BurstMs of the
            // last one is the same click cut twice by a shell, not a hand;
            // past the burst width the count resets, because no hand is
            // that fast.
            if (now - _lastShieldUndoTick > BurstMs) _burstCount = 0;
            _lastShieldUndoTick = now;
            _burstCount++;
            MinimizeRecorder.Log("ledger.undo", $"burst={_burstCount}");
        }

        internal static bool FloatDismissedRecently
            => Environment.TickCount64 - _floatDismissedTick < FloatEchoMs;

        // v1.19.45: internal now - the veto's echo branch has to know the
        // command was not aimed at this window's own chrome.
        internal static bool DeliberateMinimizeFresh
            => Environment.TickCount64 - _deliberateMinimizeTick < FloatEchoMs;

        // v1.19.45: internal now - the veto's echo branch honors the
        // cooldown too: once a shielded event has stood the shield down,
        // the reader's next minimize is their own, whoever asks.
        internal static bool ShieldSuspended
            => Environment.TickCount64 < _shieldCooldownUntilTick;

        private static bool InBurst
            => Environment.TickCount64 - _lastShieldUndoTick <= BurstMs
               && _burstCount < BurstLimit;

        // The restore net's whole question: a minimize arriving inside a
        // float's echo window - while one held the foreground, or a breath
        // after one died - with no deliberate stamp and no suspended shield,
        // is a click-away heard at the wrong window. Undo it. v1.19.45: the
        // suspension no longer swallows a burst - a second command inside
        // BurstMs of the last undo is the shell cutting the same click
        // twice, and it bounces with the first (up to BurstLimit, past
        // which it is read as a hand).
        internal static bool ShouldRestoreMinimize()
        {
            bool verdict = !DeliberateMinimizeFresh
                           && (FloatInCharge || FloatDismissedRecently)
                           && (!ShieldSuspended || InBurst);
            MinimizeRecorder.Log("ledger.net", $"verdict={verdict} {Snapshot()}");
            return verdict;
        }

        // v1.19.45: one line of ground truth for the trace log - every
        // clock the shield reads plus the true foreground, so a report
        // that outlives the fix replays here.
        internal static string Snapshot()
        {
            long now = Environment.TickCount64;
            return $"inCharge={FloatInCharge} echo={(now - _floatDismissedTick) < FloatEchoMs} " +
                   $"del={DeliberateMinimizeFresh} susp={ShieldSuspended} burst={_burstCount} " +
                   $"fg=0x{GetForegroundWindow().ToInt64():X}";
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
