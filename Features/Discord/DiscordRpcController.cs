// Features/Discord/DiscordRpcController.cs - the presence's brain.
//
// One static controller owns the whole Discord feature: the enabled state
// (persisted "discord.rpc.enabled", default ON), the IPC client, the reading
// session (book title, page, the elapsed timer's start), and the two pacing
// rules that keep the wire polite:
//   * a 400 ms debounce - rapid scrolling fires dozens of page reports a
//     second, and Discord only needs the last one;
//   * a reconnect backoff (2 s doubling to 30 s) - Discord may start after
//     Avalanche, or restart while the reader reads; the presence quietly
//     follows when it is back.
//
// The elapsed timer counts the READING session, not the page: it starts
// when a book is opened and survives every page turn - Discord's clock
// runs smoothly upward while "Page X of Y" moves underneath it. Switching
// to a different book restarts it. All entry points arrive on the UI
// thread (the document lifecycle and the page reports); the wire work
// runs on the thread pool, and nothing here can throw into the reader's
// way: a chat client being closed is never the PDF's error.

namespace Avalanche.Features.Discord
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Avalanche.Services;

    internal static class DiscordRpcController
    {
        // The feature spec's application id. Overridable through the
        // settings store ("discord.rpc.client_id") for anyone running
        // their own Discord application - the assets then belong to that
        // application, which is exactly what the override is for.
        private const string DefaultClientId = "1547881478774595705";
        private const string SmallImageUrl =
            "https://cdn.discordapp.com/app-icons/1547881478774595705/9f913825c797ce44d9a79a0ccaeeec5b.png";

        private const int DebounceMs = 400;
        private const int MaxPresenceChars = 128;
        private const int ReconnectBaseMs = 2000;
        private const int ReconnectCapMs = 30000;

        private static readonly object Gate = new();

        private static DiscordRpcClient? _client;
        private static System.Threading.Timer? _debounce;
        private static System.Threading.Timer? _reconnect;
        private static bool? _enabled;
        private static bool _connecting;
        private static int _backoffMs = ReconnectBaseMs;

        // The reading session the presence describes. A null path means
        // nothing to broadcast (no book open, or it was just closed).
        private static string? _currentPath;
        private static string? _title;
        private static int _page;
        private static int _pages;
        private static long _sessionStart;

        // The cover art for the book on screen: resolved once per book per
        // session in the background (local thumbnail -> uguu.se upload ->
        // cover_urls.json cache), null until - or unless - a URL exists.
        // The presence's large_image uses it; "kindle" covers the null.
        private static string? _coverUrl;
        private static string? _coverResolvedFor;   // the path a resolution finished for (null result memoized too)
        private static string? _coverPendingFor;    // the path a resolution is in flight for

        // Set by MainWindow at startup: maps a temp working copy's path to
        // the reader's real file (or its display name) when the window knows
        // one. The controller only asks for temp-like paths; a null answer
        // means "nothing better known" and the presence falls back to a
        // generic title instead of the temp copy's GUID.
        internal static Func<string, string?>? RealPathResolver;

        /// <summary>The feature's persisted state, read lazily so the
        /// presence also works before the summary navigator (which hosts
        /// the toggle) is ever opened. Default is ON per the spec.</summary>
        internal static bool Enabled => _enabled ??= ReadEnabled();

        internal static void SetEnabled(bool value)
        {
            _enabled = value;
            try
            {
                AppDataPaths.SetSetting("discord.rpc.enabled", value ? "1" : "0");
            }
            catch
            {
                // best-effort: settings live on a file, the feature lives on
            }

            if (value)
            {
                // The toggle says yes: broadcast what is on screen right now -
                // a no-op with no book open, the next open takes care of it.
                // The cover pipeline joins in, so a late cover re-sends the
                // frame with real art instead of waiting for a page turn.
                _backoffMs = ReconnectBaseMs;
                EnsureCoverForCurrentBook();
                BroadcastNow();
            }
            else
            {
                StopTimers();
                _ = ClearPresenceAsync();
            }
        }

        /// <summary>A document became the reading session (opened, switched
        /// to, restored at startup). A different book restarts the elapsed
        /// timer; returning to the same book does not. A repaired book first
        /// lives as a temp working copy (killerpdf_repaired_{guid}.pdf): the
        /// session keys on the reader's real file when it can be resolved,
        /// and a GUID title never reaches the wire.</summary>
        internal static void OnDocumentOpened(string filePath, int page, int pages)
        {
            string path = filePath;
            string? forcedTitle = null;
            if (IsTempLikePath(filePath))
            {
                string? real = TryResolveRealPath(filePath);
                if (real is not null)
                {
                    path = real;    // the book's own file: title and cover key on it
                }
                else
                {
                    // Nothing better is known - "Reading" beats a temp GUID.
                    forcedTitle = "Reading";
                }
            }

            lock (Gate)
            {
                bool newBook = !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase);
                _currentPath = path;
                _title = forcedTitle ?? SanitizeTitle(path);
                _pages = Math.Max(1, pages);
                _page = Math.Clamp(page, 1, _pages);
                if (newBook)
                {
                    _sessionStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }

            EnsureCoverForCurrentBook();
            ScheduleBroadcast();
        }

        /// <summary>The reader moved (scrolled, jumped, flipped): "Page X of
        /// Y" follows, the elapsed timer does not move. Debounced - a burst
        /// of page reports costs exactly one packet.</summary>
        internal static void OnPageChanged(int page, int pages)
        {
            lock (Gate)
            {
                if (_currentPath is null)
                {
                    return;     // no session to update - the open report never arrived
                }

                _pages = Math.Max(1, pages);
                _page = Math.Clamp(page, 1, _pages);
            }

            ScheduleBroadcast();
        }

        /// <summary>The book went away (tab closed, editor closed): the
        /// profile must not keep advertising a reader who has moved on.</summary>
        internal static void OnDocumentClosed()
        {
            lock (Gate)
            {
                _currentPath = null;
                _title = null;
            }

            StopTimers();
            _ = ClearPresenceAsync();
        }

        // ---- repaired-copy defense -------------------------------------------------

        // A repaired (or decrypted, rasterized, downloaded) book is edited on a
        // temp working copy under App.TempDir, named killerpdf_<tag>_<guid>.pdf.
        // Such a path must never become a Discord title: the reader's real file
        // is what the presence should wear.
        private static bool IsTempLikePath(string filePath)
        {
            try
            {
                string name = Path.GetFileName(filePath);
                if (name.Contains("killerpdf_", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("_repaired_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string tempDir = App.TempDir;
                if (!string.IsNullOrEmpty(tempDir))
                {
                    string full = Path.GetFullPath(filePath);
                    string tempFull = Path.GetFullPath(tempDir);
                    if (!tempFull.EndsWith(Path.DirectorySeparatorChar)
                        && !tempFull.EndsWith(Path.AltDirectorySeparatorChar))
                    {
                        tempFull += Path.DirectorySeparatorChar;
                    }

                    if (full.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // an unreadable path is nobody's crash; treat it as ordinary
            }

            return false;
        }

        // Asks the window for the real book behind a temp copy. The answer is
        // re-checked: a resolver that answers with another temp-like path is
        // treated as "does not know".
        private static string? TryResolveRealPath(string tempPath)
        {
            try
            {
                Func<string, string?>? resolver = RealPathResolver;
                if (resolver is null)
                {
                    return null;
                }

                string? real = resolver(tempPath);
                if (string.IsNullOrWhiteSpace(real) || IsTempLikePath(real))
                {
                    return null;
                }

                return real;
            }
            catch
            {
                return null;
            }
        }

        // The cover art: kicked once per book per session, entirely in the
        // background. While it runs (and forever when it finds nothing) the
        // presence shows the "kindle" asset; a fresh URL re-sends the frame
        // immediately so the real cover shows up without waiting for the
        // reader's next page turn. All state rides under Gate; the network
        // lives in the service.
        private static void EnsureCoverForCurrentBook()
        {
            string? path;
            lock (Gate)
            {
                path = _currentPath;
            }

            if (path is null)
            {
                return;
            }

            lock (Gate)
            {
                if (_coverPendingFor == path || _coverResolvedFor == path)
                {
                    return;     // already in flight, or already answered (even "none")
                }

                _coverPendingFor = path;
            }

            string cleanTitle = DiscordCoverService.CleanTitle(path);
            _ = Task.Run(async () =>
            {
                string? url = null;
                try
                {
                    url = await DiscordCoverService.ResolveAsync(cleanTitle).ConfigureAwait(false);
                }
                catch
                {
                    url = null;     // the service is defensive; this belt keeps the suspenders
                }

                bool send;
                lock (Gate)
                {
                    if (_coverPendingFor == path)
                    {
                        _coverPendingFor = null;
                        _coverResolvedFor = path;
                        _coverUrl = string.IsNullOrWhiteSpace(url) ? null : url;
                    }

                    // Re-send only when art actually arrived and this book is
                    // still the one on screen - no point re-packeting "kindle".
                    send = _coverUrl is not null && _currentPath == path;
                }

                if (send)
                {
                    BroadcastNow();
                }
            });
        }

        /// <summary>The application is exiting: one last, bounded wait on
        /// the clear so the presence does not outlive the reader, then the
        /// client goes away with the process.</summary>
        internal static void Shutdown()
        {
            lock (Gate)
            {
                _currentPath = null;
                _title = null;
            }

            StopTimers();
            try
            {
                ClearPresenceAsync().Wait(TimeSpan.FromMilliseconds(800));
            }
            catch
            {
                // exiting: nothing left to do about a stalled pipe
            }

            try { _client?.Dispose(); } catch { /* best-effort */ }
            _client = null;
        }

        // ---- the wire -------------------------------------------------------------

        // The debounced broadcast: every schedule resets the 400 ms window,
        // and only the last report in a burst reaches the pipe.
        private static void ScheduleBroadcast()
        {
            if (_debounce is null)
            {
                _debounce = new System.Threading.Timer(
                    BroadcastDebounced, null, DebounceMs, Timeout.Infinite);
            }
            else
            {
                try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch { /* dying */ }
            }
        }

        private static void BroadcastDebounced(object? state) => BroadcastNow();

        // Immediate broadcast (the toggle-on flip, and the debounce's edge).
        private static void BroadcastNow()
        {
            if (!Enabled)
            {
                return;
            }

            (string Path, string Title, int Page, int Pages, long Start, string? Cover)? snapshot = null;
            lock (Gate)
            {
                if (_currentPath is not null && _title is not null)
                {
                    snapshot = (_currentPath, _title, _page, _pages, _sessionStart, _coverUrl);
                }
            }

            if (snapshot is null)
            {
                return;     // nothing on screen: Discord keeps its last state until cleared
            }

            _ = RunBroadcastAsync(snapshot.Value);
        }

        private static async Task RunBroadcastAsync(
            (string Path, string Title, int Page, int Pages, long Start, string? Cover) snapshot)
        {
            try
            {
                DiscordRpcClient? client = _client;
                if (client is null || !client.IsConnected)
                {
                    client = await EnsureConnectedAsync().ConfigureAwait(false);
                    if (client is null)
                    {
                        ScheduleReconnect();
                        return;
                    }
                }

                byte[] frame = BuildActivityFrame(snapshot.Title, snapshot.Page, snapshot.Pages, snapshot.Start, snapshot.Cover);
                await client.SendAsync(DiscordRpcClient.OpcodeFrame, frame, CancellationToken.None)
                    .ConfigureAwait(false);
                _backoffMs = ReconnectBaseMs;   // a good trip resets the backoff
            }
            catch
            {
                ScheduleReconnect();
            }
        }

        // Connect-or-bust: one attempt per call, guarded so overlapping
        // broadcasts cannot stampede the pipe scan. Returns null when
        // Discord is not answering - the caller backs off and tries later.
        private static async Task<DiscordRpcClient?> EnsureConnectedAsync()
        {
            if (_client is { IsConnected: true } live)
            {
                return live;
            }

            if (_connecting)
            {
                return null;
            }

            _connecting = true;
            try
            {
                var client = new DiscordRpcClient();
                client.ConnectionLost += OnConnectionLost;
                if (await client.ConnectAsync(ReadClientId(), CancellationToken.None).ConfigureAwait(false))
                {
                    _client = client;
                    return client;
                }

                client.ConnectionLost -= OnConnectionLost;
                client.Dispose();
                return null;
            }
            finally
            {
                _connecting = false;
            }
        }

        private static void OnConnectionLost()
        {
            DiscordRpcClient? dead = _client;
            _client = null;
            try { dead?.Dispose(); } catch { /* best-effort */ }
            ScheduleReconnect();
        }

        private static void ScheduleReconnect()
        {
            // When the reconnect fires it retries whatever is on screen; the
            // backoff doubles per failure and caps at half a minute.
            if (_reconnect is null)
            {
                _reconnect = new System.Threading.Timer(
                    ReconnectTick, null, Math.Max(1, _backoffMs), Timeout.Infinite);
            }
            else
            {
                try { _reconnect.Change(Math.Max(1, _backoffMs), Timeout.Infinite); } catch { /* dying */ }
            }

            _backoffMs = Math.Min(_backoffMs * 2, ReconnectCapMs);
        }

        private static void ReconnectTick(object? state) => BroadcastNow();

        // "activity": null - the protocol's clear. Sent when the reader
        // closes the book, the app exits, or the toggle says stop.
        private static async Task ClearPresenceAsync()
        {
            try
            {
                DiscordRpcClient? client = _client;
                if (client is null || !client.IsConnected)
                {
                    return;     // nothing on the wire to clear
                }

                await client.SendAsync(
                    DiscordRpcClient.OpcodeFrame,
                    BuildClearFrame(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The pipe died mid-clear: the reconnect sorts itself out,
                // and a cleared session is the state we want anyway.
            }
        }

        // ---- frames ----------------------------------------------------------------

        private static byte[] BuildActivityFrame(string title, int page, int pages, long start, string? cover)
        {
            var activity = new JsonObject
            {
                ["type"] = 0,
                ["details"] = Truncate(title),
                ["state"] = Truncate(
                    "Page " + page.ToString(CultureInfo.InvariantCulture)
                    + " of " + pages.ToString(CultureInfo.InvariantCulture)),
                ["timestamps"] = new JsonObject
                {
                    ["start"] = start
                },
                ["assets"] = new JsonObject
                {
                    ["large_image"] = !string.IsNullOrEmpty(cover) ? cover : "kindle",
                    ["large_text"] = Truncate(title),
                    ["small_image"] = SmallImageUrl,
                    ["small_text"] = "Avalanche"
                },
                ["instance"] = true
            };

            var frame = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = activity
                },
                ["nonce"] = Guid.NewGuid().ToString()
            };

            return JsonSerializer.SerializeToUtf8Bytes(frame);
        }

        private static byte[] BuildClearFrame()
        {
            var frame = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = null
                },
                ["nonce"] = Guid.NewGuid().ToString()
            };

            return JsonSerializer.SerializeToUtf8Bytes(frame);
        }

        // ---- state -----------------------------------------------------------------

        private static void StopTimers()
        {
            try { _debounce?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* dying */ }
            try { _reconnect?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* dying */ }
        }

        private static bool ReadEnabled()
        {
            try
            {
                return AppDataPaths.GetSetting("discord.rpc.enabled") != "0";
            }
            catch
            {
                return true;
            }
        }

        private static string ReadClientId()
        {
            try
            {
                string? id = AppDataPaths.GetSetting("discord.rpc.client_id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id.Trim();
                }
            }
            catch
            {
                // best-effort
            }

            return DefaultClientId;
        }

        // ---- title sanitization ------------------------------------------------------

        // "mesopotamia_-_a_history.pdf" reads on Discord as "Mesopotamia
        // A History": underscores and floating dash separators collapse into
        // spaces, while a connecting hyphen keeps its glue - "20,000-5000
        // BC", "Cro-Magnon" and "Ice-Age" arrive exactly as named. What
        // stays is the reader's own naming, title-cased (hyphenated tokens
        // case by segment), possessives and contractions intact ("Israel's"),
        // intact acronyms untouched (USA, BC, AD), hard-capped at Discord's
        // 128. A repaired book's temp working copy never becomes a title at
        // all: OnDocumentOpened resolves it to the real file or falls back
        // to "Reading" rather than advertising a GUID.
        internal static string SanitizeTitle(string filePath)
        {
            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(filePath);
            }
            catch
            {
                name = filePath ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return "Reading";
            }

            // Underscores are filename space substitutes: they become
            // spaces. Dashes are judged in place by NormalizeDashes - a run
            // of dashes or a lone dash floating between spaces is a
            // separator and collapses, but a hyphen gluing words or numbers
            // together is part of the reader's own name ("20,000-5000",
            // "Cro-Magnon", "Ice-Age") and must survive untouched.
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '_')
                {
                    chars[i] = ' ';
                }
            }

            string collapsed = string.Join(
                " ",
                NormalizeDashes(new string(chars)).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var sb = new StringBuilder(collapsed.Length);
            foreach (string word in collapsed.Split(' '))
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(CaseWord(word));
            }

            return Truncate(sb.ToString());
        }

        private static string CaseWord(string word)
        {
            if (word.Length == 0)
            {
                return word;
            }

            // A hyphenated token cases by SEGMENT - "ice-age" becomes
            // "Ice-Age", "state-of-the-art" becomes "State-Of-The-Art" -
            // while each segment keeps the word's own rules below: intact
            // acronyms stay as written (USA, BC, AD) and letter-less runs
            // ("20,000-5000") ride along untouched.
            if (!word.Contains('-', StringComparison.Ordinal))
            {
                return CaseSegment(word);
            }

            string[] segments = word.Split('-');
            var sb = new StringBuilder(word.Length);
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('-');
                }

                sb.Append(CaseSegment(segments[i]));
            }

            return sb.ToString();
        }

        private static string CaseSegment(string segment)
        {
            if (segment.Length == 0)
            {
                return segment;
            }

            // An intact acronym (USA, RPG) keeps its shape; everything else
            // is upper-first, lower-rest - which is exactly how a possessive
            // or contraction ("Israel's") keeps its tail.
            bool allUpper = true;
            bool hasLetter = false;
            foreach (char c in segment)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (!char.IsUpper(c))
                    {
                        allUpper = false;
                        break;
                    }
                }
            }

            if (hasLetter && allUpper)
            {
                return segment;
            }

            return char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant();
        }

        // Dash triage for one filename: a dash survives only when it glues
        // real content together on both sides. A RUN of dashes ("--",
        // "---") collapses to a single space, and a lone dash with
        // whitespace on both sides (" - ") is a separator too - but
        // "20,000-5000" and "Cro-Magnon" keep their glue.
        private static string NormalizeDashes(string text)
        {
            if (!text.Contains('-', StringComparison.Ordinal))
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] != '-')
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                int runEnd = i;
                while (runEnd < text.Length && text[runEnd] == '-')
                {
                    runEnd++;
                }

                bool leftIsSpace = i == 0 || char.IsWhiteSpace(text[i - 1]);
                bool rightIsSpace = runEnd >= text.Length || char.IsWhiteSpace(text[runEnd]);
                if (runEnd - i > 1 || (leftIsSpace && rightIsSpace))
                {
                    sb.Append(' ');                 // separator: one space per dash run
                }
                else
                {
                    sb.Append('-', runEnd - i);     // connecting hyphen(s): keep
                }

                i = runEnd;
            }

            return sb.ToString();
        }

        private static string Truncate(string value)
            => value.Length <= MaxPresenceChars ? value : value[..MaxPresenceChars];
    }
}
