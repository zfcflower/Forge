using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Forge.Ipc;

/// <summary>Correlates requests by id, applies deadlines, dispatches inbound requests and notifications.</summary>
public sealed class IpcPeer : IIpcPeer
{
    private const int MaxConcurrentHandlers = 64;

    private readonly IIpcTransport transport;
    private readonly IJsonFrameCodec codec;
    private readonly TimeSpan defaultTimeout;
    private readonly int maxOutstandingRequests;
    private readonly ConcurrentDictionary<long, PendingRequest> pending = new();
    private readonly ConcurrentDictionary<long, InboundRequest> inbound = new();
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private readonly SemaphoreSlim handlerGate = new(MaxConcurrentHandlers, MaxConcurrentHandlers);
    private readonly Channel<QueuedRequest> requests = Channel.CreateUnbounded<QueuedRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private long nextId;
    private int outstanding;
    private int closed;
    private int disposed;

    public IpcPeer(IIpcTransport transport, IpcPeerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = transport;
        codec = options?.Codec ?? FrameCodec.Default;
        defaultTimeout = options?.DefaultTimeout ?? TimeSpan.FromSeconds(120);
        if (defaultTimeout <= TimeSpan.Zero)
        {
            defaultTimeout = Timeout.InfiniteTimeSpan;
        }

        maxOutstandingRequests = options?.MaxOutstandingRequests ?? 0;

        transport.FrameReceived += OnFrame;
        transport.Closed += OnTransportClosed;
        transport.ProtocolError += OnTransportProtocolError;
        pump = Task.Run(PumpAsync);
    }

    public bool IsClosed => Volatile.Read(ref closed) != 0;

    public event Func<IpcMessage, CancellationToken, Task>? RequestReceived;

    public event Action<IpcMessage>? NotificationReceived;

    public event Action<Exception?>? Closed;

    /// <summary>Raised once for each inbound frame that is dropped because it cannot be decoded, is empty, or was
    /// rejected by the transport; the connection stays usable and later valid frames are still dispatched.</summary>
    public event Action<IpcProtocolException>? ProtocolError;

    /// <summary>Raised once for each inbound frame whose <see cref="IpcMessage.Version"/> does not match
    /// <see cref="IpcProtocol.Version"/>; the frame is rejected and the connection stays usable.</summary>
    public event Action<IpcVersionMismatchException>? VersionMismatch;

    public Task StartAsync(CancellationToken ct) => transport.StartAsync(ct);

    public async Task<IpcMessage> SendRequestAsync(string operation, JsonElement? args, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ThrowIfClosed(operation);

        var reserved = ReserveOutstanding(operation);
        try
        {
            var id = Interlocked.Increment(ref nextId);
            using var deadline = new CancellationTokenSource(defaultTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token, lifetime.Token);
            var token = linked.Token;
            var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, new PendingRequest(operation, completion)))
            {
                throw new IpcProtocolException($"Request id {id} is already in flight.");
            }

            if (IsClosed)
            {
                pending.TryRemove(id, out _);
                throw new IpcConnectionClosedException($"The IPC peer is closed; '{operation}' was not sent.");
            }

            try
            {
                await transport.SendAsync(codec.Serialize(IpcMessage.Request(id, operation, args)), token).ConfigureAwait(false);
                return EnsureNoRemoteError(operation, await completion.Task.WaitAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (!IsClosed)
                {
                    SendCancellation(id, operation);
                }

                throw;
            }
            catch (OperationCanceledException oce)
            {
                if (completion.Task.IsCompleted)
                {
                    return EnsureNoRemoteError(operation, await completion.Task.ConfigureAwait(false));
                }

                if (IsClosed)
                {
                    throw new IpcConnectionClosedException(
                        $"The IPC connection closed before '{operation}' completed.", oce);
                }

                if (deadline.IsCancellationRequested)
                {
                    SendCancellation(id, operation);
                    throw new IpcTimeoutException(operation, defaultTimeout, oce);
                }

                throw;
            }
            finally
            {
                pending.TryRemove(id, out _);
            }
        }
        finally
        {
            ReleaseOutstanding(reserved);
        }
    }

    public async Task<T> SendRequestAsync<T>(string operation, JsonElement? args, CancellationToken ct)
        where T : notnull
    {
        var message = await SendRequestAsync(operation, args, ct).ConfigureAwait(false);
        if (message.Result is not { } result)
        {
            throw new IpcProtocolException($"The response to '{operation}' carries no result.");
        }

        return IpcJsonTypes.DeserializeResult<T>(result, operation);
    }

    public Task SendNotificationAsync(string operation, JsonElement? args, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ThrowIfClosed(operation);
        return transport.SendAsync(codec.Serialize(IpcMessage.Notification(operation, args)), ct).AsTask();
    }

    public Task RespondAsync(long requestId, JsonElement result, CancellationToken ct) =>
        SendOutboundAsync(IpcMessage.ResultMessage(requestId, result), requestId, ct);

    public Task RespondErrorAsync(long requestId, IpcError error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);
        return SendOutboundAsync(IpcMessage.ErrorMessage(requestId, error), requestId, ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        MarkClosed(null);
        try
        {
            await transport.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
        }

        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        transport.FrameReceived -= OnFrame;
        transport.Closed -= OnTransportClosed;
        transport.ProtocolError -= OnTransportProtocolError;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
        }

        handlerGate.Dispose();
        lifetime.Dispose();
    }

    private async Task SendOutboundAsync(IpcMessage message, long requestId, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw new IpcConnectionClosedException("The IPC peer is closed; the reply was not sent.");
        }

        inbound.TryRemove(requestId, out _);
        await transport.SendAsync(codec.Serialize(message), ct).ConfigureAwait(false);
    }

    private void OnFrame(ReadOnlyMemory<byte> frame)
    {
        IpcMessage message;
        try
        {
            message = codec.Deserialize(frame);
        }
        catch (Exception ex) when (ex is IpcProtocolException or JsonException or ArgumentException or FormatException or NotSupportedException)
        {
            RaiseProtocolError(ex as IpcProtocolException
                ?? new IpcProtocolException($"The inbound IPC frame is not a valid message: {ex.Message}", ex));
            return;
        }

        if (message.Version != IpcProtocol.Version)
        {
            RejectVersionMismatch(message);
            return;
        }

        if (message.IsResponse)
        {
            DispatchResponse(message);
            return;
        }

        if (message.IsNotification)
        {
            DispatchNotification(message);
            return;
        }

        if (string.Equals(message.Operation, IpcOperations.Ping, StringComparison.Ordinal))
        {
            _ = AnswerPingAsync(message.Id);
            return;
        }

        var registration = new InboundRequest();
        inbound[message.Id] = registration;
        if (!requests.Writer.TryWrite(new QueuedRequest(message, registration)))
        {
            inbound.TryRemove(message.Id, out _);
            registration.Dispose();
            _ = TryRespondErrorAsync(message.Id, new IpcError("closed", "The peer is closed."));
        }
    }

    private static IpcMessage EnsureNoRemoteError(string operation, IpcMessage response)
    {
        if (response.Error is { } error)
        {
            throw new IpcRemoteException(operation, error.Code, error.Message, error.Data);
        }

        return response;
    }

    private void RejectVersionMismatch(IpcMessage message)
    {
        var mismatch = new IpcVersionMismatchException(message.Version, IpcProtocol.Version);
        if (message.IsResponse)
        {
            if (pending.TryRemove(message.Id, out var entry))
            {
                entry.Completion.TrySetException(mismatch);
            }
        }
        else if (!message.IsNotification)
        {
            _ = TryRespondErrorAsync(message.Id, new IpcError("version_mismatch", mismatch.Message));
        }

        try
        {
            VersionMismatch?.Invoke(mismatch);
        }
        catch (Exception)
        {
        }
    }

    private void DispatchResponse(IpcMessage message)
    {
        if (!pending.TryRemove(message.Id, out var entry))
        {
            return;
        }

        entry.Completion.TrySetResult(message);
    }

    private void DispatchNotification(IpcMessage message)
    {
        if (string.Equals(message.Operation, IpcOperations.Cancel, StringComparison.Ordinal) &&
            message.Arguments is { } arguments &&
            arguments.TryGetProperty(IpcOperations.RequestIdArgument, out var idElement) &&
            idElement.TryGetInt64(out var cancelled))
        {
            if (inbound.TryGetValue(cancelled, out var registration))
            {
                registration.Cancel();
            }
        }

        try
        {
            NotificationReceived?.Invoke(message);
        }
        catch (Exception)
        {
        }
    }

    private async Task PumpAsync()
    {
        var reader = requests.Reader;
        try
        {
            while (await reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queued))
                {
                    try
                    {
                        await handlerGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        queued.Registration.Dispose();
                        throw;
                    }

                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    handlers[queued.Message.Id] = completion.Task;
                    _ = RunHandlerAsync(queued, completion);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ChannelClosedException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task RunHandlerAsync(QueuedRequest queued, TaskCompletionSource completion)
    {
        try
        {
            await HandleRequestAsync(queued).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            handlers.TryRemove(queued.Message.Id, out _);
            completion.TrySetResult();
        }
    }

    private async Task HandleRequestAsync(QueuedRequest queued)
    {
        var message = queued.Message;
        var registration = queued.Registration;
        try
        {
            var handler = RequestReceived;
            if (handler is null)
            {
                await TryRespondErrorAsync(message.Id, new IpcError(
                    "unhandled",
                    $"No handler is registered for '{message.Operation}'.")).ConfigureAwait(false);
                return;
            }

            await handler(message, registration.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await TryRespondErrorAsync(message.Id, new IpcError("handler_error", ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            inbound.TryRemove(message.Id, out _);
            registration.Dispose();
            handlerGate.Release();
        }
    }

    private async Task AnswerPingAsync(long id)
    {
        try
        {
            await transport.SendAsync(codec.Serialize(IpcMessage.ResultMessage(id, IpcJsonTypes.EmptyObject)), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }
    }

    private async Task TryRespondErrorAsync(long id, IpcError error)
    {
        try
        {
            await transport.SendAsync(codec.Serialize(IpcMessage.ErrorMessage(id, error)), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }
    }

    private void SendCancellation(long id, string operation)
    {
        if (IsClosed || string.Equals(operation, IpcOperations.Ping, StringComparison.Ordinal))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await transport.SendAsync(
                    codec.Serialize(IpcMessage.Notification(IpcOperations.Cancel, IpcJsonTypes.FromId(id))),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or IpcConnectionClosedException)
            {
            }
        });
    }

    private void OnTransportClosed(Exception? cause) => MarkClosed(cause);

    private void OnTransportProtocolError(IpcProtocolException error) => RaiseProtocolError(error);

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

    private void MarkClosed(Exception? cause)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        foreach (var id in pending.Keys)
        {
            if (pending.TryRemove(id, out var removed))
            {
                removed.Completion.TrySetException(new IpcConnectionClosedException(
                    $"The IPC connection closed before '{removed.Operation}' completed.", cause));
            }
        }

        foreach (var registration in inbound.Values)
        {
            registration.Cancel();
        }

        requests.Writer.TryComplete();
        while (requests.Reader.TryRead(out var queued))
        {
            inbound.TryRemove(queued.Message.Id, out _);
            queued.Registration.Dispose();
        }

        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            Closed?.Invoke(cause);
        }
        catch (Exception)
        {
        }
    }

    private bool ReserveOutstanding(string operation)
    {
        if (maxOutstandingRequests <= 0)
        {
            return false;
        }

        if (Interlocked.Increment(ref outstanding) <= maxOutstandingRequests)
        {
            return true;
        }

        Interlocked.Decrement(ref outstanding);
        throw new InvalidOperationException(
            $"The IPC peer already has {maxOutstandingRequests} outstanding request(s); '{operation}' was not sent.");
    }

    private void ReleaseOutstanding(bool reserved)
    {
        if (reserved)
        {
            Interlocked.Decrement(ref outstanding);
        }
    }

    private void ThrowIfClosed(string operation)
    {
        if (IsClosed)
        {
            throw new IpcConnectionClosedException($"The IPC peer is closed; '{operation}' was not sent.");
        }
    }

    private sealed class PendingRequest(string operation, TaskCompletionSource<IpcMessage> completion)
    {
        public string Operation { get; } = operation;

        public TaskCompletionSource<IpcMessage> Completion { get; } = completion;
    }

    private readonly record struct QueuedRequest(IpcMessage Message, InboundRequest Registration);

    private sealed class InboundRequest : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private int disposed;

        public InboundRequest() => Token = cancellation.Token;

        public CancellationToken Token { get; }

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                cancellation.Dispose();
            }
        }
    }
}
