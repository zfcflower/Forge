using System.Buffers.Binary;
using System.Collections.Concurrent;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>An Extension Host stand-in: a named-pipe server that speaks the ExtensionProtocol through <see cref="IpcPeer"/>.
/// Requests from the client are recorded and answered by per-operation handlers (default: <see cref="IpcEmpty"/>).
/// Besides typed payloads it can send raw MessagePack payloads (or none), which is how the tests reach the client's
/// validation of payloads a typed sender cannot produce.</summary>
internal sealed class FakeHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Func<IpcMessage, CancellationToken, Task<HostReply>>> handlers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<IpcMessage> requests = new();
    private readonly ConcurrentQueue<IpcMessage> notifications = new();
    private readonly EventLog<IpcMessage> requestLog = new();
    private readonly EventLog<IpcMessage> notificationLog = new();
    private readonly List<Func<FakeHost, Task>> onAccept = [];
    private readonly EventLog<int> accepts = new();
    private readonly CancellationTokenSource lifetime = new();
    private IpcPeer? peer;
    private RawFrameTransport? transport;

    public FakeHost()
    {
        PipeName = "forge-hc-" + Guid.NewGuid().ToString("N");
    }

    public string PipeName { get; }

    /// <summary>Client requests received so far for <paramref name="operation"/>, in arrival order.</summary>
    public IReadOnlyList<IpcMessage> Requests(string operation) =>
        requests.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();

    /// <summary>Client notifications received so far for <paramref name="operation"/>.</summary>
    public IReadOnlyList<IpcMessage> Notifications(string operation) =>
        notifications.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();

    /// <summary>Returns once the host has received <paramref name="count"/> requests for
    /// <paramref name="operation"/> (client to host); lets a test await input/ack frames without sleeps.</summary>
    public async Task<IReadOnlyList<IpcMessage>> WaitForRequestsAsync(string operation, int count)
    {
        var seen = await requestLog.UntilAsync(
            events => events.Count(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)) >= count)
            .ConfigureAwait(false);
        return seen.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Returns once the host has received <paramref name="count"/> notifications for
    /// <paramref name="operation"/> (client to host); lets a test await input/ack frames without sleeps.</summary>
    public async Task<IReadOnlyList<IpcMessage>> WaitForNotificationsAsync(string operation, int count)
    {
        var seen = await notificationLog.UntilAsync(
            events => events.Count(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)) >= count)
            .ConfigureAwait(false);
        return seen.Where(message => string.Equals(message.Operation, operation, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Replaces the answer to <paramref name="operation"/>. A handler that throws <see cref="FakeHostError"/>
    /// answers with an IPC error envelope; one that never completes simulates a host that does not answer.</summary>
    public void On(string operation, Func<IpcMessage, CancellationToken, Task<HostReply>> handler) =>
        handlers[operation] = handler;

    /// <summary>As <see cref="On(string, Func{IpcMessage, CancellationToken, Task{HostReply}})"/> for a handler that
    /// answers with one typed payload.</summary>
    public void On<T>(string operation, Func<IpcMessage, CancellationToken, Task<T>> handler) =>
        handlers[operation] = async (message, ct) => HostReply.Of(await handler(message, ct).ConfigureAwait(false));

    /// <summary>Queues a notification the host emits the moment it accepts the next connection, before the test
    /// resumes. It lands in the connect-to-Running window, which is what recovery-generation tests need.</summary>
    public void NotifyOnAccept<T>(string operation, T args)
    {
        lock (onAccept)
        {
            onAccept.Add(host => host.NotifyClientAsync(operation, args));
        }
    }

    /// <summary>Completes once the host has accepted <paramref name="count"/> connections and sent every queued
    /// on-accept notification of the latest one; lets a test hold the client until those frames are in flight.</summary>
    public Task WhenAcceptedAsync(int count) => accepts.UntilAsync(seen => seen.Count >= count);

    /// <summary>Creates the pipe and waits for the client. Call before the client starts so it can connect.</summary>
    public async Task<IpcPeer> AcceptAsync()
    {
        var server = await IpcPipe.CreateServer(PipeName, lifetime.Token).ConfigureAwait(false);
        var raw = new RawFrameTransport(server);
        var created = new IpcPeer(raw, new IpcPeerOptions { DefaultTimeout = TestLimit.Span });
        created.RequestReceived += (message, ct) => OnRequestAsync(created, raw, message, ct);
        created.NotificationReceived += message =>
        {
            notifications.Enqueue(message);
            notificationLog.Add(message);
        };
        await created.StartAsync(CancellationToken.None).ConfigureAwait(false);
        transport = raw;
        peer = created;
        List<Func<FakeHost, Task>> pending;
        lock (onAccept)
        {
            pending = [.. onAccept];
        }

        foreach (var send in pending)
        {
            await send(this).ConfigureAwait(false);
        }

        accepts.Add(1);
        return created;
    }

    /// <summary>Sends a request from the host to the client and returns its response.</summary>
    public Task<IpcMessage> CallClientAsync<T>(string operation, T args, CancellationToken ct) =>
        Connected.RequestAsync(operation, args, ct);

    /// <summary>Sends a request whose payload is exactly <paramref name="payload"/> (empty: a request without a
    /// payload) and returns the client's response; an error response throws <see cref="IpcRemoteException"/>.</summary>
    public Task<IpcMessage> CallClientAsync(string operation, RawPayload payload, CancellationToken ct) =>
        ConnectedTransport.RequestAsync(operation, payload.Bytes, ct);

    /// <summary>Sends a notification from the host to the client.</summary>
    public Task NotifyClientAsync<T>(string operation, T args) =>
        Connected.NotifyAsync(operation, args, CancellationToken.None);

    /// <summary>Sends a notification whose payload is exactly <paramref name="payload"/> (empty: no payload).</summary>
    public Task NotifyClientAsync(string operation, RawPayload payload) =>
        ConnectedTransport.NotifyAsync(operation, payload.Bytes);

    /// <summary>Returns once the client has processed every frame the host sent before this call: the client
    /// answers unknown operations with an error, and inbound frames are handled in order.</summary>
    public async Task SyncAsync()
    {
        using var limit = TestLimit.Create();
        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => CallClientAsync("sync", IpcEmpty.Instance, limit.Token));
        Assert.Equal("unsupported", error.Code);
    }

    /// <summary>Sends one notification and returns once the client has processed it (see <see cref="SyncAsync"/>),
    /// which is how a test publishes a registration without sleeping.</summary>
    public async Task NotifyAndSyncAsync<T>(string operation, T args)
    {
        await NotifyClientAsync(operation, args).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>As <see cref="NotifyAndSyncAsync{T}"/> with a raw payload.</summary>
    public async Task NotifyAndSyncAsync(string operation, RawPayload payload)
    {
        await NotifyClientAsync(operation, payload).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the host side of the pipe without a stop request, as a crashed host would.</summary>
    public async Task DropConnectionAsync()
    {
        await Connected.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (peer is not null)
        {
            await peer.DisposeAsync().ConfigureAwait(false);
        }
        else if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        lifetime.Dispose();
    }

    private IpcPeer Connected => peer ?? throw new InvalidOperationException("The fake host has not accepted a client.");

    private RawFrameTransport ConnectedTransport =>
        transport ?? throw new InvalidOperationException("The fake host has not accepted a client.");

    private async Task OnRequestAsync(IpcPeer active, RawFrameTransport raw, IpcMessage message, CancellationToken ct)
    {
        requests.Enqueue(message);
        requestLog.Add(message);
        var handler = handlers.TryGetValue(message.Operation, out var registered) ? registered : DefaultHandler;
        try
        {
            var reply = await handler(message, ct).ConfigureAwait(false);
            await reply.SendAsync(active, raw, message.Id).ConfigureAwait(false);
        }
        catch (FakeHostError error)
        {
            await active.RespondErrorAsync(message.Id, new IpcError(error.Code, error.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>A handler for an operation the host never answers; it ends only when the connection closes.</summary>
    public static async Task<HostReply> NeverAnswer(IpcMessage message, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return HostReply.Empty;
    }

    private static Task<HostReply> DefaultHandler(IpcMessage message, CancellationToken ct) =>
        Task.FromResult(HostReply.Empty);
}

/// <summary>The answer of a <see cref="FakeHost"/> handler: a typed payload, or a raw MessagePack payload (empty: a
/// success response without a payload).</summary>
internal abstract class HostReply
{
    public static HostReply Empty { get; } = Of(IpcEmpty.Instance);

    public static HostReply Of<T>(T value) => new Typed<T>(value);

    public static HostReply Raw(RawPayload payload) => new RawReply(payload.Bytes);

    internal abstract Task SendAsync(IpcPeer peer, RawFrameTransport transport, long id);

    private sealed class Typed<T>(T value) : HostReply
    {
        internal override Task SendAsync(IpcPeer peer, RawFrameTransport transport, long id) =>
            peer.RespondAsync(id, value, CancellationToken.None);
    }

    private sealed class RawReply(ReadOnlyMemory<byte> payload) : HostReply
    {
        internal override Task SendAsync(IpcPeer peer, RawFrameTransport transport, long id) =>
            transport.SendRawAsync(IpcHeader.Result(id), payload);
    }
}

/// <summary>A payload written to the wire verbatim (empty: the frame carries no payload). It selects the raw overloads
/// of <see cref="FakeHost"/>, so a test states explicitly that it sends bytes a typed sender cannot produce.</summary>
internal readonly record struct RawPayload(ReadOnlyMemory<byte> Bytes)
{
    public static RawPayload None => default;
}

/// <summary>Raised by a fake host handler to answer with an IPC error envelope carrying <see cref="Code"/>.</summary>
internal sealed class FakeHostError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Wraps the host's pipe so the fake host can write frames the typed <see cref="IpcPeer"/> API cannot (a raw
/// or missing payload) and read the client's answer to such a request. Raw requests use ids far above the peer's own
/// counter; their responses are taken out of the inbound stream before the peer sees them.</summary>
internal sealed class RawFrameTransport(IIpcTransport inner) : IIpcTransport
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcMessage>> pending = new();
    private long nextId = long.MaxValue / 2;
    private Action<ReadOnlyMemory<byte>>? frameReceived;
    private int subscribed;

    public bool IsConnected => inner.IsConnected;

    public event Action<Exception?>? Closed
    {
        add => inner.Closed += value;
        remove => inner.Closed -= value;
    }

    public event Action<IpcProtocolException>? ProtocolError
    {
        add => inner.ProtocolError += value;
        remove => inner.ProtocolError -= value;
    }

    public event Action<ReadOnlyMemory<byte>>? FrameReceived
    {
        add
        {
            frameReceived += value;
            if (Interlocked.Exchange(ref subscribed, 1) == 0)
            {
                inner.FrameReceived += OnFrame;
                inner.Closed += OnClosed;
            }
        }
        remove => frameReceived -= value;
    }

    public Task StartAsync(CancellationToken ct) => inner.StartAsync(ct);

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => inner.SendAsync(frame, ct);

    public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public async Task<IpcMessage> RequestAsync(string operation, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        var answer = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            await SendRawAsync(IpcHeader.Request(id, operation), payload).ConfigureAwait(false);
            var response = await answer.Task.WaitAsync(ct).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                throw new IpcRemoteException(operation, error.Code, error.Message, error.Data);
            }

            return response;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string operation, ReadOnlyMemory<byte> payload) =>
        SendRawAsync(IpcHeader.Notification(operation), payload);

    /// <summary>Writes <paramref name="header"/> followed by the payload bytes verbatim.</summary>
    public async Task SendRawAsync(IpcHeader header, ReadOnlyMemory<byte> payload)
    {
        byte[] bytes;
        using (var frame = FrameCodec.Default.Encode(header))
        {
            bytes = new byte[frame.WrittenCount + payload.Length];
            frame.WrittenMemory.CopyTo(bytes);
            payload.CopyTo(bytes.AsMemory(frame.WrittenCount));
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length - sizeof(int));
        await inner.SendAsync(bytes, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnFrame(ReadOnlyMemory<byte> frame)
    {
        try
        {
            var message = FrameCodec.Default.Decode(frame);
            if (message.IsResponse && pending.TryRemove(message.Id, out var answer))
            {
                answer.TrySetResult(message);
                return;
            }
        }
        catch (IpcProtocolException)
        {
            // Not an answer to a raw request; the peer reports it through its own protocol-error path.
        }

        frameReceived?.Invoke(frame);
    }

    private void OnClosed(Exception? cause)
    {
        foreach (var entry in pending)
        {
            entry.Value.TrySetException(new IpcConnectionClosedException(inner: cause));
        }
    }
}
