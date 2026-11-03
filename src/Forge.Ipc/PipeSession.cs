using System.Buffers.Binary;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Forge.Ipc;

/// <summary>Framing, ordered delivery, and close bookkeeping over one connected pipe stream.</summary>
internal sealed class PipeSession : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly IpcPipeOptions options;
    private readonly PipeReader reader;
    private readonly PipeWriter writer;
    private readonly Channel<byte[]> frames;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Lock budgetGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly int frameBudget;
    private readonly int maxFrameBytes;
    private readonly long maxDiscardBytes;
    private Task readLoop = Task.CompletedTask;
    private Task dispatchLoop = Task.CompletedTask;
    private TaskCompletionSource? budgetWaiter;
    private long queuedBytes;
    private int closedRaised;
    private int cleanStop;
    private int broken;
    private int started;
    private int disposed;

    public PipeSession(Stream stream, IpcPipeOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        this.stream = stream;
        this.options = options;
        Name = name ?? string.Empty;
        maxFrameBytes = Math.Max(1024, options.MaxFrameBytes);
        maxDiscardBytes = Math.Max(maxFrameBytes, options.MaxResynchronizeBytes);
        frameBudget = Math.Max(maxFrameBytes, options.FrameQueueBytes);
        frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(1, options.FrameQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true, bufferSize: Math.Max(1024, options.ReadBufferBytes)));
        writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public string Name { get; }

    public bool IsConnected =>
        Volatile.Read(ref closedRaised) == 0 &&
        Volatile.Read(ref broken) == 0 &&
        stream is PipeStream { IsConnected: true };

    public event Action<Exception?>? Closed;

    public event Action<ReadOnlyMemory<byte>>? FrameReceived;

    public event Action<IpcProtocolException>? ProtocolError;

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        dispatchLoop = DispatchLoopAsync();
        readLoop = ReadLoopAsync();
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.IsEmpty)
        {
            throw new ArgumentException("An IPC frame must not be empty.", nameof(frame));
        }

        var payload = frame;
        if (payload.Length > FrameExtractor.HeaderBytes &&
            BinaryPrimitives.ReadInt32LittleEndian(payload.Span) == payload.Length - FrameExtractor.HeaderBytes)
        {
            payload = payload[FrameExtractor.HeaderBytes..];
        }

        if (payload.Length > maxFrameBytes)
        {
            throw new ArgumentException(
                $"Frame of {payload.Length} bytes exceeds the {maxFrameBytes}-byte limit.", nameof(frame));
        }

        ThrowIfClosed();
        try
        {
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw ClosedException();
        }

        try
        {
            ThrowIfClosed();
            ct.ThrowIfCancellationRequested();
            var memory = writer.GetSpan(FrameExtractor.HeaderBytes + payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(memory, payload.Length);
            payload.Span.CopyTo(memory[FrameExtractor.HeaderBytes..]);
            writer.Advance(FrameExtractor.HeaderBytes + payload.Length);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            FailConnection(ex);
            throw ClosedException(ex);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref cleanStop, 1);
        Shutdown();
        await AwaitLoopsAsync(options.StopTimeout, ct).ConfigureAwait(false);
        await CompletePipesAsync().ConfigureAwait(false);
        RaiseClosed(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref cleanStop, 1);
        Shutdown();
        await AwaitLoopsAsync(options.StopTimeout, CancellationToken.None).ConfigureAwait(false);
        await CompletePipesAsync().ConfigureAwait(false);
        RaiseClosed(null);
        sendLock.Dispose();
        lifetime.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var token = lifetime.Token;
        var extractor = new FrameExtractor();
        Exception? cause = null;
        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                var buffer = result.Buffer;
                var fatal = false;
                while (true)
                {
                    var status = extractor.TryExtract(ref buffer, maxFrameBytes, maxDiscardBytes, out var body, out var declared);
                    if (status == FrameStatus.NeedMore)
                    {
                        break;
                    }

                    if (status == FrameStatus.Frame)
                    {
                        await EnqueueAsync(body, token).ConfigureAwait(false);
                        continue;
                    }

                    if (status == FrameStatus.Empty)
                    {
                        RaiseProtocolError(new IpcProtocolException(
                            "Received a frame declaring a zero-length body; skipped."));
                        continue;
                    }

                    if (status == FrameStatus.Oversized)
                    {
                        RaiseProtocolError(new IpcProtocolException(
                            $"Received a frame declaring {declared} bytes, above the {maxFrameBytes}-byte limit; skipped."));
                        continue;
                    }

                    cause = new IpcProtocolException(
                        $"Received a frame declaring {declared} bytes, which cannot be resynchronized.");
                    fatal = true;
                    break;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (fatal)
                {
                    if (cause is not null)
                    {
                        FailConnection(cause);
                    }

                    break;
                }

                if (result.IsCompleted)
                {
                    cause ??= new IpcConnectionClosedException($"The IPC peer closed pipe '{Name}'.");
                    FailConnection(cause);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            cause = ex;
            FailConnection(cause);
        }
        catch (Exception ex)
        {
            cause = new IpcConnectionClosedException($"IPC pipe '{Name}' read loop failed.", ex);
            FailConnection(cause);
        }
        finally
        {
            frames.Writer.TryComplete();
            try
            {
                await dispatchLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
            }

            RaiseClosed(cause);
        }
    }

    private async Task DispatchLoopAsync()
    {
        var channel = frames.Reader;
        try
        {
            while (await channel.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (channel.TryRead(out var frame))
                {
                    try
                    {
                        FrameReceived?.Invoke(frame);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        ReleaseBudget(Weight(frame.Length));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            FailConnection(new IpcConnectionClosedException($"IPC pipe '{Name}' dispatch loop failed.", ex));
        }
    }

    private async ValueTask EnqueueAsync(ReadOnlyMemory<byte> body, CancellationToken token)
    {
        var weight = Weight(body.Length);
        await ReserveBudgetAsync(weight, token).ConfigureAwait(false);
        var queued = false;
        try
        {
            byte[] copy;
            if (MemoryMarshal.TryGetArray(body, out var segment) &&
                segment.Array is { } owned &&
                segment.Offset == 0 &&
                segment.Count == owned.Length)
            {
                copy = owned;
            }
            else
            {
                copy = body.ToArray();
            }

            await frames.Writer.WriteAsync(copy, token).ConfigureAwait(false);
            queued = true;
        }
        finally
        {
            if (!queued)
            {
                ReleaseBudget(weight);
            }
        }
    }

    private int Weight(int length) => Math.Clamp(length, 1, frameBudget);

    private async ValueTask ReserveBudgetAsync(int weight, CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (budgetGate)
            {
                var queued = Volatile.Read(ref queuedBytes);
                if (queued == 0 || queued + weight <= frameBudget)
                {
                    Interlocked.Add(ref queuedBytes, weight);
                    return;
                }

                budgetWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = budgetWaiter.Task;
            }

            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private void ReleaseBudget(int weight)
    {
        Interlocked.Add(ref queuedBytes, -weight);
        TaskCompletionSource? waiter;
        lock (budgetGate)
        {
            waiter = budgetWaiter;
            budgetWaiter = null;
        }

        waiter?.TrySetResult();
    }

    private void Shutdown()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            reader.CancelPendingRead();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }

        frames.Writer.TryComplete();
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void FailConnection(Exception cause)
    {
        Interlocked.Exchange(ref broken, 1);
        Shutdown();
        if (Volatile.Read(ref started) == 0)
        {
            RaiseClosed(cause);
        }
    }

    private async Task CompletePipesAsync()
    {
        try
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        try
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task AwaitLoopsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var bound = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5);
        try
        {
            await readLoop.WaitAsync(bound, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseClosed(Exception? cause)
    {
        if (Interlocked.Exchange(ref closedRaised, 1) != 0)
        {
            return;
        }

        var reason = Volatile.Read(ref cleanStop) != 0 ? null : cause;
        try
        {
            Closed?.Invoke(reason);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseProtocolError(IpcProtocolException error)
    {
        try
        {
            ProtocolError?.Invoke(error);
        }
        catch (Exception)
        {
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closedRaised) != 0 || Volatile.Read(ref broken) != 0)
        {
            throw ClosedException();
        }
    }

    private IpcConnectionClosedException ClosedException(Exception? inner = null) =>
        new($"IPC pipe '{Name}' is closed.", inner);
}
