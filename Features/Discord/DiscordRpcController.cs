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
                _backoffMs = ReconnectBaseMs;
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
        /// timer; returning to the same book does not.</summary>
        internal static void OnDocumentOpened(string filePath, int page, int pages)
        {
            lock (Gate)
            {
                bool newBook = !string.Equals(_currentPath, filePath, StringComparison.OrdinalIgnoreCase);
                _currentPath = filePath;
                _title = SanitizeTitle(filePath);
                _pages = Math.Max(1, pages);
                _page = Math.Clamp(page, 1, _pages);
                if (newBook)
                {
                    _sessionStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }

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

            (string Path, string Title, int Page, int Pages, long Start)? snapshot = null;
            lock (Gate)
            {
                if (_currentPath is not null && _title is not null)
                {
                    snapshot = (_currentPath, _title, _page, _pages, _sessionStart);
                }
            }

            if (snapshot is null)
            {
                return;     // nothing on screen: Discord keeps its last state until cleared
            }

            _ = RunBroadcastAsync(snapshot.Value);
        }

        private static async Task RunBroadcastAsync(
            (string Path, string Title, int Page, int Pages, long Start) snapshot)
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

                byte[] frame = BuildActivityFrame(snapshot.Title, snapshot.Page, snapshot.Pages, snapshot.Start);
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

        private static byte[] BuildActivityFrame(string title, int page, int pages, long start)
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
                    ["large_image"] = "kindle",
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

        // "mesopotamia_-_a_history.pdf" reads on Discord as
        // "Mesopotamia - A History"... wait: separators become spaces first,
        // so the underscore and dash forms both collapse into readable words -
        // "Mesopotamia  A History". Trailing single spaces vanish with the
        // whitespace-run collapse; what stays is the reader's own naming,
        // title-cased, possessives and contractions intact ("Israel's"),
        // intact acronyms untouched (USA), hard-capped at Discord's 128.
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

            // Separators become spaces; whitespace runs collapse.
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '_' || chars[i] == '-')
                {
                    chars[i] = ' ';
                }
            }

            string collapsed = string.Join(
                " ",
                new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
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

            // An intact acronym (USA, RPG) keeps its shape; everything else
            // is upper-first, lower-rest - which is exactly how a possessive
            // or contraction ("Israel's") keeps its tail.
            bool allUpper = true;
            bool hasLetter = false;
            foreach (char c in word)
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
                return word;
            }

            return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        }

        private static string Truncate(string value)
            => value.Length <= MaxPresenceChars ? value : value[..MaxPresenceChars];
    }
}
