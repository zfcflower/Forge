namespace Forge.Ipc;

/// <summary>Correlates outgoing requests with responses, enforces timeouts, and dispatches
/// server-initiated requests (the peer with a request id). Notification sends are fire-and-forget.</summary>
public sealed record IpcPeerOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public IJsonFrameCodec? Codec { get; init; }

    /// <summary>Maximum in-flight outgoing requests; 0 = unlimited (default). Exceeding it throws before sending.</summary>
    public int MaxOutstandingRequests { get; init; }
}

public interface IJsonFrameCodec
{
    ReadOnlyMemory<byte> Serialize(IpcMessage message);
    IpcMessage Deserialize(ReadOnlyMemory<byte> frame);
}

public interface IIpcPeer : IAsyncDisposable
{
    bool IsClosed { get; }

    /// <summary>Raised exactly once when the peer closes (after pending requests are faulted); the cause is null for a clean local stop.</summary>
    event Action<Exception?>? Closed;

    /// <summary>Raised for each server-initiated request (a message with an id and no result/error);
    /// the handler must reply via <see cref="RespondAsync"/>.</summary>
    event Func<IpcMessage, CancellationToken, Task>? RequestReceived;

    /// <summary>Raised for notifications (id == 0).</summary>
    event Action<IpcMessage>? NotificationReceived;

    /// <summary>Raised once for each inbound frame the peer drops (undecodable JSON, empty body, or rejected by the
    /// transport); the connection stays usable, so a caller can tell a malformed peer frame from a timeout.</summary>
    event Action<IpcProtocolException>? ProtocolError;

    Task StartAsync(CancellationToken ct);

    /// <summary>Sends a request and awaits its response.</summary>
    Task<IpcMessage> SendRequestAsync(string operation, System.Text.Json.JsonElement? args, CancellationToken ct);

    /// <summary>Sends a request and awaits its typed result.</summary>
    Task<T> SendRequestAsync<T>(string operation, System.Text.Json.JsonElement? args, CancellationToken ct) where T : notnull;

    Task SendNotificationAsync(string operation, System.Text.Json.JsonElement? args, CancellationToken ct);

    /// <summary>Replies to a server-initiated request, preserving its id.</summary>
    Task RespondAsync(long requestId, System.Text.Json.JsonElement result, CancellationToken ct);

    Task RespondErrorAsync(long requestId, IpcError error, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

public sealed record IpcRequestContext(long RequestId, string Operation, System.Text.Json.JsonElement? Arguments);
