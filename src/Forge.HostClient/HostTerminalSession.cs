using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;

namespace Forge.HostClient;

/// <summary>
/// One pseudo-terminal that lives in the Extension Host and is driven from the main program over the terminal.*
/// operations of the main pipe. Output is the raw VT stream in order, input and resize after exit are ignored, and
/// the session state (running → exited, disconnected, or disposed) moves exactly once.
/// </summary>
/// <remarks>
/// The reader half of <see cref="Output"/> acknowledges what it hands out: the host keeps at most 1 MiB of
/// unacknowledged bytes per terminal and stops reading the PTY at that window, so an unconsumed queue is bounded by
/// the window and no byte can be dropped. The queue itself is therefore unbounded on this side and never blocks the
/// IPC event loop, which only decodes a frame and writes it here.
/// </remarks>
internal sealed class HostTerminalSession : ITerminalSession
{
    /// <summary>Decoded bytes read before an ack is sent; a quarter of the host's 1 MiB window.</summary>
    private const int AckWindowBytes = 64 * 1024;

    /// <summary>Largest payload of one <c>terminal.input</c>/<c>terminal.output</c> frame, decoded.</summary>
    private const int MaxFramePayloadBytes = 64 * 1024;

    /// <summary>How long <see cref="DisposeAsync"/> lets <c>terminal.stop</c> take before closing the session
    /// locally. The host answers only after it sent <c>terminal.exit</c>, so a normal stop finishes well inside it.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    private const int StateRunning = 0;
    private const int StateExited = 1;
    private const int StateFaulted = 2;
    private const int StateStoppedLocally = 3;

    private readonly ExtensionHostClient client;
    private readonly IpcPeer peer;
    private readonly Channel<ReadOnlyMemory<byte>> queue;
    private readonly ChannelWriter<ReadOnlyMemory<byte>> writer;
    private readonly TerminalOutputReader output;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Serializes input frames of this session so concurrent writers cannot interleave their bytes. It is
    /// never disposed: a writer may still be releasing it while the session is disposed, and the semaphore holds no
    /// unmanaged handle unless someone asks for one.</summary>
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Lock outputGate = new();
    private readonly Lock ackGate = new();

    /// <summary>Sequence number the next output frame must carry; guarded by <see cref="outputGate"/>.</summary>
    private long nextSeq;

    /// <summary>Decoded bytes handed to the consumer since the last ack; guarded by <see cref="ackGate"/>.</summary>
    private long unackedBytes;

    private int state = StateRunning;
    private int disposeStarted;

    internal HostTerminalSession(ExtensionHostClient client, IpcPeer peer, string terminalId)
    {
        this.client = client;
        this.peer = peer;
        Id = terminalId;
        queue = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        writer = queue.Writer;
        output = new TerminalOutputReader(this, queue.Reader);
    }

    public string Id { get; }

    public ChannelReader<ReadOnlyMemory<byte>> Output => output;

    public Task<int> Exited => exited.Task;

    /// <summary>The process tree has exited and every output byte has been handed over: writes and resizes are now
    /// ignored, and a foreground check reports false.</summary>
    private bool IsRunning
    {
        get
        {
            lock (outputGate)
            {
                return Volatile.Read(ref state) == StateRunning;
            }
        }
    }

    /// <summary>Called on the IPC event loop for one <c>terminal.output</c> frame. Only the sequence check, one queue
    /// write and (for a broken stream) the failure transition happen here; there is no await and no IPC send.</summary>
    internal void OnOutput(long seq, ReadOnlyMemory<byte> data)
    {
        lock (outputGate)
        {
            if (Volatile.Read(ref state) != StateRunning)
            {
                // terminal.exit is the last message of its id, so anything after it is stale and dropped.
                return;
            }

            if (seq != nextSeq)
            {
                var problem =
                    $"Terminal '{Id}' received output frame {seq} where frame {nextSeq} was expected; the terminal was closed.";
                client.NoteTerminalProblem(problem);
                FailUnderLock(new InvalidDataException(problem));
                return;
            }

            nextSeq = seq + 1;
            if (!data.IsEmpty)
            {
                writer.TryWrite(data);
            }
        }
    }

    /// <summary>Called on the IPC event loop for <c>terminal.exit</c>: the output completed and the process tree is
    /// gone, so <see cref="Output"/> drains and completes and <see cref="Exited"/> reports the exit code.</summary>
    internal void OnExit(int exitCode)
    {
        lock (outputGate)
        {
            if (Interlocked.CompareExchange(ref state, StateExited, StateRunning) != StateRunning)
            {
                return;
            }

            writer.TryComplete();
        }

        exited.TrySetResult(exitCode);
    }

    /// <summary>Ends the session because its connection is gone: buffered output stays readable, then
    /// <see cref="Output"/> faults and <see cref="Exited"/> fails with the same error.</summary>
    internal void OnConnectionClosed(IpcConnectionClosedException error)
    {
        lock (outputGate)
        {
            if (Interlocked.CompareExchange(ref state, StateFaulted, StateRunning) != StateRunning)
            {
                return;
            }

            writer.TryComplete(error);
        }

        exited.TrySetException(error);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        if (input.IsEmpty || !IsRunning)
        {
            return;
        }

        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var offset = 0;
            while (offset < input.Length)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsRunning)
                {
                    // The terminal ended while this write was queued: the rest is dropped, as after exit.
                    return;
                }

                var length = Math.Min(MaxFramePayloadBytes, input.Length - offset);
                // The frame is encoded before the send's first await, so the caller's memory is not retained.
                var payload = new TerminalData(Id, 0, input.Slice(offset, length));
                try
                {
                    await peer.NotifyAsync(TerminalOperations.Input, payload, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IpcConnectionClosedException or IOException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    // The connection went away; the close path of the peer ends the session, so the input is dropped.
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The peer's lifetime ended under the send, which is a connection close, not a caller cancellation.
                    return;
                }

                offset += length;
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask ResizeAsync(TerminalSize size, CancellationToken ct)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            await client.SendTerminalRequestAsync(
                    peer, TerminalOperations.Resize, new TerminalResizeRequest(Id, size.Columns, size.Rows), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException
            or ObjectDisposedException or InvalidOperationException)
        {
            // The host no longer knows the terminal (the connection changed); a resize has no addressee, which the
            // contract treats like a resize after exit. The caller's own cancellation still propagates.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The peer's lifetime ended under the send, which is a connection close, not a caller cancellation.
        }
    }

    public async ValueTask<bool> HasForegroundProcessAsync(CancellationToken ct)
    {
        if (!IsRunning)
        {
            return false;
        }

        try
        {
            var response = await client.SendTerminalRequestAsync(
                    peer, TerminalOperations.Foreground, new TerminalIdRequest(Id), ct)
                .ConfigureAwait(false);
            if (!HostAnswerRules.TryRead<TerminalForegroundResponse>(response, out var foreground, out var problem))
            {
                client.NoteTerminalProblem($"terminal.foreground for '{Id}' returned unusable data: {problem}");
                return false;
            }

            return foreground.Busy;
        }
        catch (Exception ex) when (ex is IpcRemoteException or IpcTimeoutException or IpcConnectionClosedException
            or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // "The host no longer knows the terminal" is documented as false, so a failed probe closes nothing and
            // only leaves a diagnostic. The caller's own cancellation still propagates.
            client.NoteTerminalProblem($"terminal.foreground for '{Id}' failed: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The peer's lifetime ended under the request, which is a connection close, not a caller cancellation.
            return false;
        }
    }

    /// <summary>Idempotent. An unfinished terminal is stopped in the host (<c>terminal.stop</c>, at most five
    /// seconds); the host sends <c>terminal.exit</c> before it answers, so the normal path reports the real exit
    /// code. When the answer never comes (timeout, dead connection) the session is closed locally instead of hanging
    /// the caller: <see cref="Output"/> completes normally and <see cref="Exited"/> reports <c>-1</c>, the same value
    /// a caller sees for "ended without a code". Neither <see cref="ObjectDisposedException"/> nor
    /// <see cref="OperationCanceledException"/> would be truthful here: the terminal did end, the code is just unknown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        if (IsRunning)
        {
            using var budget = new CancellationTokenSource(StopBudget);
            try
            {
                await client.SendTerminalRequestAsync(
                        peer, TerminalOperations.Stop, new TerminalIdRequest(Id), budget.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IpcRemoteException or IpcTimeoutException or IpcConnectionClosedException
                or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The host cannot answer (dead connection, or it is stopping anyway); the local completion below is
                // the session's fallback so a dispose never hangs on a host that is gone.
                client.NoteTerminalProblem($"terminal.stop for '{Id}' did not complete ({ex.GetType().Name}): {ex.Message}");
            }
        }

        lock (outputGate)
        {
            if (Volatile.Read(ref state) == StateRunning &&
                Interlocked.CompareExchange(ref state, StateStoppedLocally, StateRunning) == StateRunning)
            {
                writer.TryComplete();
                exited.TrySetResult(-1);
            }
        }

        client.ReleaseTerminal(this);
    }

    /// <summary>Accounts one delivered chunk towards the ack and sends <c>terminal.ack</c> when the window is full or
    /// the queue just drained. The send is fire and forget on purpose: the consumer never waits for the host.</summary>
    private void AccountRead(long bytes, bool drained)
    {
        long acknowledged = 0;
        lock (ackGate)
        {
            unackedBytes += bytes;
            if (drained || unackedBytes >= AckWindowBytes)
            {
                acknowledged = unackedBytes;
                unackedBytes = 0;
            }
        }

        if (acknowledged > 0)
        {
            _ = SendAckAsync(acknowledged);
        }
    }

    private async Task SendAckAsync(long bytes)
    {
        try
        {
            await peer.NotifyAsync(TerminalOperations.Ack, new TerminalAck(Id, bytes), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException
            or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // A lost ack only matters while the terminal lives; the connection that carried it is the one that died.
            client.NoteTerminalProblem($"terminal.ack for '{Id}' was not sent: {ex.Message}");
        }
    }

    /// <summary>Fails the session while <see cref="outputGate"/> is held by the caller.</summary>
    private void FailUnderLock(Exception error)
    {
        if (Interlocked.CompareExchange(ref state, StateFaulted, StateRunning) != StateRunning)
        {
            return;
        }

        writer.TryComplete(error);
        exited.TrySetException(error);
    }

    /// <summary>Whether the queue handed out its last buffered frame; an unknown count is treated as drained so the
    /// ack is sent early rather than late.</summary>
    private bool Drained()
    {
        var reader = queue.Reader;
        return !reader.CanCount || reader.Count == 0;
    }

    /// <summary>The published reader: every delivery is acknowledged, and everything else is the inner reader's.</summary>
    private sealed class TerminalOutputReader(HostTerminalSession owner, ChannelReader<ReadOnlyMemory<byte>> inner)
        : ChannelReader<ReadOnlyMemory<byte>>
    {
        public override Task Completion => inner.Completion;

        public override bool CanCount => inner.CanCount;

        public override bool CanPeek => inner.CanPeek;

        public override int Count => inner.Count;

        public override bool TryRead([MaybeNullWhen(false)] out ReadOnlyMemory<byte> item)
        {
            if (!inner.TryRead(out item))
            {
                return false;
            }

            owner.AccountRead(item.Length, owner.Drained());
            return true;
        }

        public override bool TryPeek([MaybeNullWhen(false)] out ReadOnlyMemory<byte> item) => inner.TryPeek(out item);

        public override async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            var item = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            owner.AccountRead(item.Length, owner.Drained());
            return item;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);
    }
}
