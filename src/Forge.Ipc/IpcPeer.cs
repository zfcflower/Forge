namespace Forge.Ipc;

/// <summary>Correlates outgoing requests with responses, enforces timeouts, and dispatches
/// server-initiated requests (the peer with a request id). Notification sends are fire-and-forget.</summary>
public sealed record IpcPeerOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public FrameCodec? Codec { get; init; }

    /// <summary>Maximum in-flight outgoing requests; 0 = unlimited (default). Exceeding it throws before sending.</summary>
    public int MaxOutstandingRequests { get; init; }
}

/// <summary>
/// One IPC connection. Payloads are typed: the sender serializes its argument or result object straight into the
/// frame and the receiver decodes <see cref="IpcMessage.Payload"/> with <see cref="IpcMessage.Read{T}"/> into the type
/// the operation documents. Every payload type is a source-generated MessagePack contract (AOT safe).
/// </summary>
public interface IIpcPeer : IAsyncDisposable
{
    bool IsClosed { get; }

    /// <summary>Raised exactly once when the peer closes (after pending requests are faulted); the cause is null for a clean local stop.</summary>
    event Action<Exception?>? Closed;

    /// <summary>Raised for each server-initiated request (a message with an id that is not a response);
    /// the handler must reply via <see cref="RespondAsync{TResult}"/> or <see cref="RespondErrorAsync"/>.</summary>
    event Func<IpcMessage, CancellationToken, Task>? RequestReceived;

    /// <summary>Raised for notifications (id == 0).</summary>
    event Action<IpcMessage>? NotificationReceived;

    /// <summary>Raised once for each inbound frame the peer drops (undecodable header, empty body, or rejected by the
    /// transport); the connection stays usable, so a caller can tell a malformed peer frame from a timeout.</summary>
    event Action<IpcProtocolException>? ProtocolError;

    Task StartAsync(CancellationToken ct);

    /// <summary>Sends a request and returns the successful response undecoded; a remote error throws <see cref="IpcRemoteException"/>.</summary>
    Task<IpcMessage> RequestAsync<TArgs>(string operation, TArgs args, CancellationToken ct);

    /// <summary>Sends a request and decodes its result; an undecodable result throws <see cref="IpcProtocolException"/>.</summary>
    Task<TResult> RequestAsync<TArgs, TResult>(string operation, TArgs args, CancellationToken ct);

    Task NotifyAsync<TArgs>(string operation, TArgs args, CancellationToken ct);

    /// <summary>Replies to a server-initiated request, preserving its id.</summary>
    Task RespondAsync<TResult>(long requestId, TResult result, CancellationToken ct);

    Task RespondErrorAsync(long requestId, IpcError error, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}
