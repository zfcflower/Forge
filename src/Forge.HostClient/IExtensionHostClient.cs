using Forge.Abstractions;

namespace Forge.HostClient;

/// <summary>How the main program reaches the Extension Host.</summary>
public sealed record HostConnection
{
    /// <summary>Named-pipe name; must match what the host listens on.</summary>
    public string PipeName { get; init; } = "forge-extension-host";

    /// <summary>Path to the Extension Host executable. When null, the client connects to an
    /// already-running host and never starts or stops a process.</summary>
    public string? HostExecutablePath { get; init; }

    /// <summary>Runtime used to launch the host (default "dotnet"); ignored when <see cref="HostExecutablePath"/> is null.</summary>
    public string RuntimePath { get; init; } = "dotnet";

    /// <summary>Extra arguments for the host executable.</summary>
    public IReadOnlyList<string> HostArguments { get; init; } = [];

    /// <summary>How long to wait for the host to come up before failing.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum HostStatus { Stopped, Starting, Running, Failed }

/// <summary>Main-program facade over the Extension Host. Plugin tools are exposed as
/// <see cref="ITool"/> proxies; every call is routed over IPC, and plugin-to-host tool calls
/// are permission-checked by the main program's approval machinery.</summary>
public interface IExtensionHostClient : IAsyncDisposable
{
    HostStatus Status { get; }

    /// <summary>Current plugin tools, as host-side ITool proxies. Updated atomically on registration changes.</summary>
    IReadOnlyList<ITool> Tools { get; }

    event Action? ToolsChanged;

    event Action<HostStatus, string?>? StatusChanged;

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    /// <summary>Asks the host to rescan plugin directories and re-register tools.</summary>
    Task RefreshPluginsAsync(CancellationToken ct);

    /// <summary>Approves a plugin version (by source/dependency/sdk hashes) so the host may run it.</summary>
    Task ApprovePluginAsync(string pluginId, string version, string sourceHash, string dependencyHash, string sdkVersion, CancellationToken ct);

    Task RevokePluginAsync(string pluginId, string version, CancellationToken ct);
}
