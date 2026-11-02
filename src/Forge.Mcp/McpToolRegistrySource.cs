using Forge.Abstractions;

namespace Forge.Mcp;

/// <summary>
/// Live <see cref="IToolRegistry"/> view of the MCP tools of one <see cref="McpServerManager"/>: the tool list
/// follows the servers (list_changed, reconnect, disable) instead of being frozen at construction.
/// </summary>
public sealed class McpToolRegistrySource : IToolRegistry, IDisposable
{
    private readonly McpServerManager manager;
    private readonly Lock gate = new();
    private Snapshot? snapshot;
    private bool dirty = true;
    private bool disposed;

    public McpToolRegistrySource(McpServerManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        this.manager = manager;
        manager.ToolsChanged += OnToolsChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<ITool> Tools => Current.Tools;

    public IReadOnlyList<McpToolAdapter> Adapters => Current.Adapters;

    public bool TryGet(string name, out ITool tool)
    {
        if (name is not null && Current.ByName.TryGetValue(name, out var adapter))
        {
            tool = adapter;
            return true;
        }

        tool = null!;
        return false;
    }

    public Task StartAllAsync(CancellationToken ct) => manager.StartAllAsync(ct);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        manager.ToolsChanged -= OnToolsChanged;
    }

    private Snapshot Current
    {
        get
        {
            lock (gate)
            {
                if (!dirty && snapshot is not null)
                {
                    return snapshot;
                }

                var adapters = manager.Tools;
                var byName = new Dictionary<string, McpToolAdapter>(adapters.Count, StringComparer.Ordinal);
                var tools = new ITool[adapters.Count];
                for (var i = 0; i < adapters.Count; i++)
                {
                    var adapter = adapters[i];
                    tools[i] = adapter;
                    byName[adapter.ExposedName] = adapter;
                }

                snapshot = new Snapshot(tools, adapters, byName);
                dirty = false;
                return snapshot;
            }
        }
    }

    private void OnToolsChanged()
    {
        bool changed;
        lock (gate)
        {
            changed = !dirty;
            dirty = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private sealed record Snapshot(
        IReadOnlyList<ITool> Tools,
        IReadOnlyList<McpToolAdapter> Adapters,
        IReadOnlyDictionary<string, McpToolAdapter> ByName);
}
