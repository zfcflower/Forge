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

/// <summary>
/// What the settings page's test found: how long a connect plus tool listing took, what the server exposes and what went
/// wrong. Always returned — a test button has something to say even when the server refuses to start.
/// </summary>
public sealed record McpServerProbe(
    string Name,
    McpServerStatus Status,
    TimeSpan Elapsed,
    IReadOnlyList<McpToolAdapter> Tools,
    McpServerCapabilities Capabilities,
    string? Error = null,
    string? StderrTail = null)
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

    /// <summary>
    /// A tool whose <c>inputSchema</c> is longer than this (compact JSON characters) is not offered to the model: every request
    /// carries every offered schema, so one pathological server could otherwise fill the context window on its own. The skip is
    /// recorded on the server's <see cref="McpServerState.LastError"/>. Zero or negative disables the check.
    /// </summary>
    public int MaxToolSchemaChars { get; init; } = 64 * 1024;

    /// <summary>A tool description longer than this is cut (with an ellipsis) before it reaches the model; zero or negative keeps it whole.</summary>
    public int MaxToolDescriptionChars { get; init; } = 4 * 1024;

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
