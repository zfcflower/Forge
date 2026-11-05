using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;

namespace Forge.HostClient;

/// <summary>
/// Terminal half of <see cref="ExtensionHostClient"/>: starts pseudo-terminals in the Extension Host, keeps the
/// registry of the terminals of the current connection generation, and turns <c>terminal.output</c>/<c>terminal.exit</c>
/// notifications into the sessions' readers and exit tasks. The wire format lives in <see cref="TerminalProtocol"/>.
/// </summary>
public sealed partial class ExtensionHostClient : ITerminalHost
{
    /// <summary>Upper bound of the decoded bytes buffered for terminals whose <c>terminal.start</c> answer has not been
    /// handled yet. The host's flow-control window is 1 MiB per terminal, so a well-behaved host cannot exceed it while
    /// the answer is in flight; the bound only keeps a misbehaving or unreachable host from growing this buffer.</summary>
    private const long MaxEarlyTerminalBytes = 1024 * 1024;

    /// <summary>Upper bound of buffered terminal ids: the host allows at most 32 terminals per connection.</summary>
    private const int MaxEarlyTerminalTerminals = 32;

    private const int MaxTerminalColumns = 1000;
    private const int MaxTerminalRows = 500;

    private readonly Lock terminalsGate = new();

    /// <summary>Live terminals of the current connection generation, by terminal id.</summary>
    private readonly Dictionary<string, HostTerminalSession> terminals = new(StringComparer.Ordinal);

    /// <summary>Frames of terminals the host already started but whose <c>terminal.start</c> answer is not handled yet,
    /// keyed by terminal id (see <see cref="OnTerminalOutput"/>).</summary>
    private readonly Dictionary<string, EarlyTerminalFrames> earlyTerminalFrames = new(StringComparer.Ordinal);

    /// <summary>Insertion order of <see cref="earlyTerminalFrames"/>, so the oldest entry can be evicted. It holds at
    /// most <see cref="MaxEarlyTerminalTerminals"/> ids and is kept in sync with the dictionary.</summary>
    private readonly List<string> earlyTerminalOrder = [];

    private long earlyTerminalBytes;

    /// <summary>
    /// Starts a pseudo-terminal in the host. The client must be <see cref="HostStatus.Running"/> and the request is
    /// validated locally (fully qualified working directory, 1–1000 columns, 1–500 rows) before anything is sent, so
    /// an unusable request never reaches the IPC. Host-side refusals arrive as
    /// <see cref="IpcRemoteException"/> (<c>invalid_request</c>, <c>terminal_limit</c>, <c>terminal_start_failed</c>,
    /// <c>host_stopping</c>).
    /// </summary>
    public async ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTerminalStart(request);
        IpcPeer active;
        try
        {
            active = RequireRunning();
        }
        catch (IpcConnectionClosedException ex)
        {
            // The contract of ITerminalHost is an InvalidOperationException for "the host client is not running"; a
            // dead or desired connection is reported that way instead of leaking the transport exception.
            throw new InvalidOperationException(
                "The Extension Host client is not running; a terminal can only be started while it is Running.", ex);
        }

        var generation = Volatile.Read(ref activeGeneration);
        var payload = TerminalProtocol.SerializeStart(request);
        var response = await SendAsync(active, TerminalOps.Start, payload, RequestBudget, ct).ConfigureAwait(false);
        if (!TerminalProtocol.TryReadStartResult(response.Result, out var terminalId, out var problem))
        {
            Diagnose("terminal.start returned unusable data: " + problem);
            throw new InvalidDataException(
                $"{TerminalOps.Start} returned a response that cannot be decoded: {problem}");
        }

        if (generation != Volatile.Read(ref activeGeneration) || active.IsClosed)
        {
            // The connection that started the terminal was retired while the answer was in flight; the host kills its
            // terminals with the connection, so there is nothing to hand out and nothing to stop.
            throw new IpcConnectionClosedException(
                $"The Extension Host connection closed while terminal '{terminalId}' was starting.");
        }

        return RegisterTerminal(terminalId, active);
    }

    /// <summary>Removes a session from the registry if it is still registered; a no-op when the exit notification
    /// already did.</summary>
    internal void ReleaseTerminal(HostTerminalSession session)
    {
        lock (terminalsGate)
        {
            if (terminals.TryGetValue(session.Id, out var registered) && ReferenceEquals(registered, session))
            {
                terminals.Remove(session.Id);
            }
        }
    }

    /// <summary>Records a protocol problem of the terminal path in <see cref="Diagnostics"/>; the client's queue is
    /// bounded and this never throws into the IPC event loop.</summary>
    internal void NoteTerminalProblem(string message) => Diagnose(message);

    /// <summary>Sends one terminal request with the client's per-call budget, on the connection the session belongs to.</summary>
    internal Task<IpcMessage> SendTerminalRequestAsync(
        IpcPeer active, string operation, JsonElement args, CancellationToken ct) =>
        SendAsync(active, operation, args, RequestBudget, ct);

    /// <summary>Registers the session of a successful start and replays what the host sent before the answer was
    /// handled. The replay runs under the registry lock, the same lock the notification handler takes, so a live frame
    /// cannot slip in between the buffered ones and break the sequence.</summary>
    private HostTerminalSession RegisterTerminal(string terminalId, IpcPeer active)
    {
        var session = new HostTerminalSession(this, active, terminalId);
        HostTerminalSession? replaced = null;
        var exited = false;
        var exitCode = 0;
        lock (terminalsGate)
        {
            if (terminals.TryGetValue(terminalId, out replaced))
            {
                Diagnose(
                    $"terminal.start returned the id '{terminalId}' of a terminal that is already running; the earlier session was ended.");
            }

            terminals[terminalId] = session;
            if (earlyTerminalFrames.Remove(terminalId, out var buffered))
            {
                earlyTerminalOrder.Remove(terminalId);
                earlyTerminalBytes -= buffered.Bytes;
                foreach (var (seq, data) in buffered.Frames)
                {
                    session.OnOutput(seq, data);
                }

                // terminal.exit is the last message of its id, so the session leaves the registry right away; the exit
                // itself is applied after the lock, where it can only complete the writer the replay already filled.
                exited = buffered.HasExit;
                exitCode = buffered.ExitCode;
                if (exited)
                {
                    terminals.Remove(terminalId);
                }
            }
        }

        if (replaced is not null)
        {
            replaced.OnConnectionClosed(new IpcConnectionClosedException(
                $"The host started a new terminal with the id '{terminalId}'; the earlier session was ended."));
        }

        if (exited)
        {
            session.OnExit(exitCode);
        }

        return session;
    }

    private static void ValidateTerminalStart(TerminalStartRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        if (!IsFullyQualified(request.WorkingDirectory))
        {
            throw new ArgumentException(
                "The working directory of a terminal must be a fully qualified path.", nameof(request));
        }

        if (request.Size.Columns is < 1 or > MaxTerminalColumns)
        {
            throw new ArgumentException(
                $"The column count of a terminal must be between 1 and {MaxTerminalColumns}; it was {request.Size.Columns}.",
                nameof(request));
        }

        if (request.Size.Rows is < 1 or > MaxTerminalRows)
        {
            throw new ArgumentException(
                $"The row count of a terminal must be between 1 and {MaxTerminalRows}; it was {request.Size.Rows}.",
                nameof(request));
        }
    }

    /// <summary>
    /// Handles one <c>terminal.output</c> notification on the IPC event loop: decode, sequence-check and enqueue, no
    /// await and no send. An id that is not registered yet belongs to a terminal whose <c>terminal.start</c> answer is
    /// still in flight (the host writes output before the client's continuation runs), so its frames are buffered here
    /// and replayed by <see cref="RegisterTerminal"/> instead of being dropped.
    /// </summary>
    private void OnTerminalOutput(long generation, JsonElement? payload)
    {
        if (!TerminalProtocol.TryReadOutput(payload, out var terminalId, out var seq, out var data, out var problem))
        {
            Diagnose("terminal.output carried no usable payload: " + problem);
            return;
        }

        if (generation != Volatile.Read(ref activeGeneration))
        {
            // A retired connection: its terminals were already ended, and its frames must not reach a live one.
            return;
        }

        lock (terminalsGate)
        {
            if (terminals.TryGetValue(terminalId, out var session))
            {
                session.OnOutput(seq, data);
                return;
            }

            BufferEarlyFrame(terminalId, seq, data);
        }
    }

    /// <summary>Handles one <c>terminal.exit</c> notification: the terminal is removed from the registry and its
    /// session reports the exit code. An early exit is buffered like early output and replayed at registration.</summary>
    private void OnTerminalExit(long generation, JsonElement? payload)
    {
        if (!TerminalProtocol.TryReadExit(payload, out var terminalId, out var exitCode, out var problem))
        {
            Diagnose("terminal.exit carried no usable payload: " + problem);
            return;
        }

        if (generation != Volatile.Read(ref activeGeneration))
        {
            return;
        }

        HostTerminalSession? session = null;
        lock (terminalsGate)
        {
            if (!terminals.Remove(terminalId, out session))
            {
                var buffered = EnsureEarlyFrames(terminalId);
                buffered.HasExit = true;
                buffered.ExitCode = exitCode;
                return;
            }
        }

        session.OnExit(exitCode);
    }

    /// <summary>Buffers one frame of a terminal whose start answer was not handled yet. Both bounds are reported
    /// through <see cref="Diagnostics"/> and only cost the affected terminal (a gap in its sequence ends that session
    /// with a protocol error) instead of growing without limit. A frame that arrives after its terminal's exit is the
    /// only other way in here: the wire contract makes <c>terminal.exit</c> the last message of an id, so such a frame
    /// is buffered (never replayed) and evicted like any other stale entry.</summary>
    private void BufferEarlyFrame(string terminalId, long seq, byte[] data)
    {
        if (earlyTerminalBytes + data.Length > MaxEarlyTerminalBytes)
        {
            Diagnose(
                $"terminal.output for '{terminalId}' arrived before its terminal.start answer and the early-frame buffer is full; the frame was dropped.");
            return;
        }

        var buffered = EnsureEarlyFrames(terminalId);
        buffered.Frames.Add((seq, data));
        buffered.Bytes += data.Length;
        earlyTerminalBytes += data.Length;
    }

    /// <summary>Returns the buffer of <paramref name="terminalId"/>, evicting the oldest buffered terminal when the
    /// id bound is reached.</summary>
    private EarlyTerminalFrames EnsureEarlyFrames(string terminalId)
    {
        if (earlyTerminalFrames.TryGetValue(terminalId, out var existing))
        {
            return existing;
        }

        while (earlyTerminalFrames.Count >= MaxEarlyTerminalTerminals && earlyTerminalOrder.Count > 0)
        {
            var oldest = earlyTerminalOrder[0];
            earlyTerminalOrder.RemoveAt(0);
            if (earlyTerminalFrames.Remove(oldest, out var dropped))
            {
                earlyTerminalBytes -= dropped.Bytes;
                Diagnose(
                    $"terminal frames for '{oldest}' waited too long for their terminal.start answer and were dropped.");
            }
        }

        var created = new EarlyTerminalFrames();
        earlyTerminalFrames[terminalId] = created;
        earlyTerminalOrder.Add(terminalId);
        return created;
    }

    /// <summary>Ends every terminal of the connection generation being retired (stop, crash, reconnect) and drops
    /// buffered frames: their sessions' <see cref="ITerminalSession.Output"/> faults and
    /// <see cref="ITerminalSession.Exited"/> fails with an <see cref="IpcConnectionClosedException"/>.</summary>
    private void TerminateTerminals(Exception? cause)
    {
        List<HostTerminalSession> sessions;
        lock (terminalsGate)
        {
            if (terminals.Count == 0 && earlyTerminalFrames.Count == 0)
            {
                return;
            }

            sessions = [.. terminals.Values];
            terminals.Clear();
            earlyTerminalFrames.Clear();
            earlyTerminalOrder.Clear();
            earlyTerminalBytes = 0;
        }

        foreach (var session in sessions)
        {
            session.OnConnectionClosed(new IpcConnectionClosedException(
                cause is null
                    ? $"The Extension Host connection closed; terminal '{session.Id}' was ended."
                    : $"The Extension Host connection closed; terminal '{session.Id}' was ended: {cause.Message}",
                cause));
        }
    }

    /// <summary>Frames of one terminal that arrived before its <c>terminal.start</c> answer was handled.</summary>
    private sealed class EarlyTerminalFrames
    {
        public List<(long Seq, byte[] Data)> Frames { get; } = [];

        public long Bytes { get; set; }

        public bool HasExit { get; set; }

        public int ExitCode { get; set; }
    }
}
