// Features/Discord/DiscordRpcClient.cs - the wire: Discord's local IPC.
//
// Discord's desktop client exposes one local named pipe per app slot -
// \\.\pipe\discord-ipc-0 through discord-ipc-9. The protocol is a length
// prefixed frame: [Int32 opcode (LE)][Int32 length (LE)][UTF-8 JSON]. A
// session opens with opcode 0 (handshake: protocol version + client id)
// and every presence update is opcode 1 (SET_ACTIVITY). That is the whole
// conversation - no HTTP, no sockets, no helper process, no NuGet package:
// a NamedPipeClientStream and two integers are the entire dependency.
//
// Everything here is defensive by design: Discord may not be running, the
// pipe may vanish mid-write (the client was closed), a slot may be taken
// by another app's connection attempt. Every failure path disconnects
// quietly and reports false - the reader's PDF never owns an unhandled
// exception because their chat client hiccuped.

namespace Avalanche.Features.Discord
{
    using System;
    using System.IO;
    using System.IO.Pipes;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class DiscordRpcClient : IDisposable
    {
        // The two opcodes Avalanche speaks: the opening handshake and the
        // presence frame (Discord's dispatches ride opcode 1 back; the
        // drain loop reads and discards them).
        public const int OpcodeHandshake = 0;
        public const int OpcodeFrame = 1;

        private NamedPipeClientStream? _pipe;
        private CancellationTokenSource? _drainCts;
        private Task? _drain;

        /// <summary>True while the pipe to Discord is open and usable.</summary>
        public bool IsConnected => _pipe is { IsConnected: true };

        /// <summary>Raised (on a thread-pool thread) when the drain loop
        /// notices the pipe died - the controller schedules a reconnect.</summary>
        public event Action? ConnectionLost;

        /// <summary>Scans the ten well-known pipe slots, connects to the
        /// first that answers and completes the handshake. Returns false
        /// (never throws) when Discord is not running - the caller just
        /// retries later. Roughly 600ms per silent slot, all of it on the
        /// caller's thread pool thread: the UI never waits on this.</summary>
        public async Task<bool> ConnectAsync(string clientId, CancellationToken ct)
        {
            Disconnect();
            byte[] handshake = JsonSerializer.SerializeToUtf8Bytes(
                new HandshakePayload { V = 1, ClientId = clientId });
            for (int slot = 0; slot < 10; slot++)
            {
                ct.ThrowIfCancellationRequested();
                var pipe = new NamedPipeClientStream(
                    ".", "discord-ipc-" + slot.ToStringInvariant(), PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(600);
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await WriteFrameAsync(pipe, OpcodeHandshake, handshake, ct).ConfigureAwait(false);
                    _pipe = pipe;
                    StartDrain(pipe);
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDispose(pipe);
                    throw;      // the caller is shutting down: stop scanning
                }
                catch
                {
                    TryDispose(pipe);   // this slot is silent or busy: the next one
                }
            }

            return false;
        }

        /// <summary>Sends one framed payload (opcode 1 = SET_ACTIVITY).
        /// Throws on a dead pipe so the controller can schedule the
        /// reconnect - the caller decides what a failed update costs.</summary>
        public async Task SendAsync(int opcode, byte[] payload, CancellationToken ct)
        {
            NamedPipeClientStream? pipe = _pipe;
            if (pipe is not { IsConnected: true })
            {
                throw new IOException("Discord IPC pipe is not connected.");
            }

            await WriteFrameAsync(pipe, opcode, payload, ct).ConfigureAwait(false);
        }

        public void Dispose() => Disconnect();

        /// <summary>Tears the pipe down quietly: the drain loop is cancelled
        /// first, so its death notification cannot fire a pointless
        /// reconnect behind a deliberate close.</summary>
        public void Disconnect()
        {
            try { _drainCts?.Cancel(); } catch { /* already gone */ }
            _drainCts = null;
            _drain = null;
            NamedPipeClientStream? pipe = _pipe;
            _pipe = null;
            TryDispose(pipe);
        }

        // The frame writer: an 8-byte header (opcode, then payload length,
        // both little-endian) followed by the UTF-8 JSON body.
        private static async Task WriteFrameAsync(
            NamedPipeClientStream pipe, int opcode, byte[] payload, CancellationToken ct)
        {
            byte[] frame = new byte[8 + payload.Length];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
            payload.CopyTo(frame, 8);
            await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }

        // The drain: Discord answers every frame (the handshake's READY,
        // activity errors, subscribe events). Nothing here needs the
        // answers - but reading them keeps the pipe healthy and turns a
        // dead client into a prompt ConnectionLost instead of a silent
        // half-open pipe that eats every future write.
        private void StartDrain(NamedPipeClientStream pipe)
        {
            _drainCts = new CancellationTokenSource();
            CancellationToken ct = _drainCts.Token;
            _drain = Task.Run(async () =>
            {
                try
                {
                    byte[] header = new byte[8];
                    while (true)
                    {
                        await ReadExactAsync(pipe, header, ct).ConfigureAwait(false);
                        int length = BitConverter.ToInt32(header, 4);
                        if (length > 0)
                        {
                            byte[] body = new byte[length];
                            await ReadExactAsync(pipe, body, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // The pipe is gone (Discord closed, session ended). Tell
                    // the controller - unless the tear-down is deliberate.
                    if (!ct.IsCancellationRequested && ReferenceEquals(_pipe, pipe))
                    {
                        ConnectionLost?.Invoke();
                    }
                }
            }, CancellationToken.None);
        }

        private static async Task ReadExactAsync(
            NamedPipeClientStream pipe, byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await pipe.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new IOException("Discord IPC pipe closed.");
                }

                read += n;
            }
        }

        private static void TryDispose(NamedPipeClientStream? pipe)
        {
            try { pipe?.Dispose(); } catch { /* best-effort */ }
        }

        private sealed class HandshakePayload
        {
            [JsonPropertyName("v")]
            public int V { get; init; }

            [JsonPropertyName("client_id")]
            public string ClientId { get; init; } = string.Empty;
        }
    }

    // Tiny helper kept next to the client: the slot index needs an
    // invariant string no matter the machine's culture.
    internal static class PipeSlotExtensions
    {
        public static string ToStringInvariant(this int value)
            => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
