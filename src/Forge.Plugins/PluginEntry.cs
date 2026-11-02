using Forge.Abstractions;

namespace Forge.Plugins;

internal sealed class PluginEntry
{
    private volatile PluginStatus status;

    public PluginEntry(DiscoveredPlugin discovered)
    {
        Discovered = discovered;
        status = new PluginStatus(
            discovered.Manifest.Id,
            discovered.Manifest.Name,
            discovered.Manifest.Version,
            discovered.Directory,
            PluginState.Discovered);
    }

    public DiscoveredPlugin Discovered { get; private set; }

    public string Id => Discovered.Manifest.Id;

    public string Directory => Discovered.Directory;

    /// <summary>Serializes load, unload and reload of this one plugin.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public PluginStatus Status => status;

    public PluginLoadContext? Context { get; set; }

    public WeakReference? WeakContext { get; set; }

    public IForgePlugin? Instance { get; set; }

    public AcceptedRegistrations? Accepted { get; set; }

    public List<PluginLoadIssue> Issues { get; private set; } = [];

    public void SetManifest(PluginManifest manifest)
    {
        Discovered = Discovered with { Manifest = manifest };
        SetState(status.State, status.LastError);
    }

    public void SetState(PluginState state, string? error) =>
        status = status with
        {
            State = state,
            LastError = error,
            Name = Discovered.Manifest.Name,
            Version = Discovered.Manifest.Version,
            Directory = Discovered.Directory,
        };

    public void SetIssues(List<PluginLoadIssue> issues) => Issues = issues;

    /// <summary>Drops every reference into the plugin's load context so the collectible context can be freed.</summary>
    public void ClearRuntime()
    {
        Context = null;
        WeakContext = null;
        Instance = null;
        Accepted = null;
    }
}
