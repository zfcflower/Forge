namespace Forge.Mcp;

public enum McpServerStatus
{
    Stopped,
    Starting,
    Ready,
    Failed,
}

public sealed record McpServerState(
    string Name,
    McpServerStatus Status,
    bool Enabled,
    string? LastError = null,
    string? StderrTail = null,
    DateTimeOffset? ReadyAt = null,
    int ConsecutiveFailures = 0)
{
    public bool IsReady => Status == McpServerStatus.Ready;
}

public sealed class McpServerManagerOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Zero or negative gives up after the first failure instead of retrying.</summary>
    public int MaxReconnectAttempts { get; init; } = 5;

    public int MaxResultChars { get; init; } = 64 * 1024;

    public IReadOnlyList<McpRoot> Roots { get; init; } = [];

    public string ClientName { get; init; } = "forge";

    public int StderrCapacityChars { get; init; } = 8192;

    /// <summary>Replaces the built-in transport creation, e.g. for tests or a custom socket.</summary>
    public Func<McpServerConfig, IMcpTransport>? TransportFactory { get; init; }

    /// <summary>
    /// The client every HTTP server's transport uses (the host routes it through its network stack: resolver and proxy).
    /// Not owned by the manager. Null keeps the transport's own shared client.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}
