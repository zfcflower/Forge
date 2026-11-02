namespace Forge.Ipc;

/// <summary>Correlates outgoing requests with responses, enforces timeouts, and dispatches
/// server-initiated requests (the peer with a request id). Notification sends are fire-and-forget.</summary>
public sealed record IpcPeerOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public IJsonFrameCodec? Codec { get; init; }
}

public interface IJsonFrameCodec
{
    ReadOnlyMemory<byte> Serialize(IpcMessage message);
    IpcMessage Deserialize(ReadOnlyMemory<byte> frame);
}

public interface IIpcPeer : IAsyncDisposable
{
    bool IsClosed { get; }

    /// <summary>Raised for each server-initiated request (a message with an id and no result/error);
    /// the handler must reply via <see cref="RespondAsync"/>.</summary>
    event Func<IpcMessage, CancellationToken, Task>? RequestReceived;

    /// <summary>Raised for notifications (id == 0).</summary>
    event Action<IpcMessage>? NotificationReceived;

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
