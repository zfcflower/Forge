using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Forge.Ipc;

namespace Forge.ExtensionHost;

/// <summary>Sends one host → main program notification for a terminal. Implemented by <see cref="TerminalManager"/>, which
/// is the only place that knows about the peer; a session therefore never touches the IPC layer directly.</summary>
internal interface ITerminalNotifier
{
    /// <summary>Writes one notification frame. <c>false</c> means the frame was not handed to the pipe completely, which
    /// the session treats as "this terminal can no longer be served".</summary>
    Task<bool> SendAsync(string operation, JsonElement payload, CancellationToken ct);
}

/// <summary>
/// One running terminal on the host side: it owns the pseudo-terminal, the reader that turns PTY output into
/// <c>terminal.output</c> frames, the serialized input writer, the acknowledgement window and the single shutdown path that
/// drains the output, kills the whole process tree and then sends the id's last message.
/// <para>
/// Flow control, and the invariants of this file:
/// </para>
/// <list type="bullet">
/// <item>Each terminal may have at most <see cref="WindowBytes"/> decoded bytes sent but not acknowledged. At the limit the
/// reader simply stops reading the PTY, so the child's writes block exactly like they would on a real terminal. An
/// acknowledgement continues the reader.</item>
/// <item>No byte is ever dropped while the terminal is open. The only discarded output is what is still unsent when the
/// terminal is closing - the tree is already dead and the reader is only draining so the console can close.</item>
/// <item>One read is one frame: the buffer is 64 KiB and nothing merges or splits it, so the byte order the reader saw is
/// the byte order the sequence numbers describe.</item>
/// <item>Input is queued per terminal and written by one task in order; the notification handler never blocks the IPC
/// dispatch loop on a PTY write.</item>
/// <item>A failed notification or a closed connection ends the terminal instead of being swallowed: the caller cannot ack,
/// so the window would never open again.</item>
/// </list>
/// </summary>
internal sealed class TerminalSession
{
    /// <summary>Sent-but-unacknowledged byte window, decoded. Reaching it stops the host from reading the PTY.</summary>
    internal const int WindowBytes = 1024 * 1024;

    /// <summary>Size of one PTY read, and therefore of one <c>terminal.output</c> frame.</summary>
    internal const int ReadChunkBytes = WorkerProcessOwnership.PtyReadChunkBytes;

    /// <summary>How long one notification frame may take before the terminal treats the connection as unusable.</summary>
    private static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(15);

    /// <summary>How long the reader gets to reach end of file once the tree is dead and the console is closing.</summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(5);

    /// <summary>How many input chunks one terminal may queue before a further chunk is refused instead of growing the
    /// queue without bound. The refusal drops that chunk (a keystroke flood of over 1024 unread chunks), never part of one.</summary>
    private const int InputQueueChunks = 1024;

    private readonly ITerminalNotifier notifier;
    private readonly HostCleanupReport cleanup;
    private readonly WorkerProcessOwnership.PtyProcess process;
    private readonly Action<TerminalSession>? finished;
    private readonly ArrayBufferWriter<byte> frameBuffer = new(ReadChunkBytes + 512);
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(InputQueueChunks)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly TaskCompletionSource readerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task inputWriter;
    private readonly Thread reader;
    /// <summary>Guards the acknowledgement window. A plain object, not <see cref="Lock"/>: the reader waits on it with
    /// <see cref="Monitor.Wait(object)"/>, which a <see cref="Lock"/> instance cannot serve.</summary>
    private readonly object flow = new();
    private readonly Lock gate = new();
    private long unacknowledged;
    private long sequence;
    private volatile bool draining;
    private Task? completion;

    internal TerminalSession(
        string id,
        WorkerProcessOwnership.PtyProcess process,
        ITerminalNotifier notifier,
        HostCleanupReport cleanup,
        Action<TerminalSession>? finished = null)
    {
        Id = id;
        this.process = process;
        this.notifier = notifier;
        this.cleanup = cleanup;
        this.finished = finished;

        // A blocked read cannot be interrupted portably, and the reader must never hold a thread-pool slot while an idle
        // shell waits at its prompt: each terminal gets one dedicated background thread, which ends when the PTY reaches
        // end of file (the shell exiting, or the tree being killed).
        reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "terminal-reader-" + id,
        };
        reader.Start();
        inputWriter = Task.Run(WriteInputAsync);
        _ = Task.Run(WatchAsync);
    }

    internal string Id { get; }

    /// <summary>The shell's process id, reported to the main program for its own diagnostics.</summary>
    internal int ProcessId => process.ProcessId;

    /// <summary>Completes once the terminal is over: output drained, process tree gone and (unless the host is stopping)
    /// the <c>terminal.exit</c> frame written. Never faults.</summary>
    internal Task Completion
    {
        get
        {
            lock (gate)
            {
                return completion ??= Task.Run(RunAsync);
            }
        }
    }

    /// <summary>Queues input for the PTY. Returns <c>false</c> when the queue is full or the terminal is closing, in which
    /// case the bytes are dropped - a flood of keystrokes is not worth blocking the IPC dispatch loop for.</summary>
    internal bool WriteInput(byte[] data) => !draining && input.Writer.TryWrite(data);

    /// <summary>Accounts bytes the main program has consumed, which reopens the flow-control window.</summary>
    internal void Acknowledge(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (flow)
        {
            unacknowledged = Math.Max(0, unacknowledged - bytes);
            Monitor.PulseAll(flow);
        }
    }

    /// <summary>Sizes the terminal. False is not an error the caller reports: the terminal may have exited in the meantime
    /// and <c>terminal.resize</c> answers success either way.</summary>
    internal bool Resize(int columns, int rows) => process.TryResize(columns, rows, out _);

    /// <summary>True while the shell has a live child; false after exit or when the native query cannot answer.</summary>
    internal bool HasForegroundChild() => !draining && process.HasForegroundChild();

    /// <summary>Stops the terminal and waits until it is over, including the <c>terminal.exit</c> notification. Idempotent:
    /// every caller awaits the same single-flight shutdown.</summary>
    internal Task StopAsync() => Completion;

    private async Task WatchAsync()
    {
        // The shell exiting on its own is what starts the shutdown of an abandoned-of-its-own-accord terminal: the reader
        // stays blocked until the console is closed, so end of file cannot be the trigger on Windows.
        try
        {
            await process.ExitCode.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A terminal whose exit code cannot be observed is still over; the shutdown path records what it cannot verify.
        }

        await Completion.ConfigureAwait(false);
    }

    private void ReadLoop()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            while (true)
            {
                int allowed;
                lock (flow)
                {
                    while (unacknowledged >= WindowBytes && !draining)
                    {
                        Monitor.Wait(flow);
                    }

                    allowed = draining
                        ? ReadChunkBytes
                        : (int)Math.Min(ReadChunkBytes, WindowBytes - unacknowledged);
                }

                int read;
                try
                {
                    read = process.OutputStream.Read(buffer, 0, allowed);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
                {
                    // A POSIX master reports end of file as EIO once the last slave is closed, which surfaces as an
                    // IOException; a disposed stream means the shutdown path already released the terminal.
                    read = 0;
                }

                if (read <= 0)
                {
                    break;
                }

                if (draining)
                {
                    continue;
                }

                lock (flow)
                {
                    unacknowledged += read;
                }

                if (!SendOutput(buffer.AsSpan(0, read)))
                {
                    // The frame could not be written, so no acknowledgement for it can ever arrive: keeping the terminal
                    // alive would only fill the window and leak the process tree.
                    _ = Completion;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            readerFinished.TrySetResult();
        }
    }

    private bool SendOutput(ReadOnlySpan<byte> data)
    {
        var frame = TerminalProtocol.OutputPayload(frameBuffer, Id, sequence, data);
        sequence++;
        try
        {
            using var deadline = new CancellationTokenSource(SendWindow);
            return notifier.SendAsync(TerminalProtocol.Output, frame, deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WriteInputAsync()
    {
        try
        {
            while (await input.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (input.Reader.TryRead(out var chunk))
                {
                    try
                    {
                        await process.InputStream.WriteAsync(chunk, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
                    {
                        // The shell or its terminal is gone; further input has nowhere to go.
                        return;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The single shutdown path, in the order the contract requires: stop the flow-control window and keep draining so the
    /// console can close, kill the whole tree, wait until the process is really gone, release the console, wait for the
    /// reader to reach end of file, and only then send the terminal's last message. Every step is bounded and every step it
    /// could not verify is recorded in the cleanup report instead of being reported as success.
    /// </summary>
    private async Task RunAsync()
    {
        try
        {
            int exitCode;
            lock (flow)
            {
                draining = true;
                Monitor.PulseAll(flow);
            }

            input.Writer.TryComplete();

            // Windows requires the pseudo console's output pipe to be drained while the console is closing; the reader keeps
            // running and discarding until it sees end of file. On POSIX the same drain reaches the EIO the kernel reports
            // once the slaves are gone.
            var killProblem = process.KillTree();
            exitCode = await WaitForExitAsync().ConfigureAwait(false);

            // Now - and not before - the console may be released: closing it while a client is still attached is what the
            // ConPTY documentation warns about, and on POSIX there is nothing to release.
            await Task.Run(process.CloseConsole).ConfigureAwait(false);

            try
            {
                await readerFinished.Task.WaitAsync(DrainWindow).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                cleanup.Record(
                    $"the output of terminal '{Id}' (pid {ProcessId}) did not reach end of file within "
                    + $"{DrainWindow.TotalSeconds:0} s after its process tree was killed; the remaining output was dropped");
            }

            using (var deadline = new CancellationTokenSource(SendWindow))
            {
                // The notifier drops the frame once the host is stopping: a closing host sends nothing, and terminal.exit is
                // not an exception to that rule.
                try
                {
                    await notifier
                        .SendAsync(TerminalProtocol.Exit, TerminalProtocol.ExitPayload(Id, exitCode), deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
                {
                    // The main program is gone; the terminal ends with it and nothing else can be told to it.
                }
            }

            if (killProblem is not null && exitCode < 0)
            {
                cleanup.Record(
                    $"the process tree of terminal '{Id}' (pid {ProcessId}) could not be verified as stopped: {killProblem}");
            }
        }
        catch (Exception exception)
        {
            cleanup.Record($"terminal '{Id}' could not be closed cleanly", exception);
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch (Exception exception)
            {
                cleanup.Record($"the pseudo-terminal of terminal '{Id}' could not be released", exception);
            }

            await AwaitInputWriterAsync().ConfigureAwait(false);
            finished?.Invoke(this);
        }
    }

    private async Task<int> WaitForExitAsync()
    {
        try
        {
            return await process.ExitCode.WaitAsync(WorkerProcessOwnership.PtyExitWindow).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still running after the kill: that is exactly the case the cleanup report exists for. Reporting a failure exit
            // code keeps the notification honest instead of claiming an observed 0.
            cleanup.Record(
                $"terminal '{Id}' (pid {ProcessId}) was still running {WorkerProcessOwnership.PtyExitWindow.TotalSeconds:0} s "
                + "after its process tree was killed; the host cannot verify that the tree is gone");
            return -1;
        }
    }

    private async Task AwaitInputWriterAsync()
    {
        try
        {
            await inputWriter.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the writer only blocks on a PTY that no longer accepts input, and it dies with the process.
        }
    }
}
