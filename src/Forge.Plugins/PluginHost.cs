using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.Plugins;

/// <summary>
/// Owns plugin discovery, loading, registration and unloading. Each plugin lives in its own collectible
/// <see cref="PluginLoadContext"/>; nothing a plugin registers reaches the host before its <c>Register</c>
/// returns cleanly, and everything it did register is dropped on unload. Load and unload run one at a time
/// per plugin while <see cref="Tools"/>, <see cref="Commands"/>, <see cref="Workflows"/>, <see cref="Agents"/>,
/// <see cref="PromptContributors"/>, <see cref="Panels"/> and <see cref="Issues"/> are lock-free snapshots.
/// </summary>
/// <remarks>
/// Registrations commit last: a plugin that throws from its constructor or <c>Register</c> leaves nothing behind.
/// Its data directory <c>&lt;DataBaseDirectory&gt;/&lt;pluginId&gt;</c> is created only when the plugin is actually
/// loaded and survives unload, so plugin state persists across reloads.
/// </remarks>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly PluginDiscovery discovery;
    private readonly IServiceProvider services;
    private readonly string dataBaseDirectory;
    private readonly TimeSpan unloadTimeout;
    private readonly HashSet<string> reservedToolNames;
    private readonly Lock writeLock = new();
    private readonly Dictionary<string, PluginEntry> byId = new(StringComparer.Ordinal);
    private readonly List<PluginLoadIssue> discoveryIssues = [];
    private readonly SemaphoreSlim discoveryGate = new(1, 1);

    private PluginEntry[] orderedEntries = [];
    private volatile PluginScanResult scanResult = PluginScanResult.Empty;
    private volatile PluginRegistrations registrations = PluginRegistrations.Empty;
    private volatile PluginStatus[] statuses = [];
    private volatile PluginLoadIssue[] issues = [];
    private volatile bool discovered;
    private int disposed;

    public PluginHost(PluginHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataBaseDirectory))
        {
            throw new ArgumentException("DataBaseDirectory is required", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.PluginRoots, nameof(options));

        dataBaseDirectory = Path.GetFullPath(options.DataBaseDirectory);
        unloadTimeout = options.UnloadTimeout > TimeSpan.Zero ? options.UnloadTimeout : TimeSpan.FromSeconds(2);
        services = options.Services ?? EmptyServiceProvider.Instance;
        discovery = new PluginDiscovery(options.PluginRoots);
        reservedToolNames = new HashSet<string>(StringComparer.Ordinal);
        if (options.ReservedToolNames is { } reserved)
        {
            foreach (var name in reserved)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    reservedToolNames.Add(name);
                }
            }
        }
    }

    public event EventHandler<PluginChangedEventArgs>? Changed;

    public IReadOnlyList<ITool> Tools => registrations.Tools;

    public IReadOnlyList<ICommand> Commands => registrations.Commands;

    public IReadOnlyList<WorkflowDefinition> Workflows => registrations.Workflows;

    public IReadOnlyList<AgentDefinition> Agents => registrations.Agents;

    public IReadOnlyList<IPromptContributor> PromptContributors => registrations.PromptContributors;

    public IReadOnlyList<PanelDefinition> Panels => registrations.Panels;

    public PluginRegistrations Registrations => registrations;

    public IReadOnlyList<PluginStatus> Plugins => statuses;

    public IReadOnlyList<PluginLoadIssue> Issues => issues;

    public string DataBaseDirectory => dataBaseDirectory;

    public IReadOnlyList<string> PluginRoots => discovery.Roots;

    public bool TryGetStatus(string pluginId, [MaybeNullWhen(false)] out PluginStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (byId.TryGetValue(pluginId, out var entry))
        {
            status = entry.Status;
            return true;
        }

        status = null;
        return false;
    }

    /// <summary>Scans the roots once and caches the result; concurrent callers share the scan.</summary>
    public Task<PluginScanResult> DiscoverAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return EnsureDiscoveredAsync(ct);
    }

    /// <summary>Loads and registers one plugin; a plugin that is already loaded is returned unchanged.</summary>
    public async Task<PluginLoadResult> LoadAsync(string pluginId, CancellationToken ct = default)
    {
        var entry = await RequireEntryAsync(pluginId, ct).ConfigureAwait(false);
        PluginLoadResult result;
        PluginChange? change;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (result, change) = await LoadCoreAsync(entry, rereadManifest: false, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }

        Raise(change);
        return result;
    }

    public async Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default)
    {
        await EnsureDiscoveredAsync(ct).ConfigureAwait(false);
        var ids = SnapshotIds();
        var results = new PluginLoadResult[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            results[i] = await LoadAsync(ids[i], ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Unloads one plugin and drops every reference it registered together with its load context. Waits up to
    /// <see cref="PluginHostOptions.UnloadTimeout"/> for the collectible context to be collected and reports the
    /// outcome in <see cref="PluginUnloadResult.ContextCollected"/>; cancellation is honoured before the unload
    /// starts, so a cancelled caller never leaves the plugin half-unloaded.
    /// </summary>
    public async Task<PluginUnloadResult> UnloadAsync(string pluginId, CancellationToken ct = default)
    {
        var entry = await RequireEntryAsync(pluginId, ct).ConfigureAwait(false);
        PluginUnloadResult result;
        PluginChange? change;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (result, change) = await UnloadCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }

        Raise(change);
        return result;
    }

    public async Task<IReadOnlyList<PluginUnloadResult>> UnloadAllAsync(CancellationToken ct = default)
    {
        var ids = SnapshotIds();
        var results = new PluginUnloadResult[ids.Length];
        for (var i = ids.Length - 1; i >= 0; i--)
        {
            results[i] = await UnloadAsync(ids[i], ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Unloads the plugin (waiting for its context to be collected) and loads it again, re-reading its manifest.</summary>
    public async Task<PluginLoadResult> ReloadAsync(string pluginId, CancellationToken ct = default)
    {
        var entry = await RequireEntryAsync(pluginId, ct).ConfigureAwait(false);
        PluginLoadResult result;
        PluginChange? change;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UnloadCoreAsync(entry, ct).ConfigureAwait(false);
            (result, change) = await LoadCoreAsync(entry, rereadManifest: true, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }

        Raise(change is null ? null : change with { Kind = result.Success ? PluginChangeKind.Reloaded : PluginChangeKind.Failed });
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var ids = SnapshotIds();
        for (var i = ids.Length - 1; i >= 0; i--)
        {
            if (!byId.TryGetValue(ids[i], out var entry))
            {
                continue;
            }

            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await UnloadCoreAsync(entry, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
                entry.Gate.Dispose();
            }
        }

        discoveryGate.Dispose();
    }

    private async Task<PluginScanResult> EnsureDiscoveredAsync(CancellationToken ct)
    {
        if (discovered)
        {
            return scanResult;
        }

        await discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (discovered)
            {
                return scanResult;
            }

            var scan = await discovery.ScanAsync(ct).ConfigureAwait(false);
            var changes = new List<PluginChange>(scan.Plugins.Count);
            lock (writeLock)
            {
                var entries = new List<PluginEntry>(scan.Plugins.Count);
                foreach (var plugin in scan.Plugins)
                {
                    if (byId.ContainsKey(plugin.Id))
                    {
                        continue;
                    }

                    var entry = new PluginEntry(plugin);
                    byId.Add(plugin.Id, entry);
                    entries.Add(entry);
                    changes.Add(new PluginChange(plugin.Id, PluginChangeKind.Discovered, PluginState.Discovered));
                }

                orderedEntries = [.. entries];
                scanResult = scan;
                discoveryIssues.Clear();
                discoveryIssues.AddRange(scan.Issues);
                discovered = true;
                Rebuild();
            }

            Raise(changes);
            return scanResult;
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    private async Task<PluginEntry> RequireEntryAsync(string pluginId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ThrowIfDisposed();
        await EnsureDiscoveredAsync(ct).ConfigureAwait(false);
        lock (writeLock)
        {
            if (byId.TryGetValue(pluginId, out var entry))
            {
                return entry;
            }
        }

        throw new PluginNotFoundException(pluginId);
    }

    private async Task<(PluginLoadResult Result, PluginChange? Change)> LoadCoreAsync(PluginEntry entry, bool rereadManifest, CancellationToken ct)
    {
        if (entry.Status.State == PluginState.Loaded && entry.Context is not null)
        {
            return (new PluginLoadResult(entry.Id, true, PluginState.Loaded), null);
        }

        ct.ThrowIfCancellationRequested();
        var manifest = entry.Discovered.Manifest;
        if (rereadManifest)
        {
            var read = await PluginManifestReader.ReadAsync(entry.Directory, ct).ConfigureAwait(false);
            if (!read.Success)
            {
                var message = read.Message ?? read.Error.ToString();
                lock (writeLock)
                {
                    entry.SetIssues([new PluginLoadIssue(entry.Id, PluginIssueKind.ManifestInvalid, message, entry.Directory)]);
                    entry.SetState(PluginState.Failed, message);
                    Rebuild();
                }

                return Fail(entry, message);
            }

            manifest = read.Manifest!;
            lock (writeLock)
            {
                entry.SetManifest(manifest);
            }
        }

        var (success, error) = await Task.Factory
            .StartNew(() => LoadPlugin(entry, manifest), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ConfigureAwait(false);

        return success
            ? (new PluginLoadResult(entry.Id, true, PluginState.Loaded), new PluginChange(entry.Id, PluginChangeKind.Loaded, PluginState.Loaded))
            : Fail(entry, error);
    }

    private static (PluginLoadResult Result, PluginChange Change) Fail(PluginEntry entry, string? error) =>
        (new PluginLoadResult(entry.Id, false, PluginState.Failed, error),
         new PluginChange(entry.Id, PluginChangeKind.Failed, PluginState.Failed, error));

    /// <summary>Runs on a dedicated thread so no threadpool stack keeps the plugin alive after a failed load.</summary>
    private (bool Success, string? Error) LoadPlugin(PluginEntry entry, PluginManifest manifest)
    {
        PluginLoadContext? context = null;
        var kind = PluginIssueKind.EntryAssemblyLoadFailed;
        string? error = null;

        try
        {
            var assemblyPath = PluginManifestReader.ResolveEntryAssembly(entry.Directory, manifest.EntryAssembly)
                ?? throw new InvalidOperationException($"Entry assembly '{manifest.EntryAssembly}' escapes {entry.Directory}");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Entry assembly not found: {assemblyPath}", assemblyPath);
            }

            context = new PluginLoadContext(entry.Directory, assemblyPath, $"forge-plugin:{manifest.Id}");
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            kind = PluginIssueKind.EntryTypeInvalid;
            var type = assembly.GetType(manifest.EntryType, throwOnError: false);
            if (type is null)
            {
                throw new InvalidOperationException($"Entry type '{manifest.EntryType}' not found in {assemblyPath}");
            }

            if (!type.IsVisible || !typeof(IForgePlugin).IsAssignableFrom(type) || type.IsAbstract ||
                type.GetConstructor(BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException($"Entry type '{manifest.EntryType}' must be a public, non-abstract IForgePlugin with a public parameterless constructor");
            }

            var dataDirectory = Path.Combine(dataBaseDirectory, manifest.Id);
            Directory.CreateDirectory(dataDirectory);
            var pluginContext = new PluginContext(entry.Directory, dataDirectory, services);
            var recording = new RecordingPluginRegistry();

            kind = PluginIssueKind.RegisterFailed;
            var plugin = (IForgePlugin)Activator.CreateInstance(type)!;
            plugin.Register(recording, pluginContext);

            lock (writeLock)
            {
                entry.Context = context;
                entry.Instance = plugin;
                entry.WeakContext = new WeakReference(context);
                ApplyRegistrations(entry, recording);
                entry.SetState(PluginState.Loaded, null);
                Rebuild();
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }

        context?.Unload();
        lock (writeLock)
        {
            entry.ClearRuntime();
            entry.SetIssues([new PluginLoadIssue(entry.Id, kind, error!, entry.Directory)]);
            entry.SetState(PluginState.Failed, error);
            Rebuild();
        }

        return (false, error);
    }

    private async Task<(PluginUnloadResult Result, PluginChange? Change)> UnloadCoreAsync(PluginEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wasActive = entry.Context is not null || entry.Status.State != PluginState.Unloaded;
        var weak = ReleaseRuntime(entry);
        var collected = weak is null || await WaitForCollectionAsync(weak).ConfigureAwait(false);

        string? error = null;
        if (!collected)
        {
            error = "Plugin load context is still referenced outside the host; it will be collected once those references drop";
            lock (writeLock)
            {
                if (!entry.Issues.Exists(static issue => issue.Kind == PluginIssueKind.ContextNotCollected))
                {
                    entry.Issues.Add(new PluginLoadIssue(entry.Id, PluginIssueKind.ContextNotCollected, error, entry.Directory));
                    Rebuild();
                }
            }
        }

        return wasActive
            ? (new PluginUnloadResult(entry.Id, true, collected, error), new PluginChange(entry.Id, PluginChangeKind.Unloaded, PluginState.Unloaded, error))
            : (new PluginUnloadResult(entry.Id, true, collected), null);
    }

    /// <summary>Separate frame so the unload path keeps no strong reference to the context while waiting for collection.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? ReleaseRuntime(PluginEntry entry)
    {
        var weak = entry.WeakContext;
        entry.Context?.Unload();
        lock (writeLock)
        {
            entry.ClearRuntime();
            entry.SetState(PluginState.Unloaded, null);
            Rebuild();
        }

        return weak;
    }

    private async Task<bool> WaitForCollectionAsync(WeakReference weak)
    {
        var deadline = Environment.TickCount64 + (long)unloadTimeout.TotalMilliseconds;
        var attempt = 0;
        while (weak.IsAlive)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            if (!weak.IsAlive)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            attempt++;
            await Task.Delay(Math.Min(attempt * 10, 50), CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Commit point: first registration of a name wins, built-in tool names are reserved, everything else is reported.</summary>
    private void ApplyRegistrations(PluginEntry entry, RecordingPluginRegistry recording)
    {
        var accepted = new AcceptedRegistrations();
        var newIssues = new List<PluginLoadIssue>();
        var taken = CollectNames(entry);

        foreach (var tool in recording.Tools)
        {
            string name;
            try
            {
                name = tool.Definition.Name;
            }
            catch (Exception ex)
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, $"Tool definition threw: {Describe(ex)}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Tool has an empty name"));
            }
            else if (reservedToolNames.Contains(name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.ReservedRegistration, $"Tool '{name}' shadows a built-in tool"));
            }
            else if (!taken.Tools.Add(name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Tool '{name}' is already registered"));
            }
            else
            {
                accepted.Tools.Add(new NamedTool(name, tool));
            }
        }

        foreach (var command in recording.Commands)
        {
            string name;
            try
            {
                name = command.Definition.Name;
            }
            catch (Exception ex)
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, $"Command definition threw: {Describe(ex)}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Command has an empty name"));
            }
            else if (!taken.Commands.Add(name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Command '{name}' is already registered"));
            }
            else
            {
                accepted.Commands.Add(new NamedCommand(name, command));
            }
        }

        foreach (var workflow in recording.Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.Name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Workflow has an empty name"));
            }
            else if (!taken.Workflows.Add(workflow.Name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Workflow '{workflow.Name}' is already registered"));
            }
            else
            {
                accepted.Workflows.Add(new NamedWorkflow(workflow.Name, workflow));
            }
        }

        foreach (var agent in recording.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Agent has an empty name"));
            }
            else if (!taken.Agents.Add(agent.Name))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Agent '{agent.Name}' is already registered"));
            }
            else
            {
                accepted.Agents.Add(new NamedAgent(agent.Name, agent));
            }
        }

        foreach (var contributor in recording.PromptContributors)
        {
            if (string.IsNullOrWhiteSpace(contributor.Id))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Prompt contributor has an empty id"));
            }
            else if (!taken.PromptContributors.Add(contributor.Id))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Prompt contributor '{contributor.Id}' is already registered"));
            }
            else
            {
                accepted.PromptContributors.Add(new NamedPromptContributor(contributor.Id, contributor));
            }
        }

        foreach (var panel in recording.Panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Id))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.InvalidRegistration, "Panel has an empty id"));
            }
            else if (!taken.Panels.Add(panel.Id))
            {
                newIssues.Add(Issue(entry, PluginIssueKind.DuplicateRegistration, $"Panel '{panel.Id}' is already registered"));
            }
            else
            {
                accepted.Panels.Add(new NamedPanel(panel.Id, panel));
            }
        }

        entry.Accepted = accepted.IsEmpty ? null : accepted;
        entry.SetIssues(newIssues);
    }

    private NameSets CollectNames(PluginEntry except)
    {
        var sets = new NameSets();
        foreach (var other in orderedEntries)
        {
            if (ReferenceEquals(other, except) || other.Accepted is not { } accepted)
            {
                continue;
            }

            foreach (var item in accepted.Tools)
            {
                sets.Tools.Add(item.Name);
            }

            foreach (var item in accepted.Commands)
            {
                sets.Commands.Add(item.Name);
            }

            foreach (var item in accepted.Workflows)
            {
                sets.Workflows.Add(item.Name);
            }

            foreach (var item in accepted.Agents)
            {
                sets.Agents.Add(item.Name);
            }

            foreach (var item in accepted.PromptContributors)
            {
                sets.PromptContributors.Add(item.Id);
            }

            foreach (var item in accepted.Panels)
            {
                sets.Panels.Add(item.Id);
            }
        }

        return sets;
    }

    private void Rebuild()
    {
        var tools = new List<ITool>();
        var commands = new List<ICommand>();
        var workflows = new List<WorkflowDefinition>();
        var agents = new List<AgentDefinition>();
        var contributors = new List<IPromptContributor>();
        var panels = new List<PanelDefinition>();
        var newStatuses = new PluginStatus[orderedEntries.Length];
        var newIssues = new List<PluginLoadIssue>(discoveryIssues);

        for (var i = 0; i < orderedEntries.Length; i++)
        {
            var entry = orderedEntries[i];
            newStatuses[i] = entry.Status;
            newIssues.AddRange(entry.Issues);
            if (entry.Accepted is not { } accepted)
            {
                continue;
            }

            foreach (var item in accepted.Tools)
            {
                tools.Add(item.Tool);
            }

            foreach (var item in accepted.Commands)
            {
                commands.Add(item.Command);
            }

            foreach (var item in accepted.Workflows)
            {
                workflows.Add(item.Workflow);
            }

            foreach (var item in accepted.Agents)
            {
                agents.Add(item.Agent);
            }

            foreach (var item in accepted.PromptContributors)
            {
                contributors.Add(item.Contributor);
            }

            foreach (var item in accepted.Panels)
            {
                panels.Add(item.Panel);
            }
        }

        registrations = new PluginRegistrations(
            Array.AsReadOnly(tools.ToArray()),
            Array.AsReadOnly(commands.ToArray()),
            Array.AsReadOnly(workflows.ToArray()),
            Array.AsReadOnly(agents.ToArray()),
            Array.AsReadOnly(contributors.ToArray()),
            Array.AsReadOnly(panels.ToArray()));
        statuses = newStatuses;
        issues = [.. newIssues];
    }

    private string[] SnapshotIds()
    {
        lock (writeLock)
        {
            var ids = new string[orderedEntries.Length];
            for (var i = 0; i < orderedEntries.Length; i++)
            {
                ids[i] = orderedEntries[i].Id;
            }

            return ids;
        }
    }

    private void Raise(PluginChange? change)
    {
        if (change is null)
        {
            return;
        }

        Changed?.Invoke(this, new PluginChangedEventArgs(change));
    }

    private void Raise(List<PluginChange> changes)
    {
        var handler = Changed;
        if (handler is null || changes.Count == 0)
        {
            return;
        }

        foreach (var change in changes)
        {
            handler(this, new PluginChangedEventArgs(change));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PluginHost));
        }
    }

    private static PluginLoadIssue Issue(PluginEntry entry, PluginIssueKind kind, string message) =>
        new(entry.Id, kind, message, entry.Directory);

    private static string Describe(Exception exception)
    {
        if (exception is ReflectionTypeLoadException reflection && reflection.LoaderExceptions.Length > 0)
        {
            var loaderMessages = new List<string>(reflection.LoaderExceptions.Length);
            foreach (var loaderException in reflection.LoaderExceptions)
            {
                if (loaderException is not null)
                {
                    loaderMessages.Add($"{loaderException.GetType().Name}: {loaderException.Message}");
                }
            }

            return $"{reflection.GetType().Name}: {string.Join("; ", loaderMessages)}";
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private sealed class NameSets
    {
        public HashSet<string> Tools { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Commands { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Workflows { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Agents { get; } = new(StringComparer.Ordinal);

        public HashSet<string> PromptContributors { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Panels { get; } = new(StringComparer.Ordinal);
    }
}
