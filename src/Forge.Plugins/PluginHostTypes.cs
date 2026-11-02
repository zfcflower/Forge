using Forge.Abstractions;

namespace Forge.Plugins;

public enum PluginState
{
    Discovered = 0,
    Loaded,
    Failed,
    Unloaded,
}

public sealed record PluginStatus(string Id, string Name, string Version, string Directory, PluginState State, string? LastError = null)
{
    public bool IsLoaded => State == PluginState.Loaded;
}

public sealed record PluginLoadResult(string PluginId, bool Success, PluginState State, string? Error = null);

public sealed record PluginUnloadResult(string PluginId, bool Success, bool ContextCollected, string? Error = null);

public enum PluginChangeKind
{
    Discovered = 0,
    Loaded,
    Unloaded,
    Reloaded,
    Failed,
}

public sealed record PluginChange(string PluginId, PluginChangeKind Kind, PluginState State, string? Error = null);

public sealed class PluginChangedEventArgs(PluginChange change) : EventArgs
{
    public PluginChange Change { get; } = change;
}

/// <summary>Host configuration. Roots are ordered by ascending precedence (global first, workspace last).</summary>
public sealed record PluginHostOptions
{
    /// <summary>Host base directory; each plugin gets <c>&lt;DataBaseDirectory&gt;/&lt;pluginId&gt;</c> as its data directory.</summary>
    public required string DataBaseDirectory { get; init; }

    /// <summary>Ascending precedence: a plugin in a later root overrides the same id from an earlier root.</summary>
    public required IReadOnlyList<string> PluginRoots { get; init; }

    /// <summary>Built-in tool names a plugin must never shadow.</summary>
    public IReadOnlyList<string>? ReservedToolNames { get; init; }

    public IServiceProvider? Services { get; init; }

    /// <summary>Upper bound on how long <see cref="PluginHost.UnloadAsync"/> waits for the context to be collected.</summary>
    public TimeSpan UnloadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public static PluginHostOptions ForWorkspace(
        string workspacePath,
        string? dataBaseDirectory = null,
        string? userPluginsDirectory = null,
        IReadOnlyList<string>? reservedToolNames = null,
        IServiceProvider? services = null) => new()
        {
            DataBaseDirectory = string.IsNullOrWhiteSpace(dataBaseDirectory) ? PluginPaths.DefaultDataDirectory : dataBaseDirectory,
            PluginRoots = PluginDiscovery.DefaultRoots(workspacePath, userPluginsDirectory),
            ReservedToolNames = reservedToolNames,
            Services = services,
        };
}

public sealed class PluginNotFoundException(string pluginId) : Exception($"Plugin '{pluginId}' was not discovered in any plugin root")
{
    public string PluginId { get; } = pluginId;
}

internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}
