using System.Collections.ObjectModel;
using Avalonia.Threading;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.Mcp;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>Whether the prompt snapshot can be reloaded right now, and if not, why not.</summary>
internal enum PromptReloadState
{
    /// <summary>The session has no host to reload (read-only view, closed).</summary>
    Unavailable,

    /// <summary>Nothing holds the host: the reload works.</summary>
    Ready,

    /// <summary>A turn is running; the reload is refused until it ends.</summary>
    TurnRunning,

    /// <summary>A child started outside a turn is running; the reload is refused until it ends.</summary>
    AutomationRunning,
}

/// <summary>
/// The one session the resource window shows (implemented by <see cref="SessionWorkspaceViewModel"/>). The window only reads
/// through it and listens for "something changed"; it never writes into the session.
/// </summary>
internal interface IResourceInspectorSource
{
    /// <summary>UI thread: appends every live (queued or running) child an automation started, in the order they started.</summary>
    void CollectAutomationAgents(List<SubAgentViewModel> into);

    /// <summary>UI thread: what the session's tools and prompt snapshot cost in context. Empty before the first turn.</summary>
    ResourceUsage DescribeUsage();

    /// <summary>True while the prompt files changed on disk since the snapshot the host holds was taken.</summary>
    bool PromptsStale { get; }

    /// <summary>Whether the snapshot can be reloaded now.</summary>
    PromptReloadState PromptReload { get; }

    /// <summary>Opens the child's tab (the window's automation rows are links to it).</summary>
    void OpenAgent(SubAgentViewModel agent);

    /// <summary>UI thread: the automation set, the prompt snapshot or the reload state changed, or the session is closing.</summary>
    event EventHandler? RunningResourcesChanged;
}

/// <summary>One line of a usage table: a name with its tool count and its size in context.</summary>
internal sealed class ResourceUsageLineViewModel(string name, int tools, UsageSize size, bool isTotal = false)
{
    public string Name { get; } = name;

    public int Tools { get; } = tools;

    public string ToolsText => TextFormat.Number(Tools);

    public string CharsText => TextFormat.Number(size.Chars);

    public string TokensText => "≈ " + TextFormat.Number(size.Tokens);

    public bool IsTotal { get; } = isTotal;
}

/// <summary>One MCP server in the window's table: its run state, what its tools cost and the buttons that act on it.</summary>
internal sealed class ResourceMcpRowViewModel : NotifyingObject
{
    private readonly McpServices mcp;
    private McpServerState state;
    private int tools;
    private UsageSize size;

    public ResourceMcpRowViewModel(McpServices mcp, McpServerState state, Action<Exception, string> onError)
    {
        this.mcp = mcp;
        this.state = state;
        Name = state.Name;
        RestartCommand = new AsyncRelayCommand(
            ct => mcp.Manager.RestartAsync(Name, ct),
            () => state.Enabled,
            exception => onError(exception, Name));
        StopCommand = new AsyncRelayCommand(
            ct => mcp.Manager.StopAsync(Name, ct),
            () => state.Enabled,
            exception => onError(exception, Name));
        StartCommand = new AsyncRelayCommand(
            async ct => await mcp.Manager.StartAsync(Name, ct).ConfigureAwait(true),
            () => state.Enabled,
            exception => onError(exception, Name));
        SignInCommand = new AsyncRelayCommand(
            ct => mcp.SignInAsync(Name, ct),
            null,
            exception => onError(exception, Name));
    }

    public string Name { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand SignInCommand { get; }

    public McpServerStatus Status => state.Status;

    public bool IsReady => state.Status == McpServerStatus.Ready;

    public bool IsFailed => state.Status == McpServerStatus.Failed;

    public bool IsStarting => state.Status == McpServerStatus.Starting;

    public string StatusText => state.Enabled ? McpSettingsStrings.StatusOf(state) : ResourceInspectorStrings.McpDisabled;

    /// <summary>The failure message as the tooltip of the status; null while the server is not failed.</summary>
    public string? ErrorText => state.Status == McpServerStatus.Failed ? state.LastError : null;

    public string ToolsText => TextFormat.Number(tools);

    public string CharsText => TextFormat.Number(size.Chars);

    public string TokensText => "≈ " + TextFormat.Number(size.Tokens);

    /// <summary>Running or starting: the stop and reconnect buttons apply.</summary>
    public bool CanStopOrRestart => state.Enabled && state.Status is McpServerStatus.Ready or McpServerStatus.Starting or McpServerStatus.Failed;

    /// <summary>Enabled but not running: the start button applies.</summary>
    public bool CanStart => state.Enabled && state.Status == McpServerStatus.Stopped;

    public bool CanSignIn => state.NeedsSignIn && mcp.CanSignIn;

    public bool IsDisabled => !state.Enabled;

    public string RestartText => ResourceInspectorStrings.McpRestart;

    public string StopText => ResourceInspectorStrings.McpStop;

    public string StartText => ResourceInspectorStrings.McpStart;

    public string SignInText => ResourceInspectorStrings.McpSignIn;

    /// <summary>Applies the manager's current state and this server's usage; raises only what moved.</summary>
    internal void Update(McpServerState current, int toolCount, UsageSize usage)
    {
        var stateChanged = !Equals(state, current);
        state = current;
        if (stateChanged)
        {
            OnPropertyChanged(string.Empty);
            RestartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
        }

        if (toolCount != tools || usage != size)
        {
            tools = toolCount;
            size = usage;
            OnPropertyChanged(nameof(ToolsText));
            OnPropertyChanged(nameof(CharsText));
            OnPropertyChanged(nameof(TokensText));
        }
    }

    internal void RefreshText() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The resource window: for the one session whose composer button opened it, the automation agents that are running, the MCP
/// servers (with their reconnect and stop), what the tool definitions cost and what the prompt snapshot costs.
/// <para>It is event-driven, not polled: <see cref="Refresh"/> runs when the session says its automation set or prompt
/// snapshot changed, when the MCP manager reports a state or tool change (posted to the UI thread, coalesced into one
/// background-priority pass however many arrive in a burst) and when the language changes. Rows are reconciled in place, so a
/// binding and the scroll position survive every pass. The window holds listeners only while it is open (see
/// <see cref="Dispose"/>).</para>
/// </summary>
internal sealed class ResourceInspectorViewModel : NotifyingObject, IDisposable
{
    private static readonly Action<Action> DefaultPost =
        static action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    private readonly IResourceInspectorSource source;
    private readonly McpServices? mcp;
    private readonly Action? close;
    private readonly Action? reloadPrompts;
    private readonly Action<Action> post;
    private readonly List<SubAgentViewModel> agentBuffer = [];
    private readonly ObservableCollection<SubAgentViewModel> agents = [];
    private readonly ObservableCollection<ResourceMcpRowViewModel> mcpRows = [];
    private readonly ObservableCollection<ResourceUsageLineViewModel> toolRows = [];
    private readonly ObservableCollection<ResourceUsageLineViewModel> promptRows = [];
    private readonly Dictionary<string, ResourceMcpRowViewModel> mcpByName = new(StringComparer.Ordinal);
    private readonly EventHandler onSourceChanged;
    private readonly Action<McpServerState> onMcpState;
    private readonly Action onMcpTools;
    private readonly Action runQueuedRefresh;
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private bool promptsStale;
    private PromptReloadState reloadState;
    private int refreshQueued;

    // The usage snapshot costs a pass over every tool definition; a server flapping between states does not change it, so only
    // the session's own events and a tool-list change mark it dirty.
    private int usageDirty = 1;
    private ResourceUsage usageSnapshot = ResourceUsage.Empty;
    private bool disposed;

    /// <param name="source">The session the window shows.</param>
    /// <param name="mcp">The app-wide MCP services; null when the app runs without any.</param>
    /// <param name="close">Closes the overlay (its close button, Escape or a click on the scrim).</param>
    /// <param name="reloadPrompts">Reloads the session's prompt snapshot (and tells the user how it went).</param>
    /// <param name="post">How a coalesced refresh is queued; defaults to one background-priority dispatcher post.</param>
    public ResourceInspectorViewModel(
        IResourceInspectorSource source,
        McpServices? mcp,
        Action? close = null,
        Action? reloadPrompts = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.mcp = mcp;
        this.close = close;
        this.reloadPrompts = reloadPrompts;
        this.post = post ?? DefaultPost;
        onSourceChanged = (_, _) => RequestRefresh(usageChanged: true);
        onMcpState = _ => RequestRefresh(usageChanged: false);
        onMcpTools = () => RequestRefresh(usageChanged: true);
        runQueuedRefresh = RunQueuedRefresh;

        Agents = new ReadOnlyObservableCollection<SubAgentViewModel>(agents);
        McpServers = new ReadOnlyObservableCollection<ResourceMcpRowViewModel>(mcpRows);
        Tools = new ReadOnlyObservableCollection<ResourceUsageLineViewModel>(toolRows);
        Prompts = new ReadOnlyObservableCollection<ResourceUsageLineViewModel>(promptRows);
        CloseCommand = new RelayCommand(() => this.close?.Invoke(), () => !disposed);
        ReloadCommand = new RelayCommand(Reload, () => !disposed && reloadState == PromptReloadState.Ready);

        source.RunningResourcesChanged += onSourceChanged;
        if (mcp is not null)
        {
            mcp.Manager.StateChanged += onMcpState;
            mcp.Manager.ToolsChanged += onMcpTools;
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ReadOnlyObservableCollection<SubAgentViewModel> Agents { get; }

    public ReadOnlyObservableCollection<ResourceMcpRowViewModel> McpServers { get; }

    public ReadOnlyObservableCollection<ResourceUsageLineViewModel> Tools { get; }

    public ReadOnlyObservableCollection<ResourceUsageLineViewModel> Prompts { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public bool HasAgents => agents.Count > 0;

    public bool HasMcp => mcpRows.Count > 0;

    public bool HasTools => toolRows.Count > 0;

    public bool HasPrompts => promptRows.Count > 0;

    public bool HasUsage => HasTools || HasPrompts;

    public bool PromptsAreStale => promptsStale;

    /// <summary>Why the reload button is disabled; null when it works (or when there is nothing to reload).</summary>
    public string? ReloadBlockedReason => reloadState switch
    {
        PromptReloadState.TurnRunning => ResourceInspectorStrings.PromptsTurnRunning,
        PromptReloadState.AutomationRunning => ResourceInspectorStrings.PromptsAgentRunning,
        _ => null,
    };

    /// <summary>The reload button's tooltip: the reason it is disabled, otherwise what it does.</summary>
    public string ReloadTooltip => ReloadBlockedReason ?? ResourceInspectorStrings.PromptsReloadTooltip;

    public string Title => ResourceInspectorStrings.Title;

    public string CloseLabel => ResourceInspectorStrings.Close;

    public string CloseTooltip => ResourceInspectorStrings.CloseTooltip;

    public string AgentsTitle => ResourceInspectorStrings.AgentsTitle;

    public string AgentsEmpty => ResourceInspectorStrings.AgentsEmpty;

    public string AgentsOpenTooltip => ResourceInspectorStrings.AgentsOpenTooltip;

    public string McpTitle => ResourceInspectorStrings.McpTitle;

    public string McpNote => ResourceInspectorStrings.McpNote;

    public string McpEmpty => ResourceInspectorStrings.McpEmpty;

    public string McpServerHeader => ResourceInspectorStrings.McpServer;

    public string McpStatusHeader => ResourceInspectorStrings.McpStatus;

    public string ToolsTitle => ResourceInspectorStrings.ToolsTitle;

    public string ToolsNote => ResourceInspectorStrings.ToolsNote;

    public string ToolsCategoryHeader => ResourceInspectorStrings.ToolsCategory;

    public string ToolsEmpty => ResourceInspectorStrings.ToolsEmpty;

    public string PromptsTitle => ResourceInspectorStrings.PromptsTitle;

    public string PromptsSourceHeader => ResourceInspectorStrings.PromptsSource;

    public string PromptsEmpty => ResourceInspectorStrings.PromptsEmpty;

    public string PromptsStaleText => ResourceInspectorStrings.PromptsStale;

    public string PromptsReloadText => ResourceInspectorStrings.PromptsReload;

    public string ColumnTools => ResourceInspectorStrings.ColumnTools;

    public string ColumnChars => ResourceInspectorStrings.ColumnChars;

    public string ColumnTokens => ResourceInspectorStrings.ColumnTokens;

    public string EstimateNote => ResourceInspectorStrings.EstimateNote;

    /// <summary>Reads the session and the MCP manager once, so the window is drawn populated on its first frame.</summary>
    public void Start() => Refresh();

    /// <summary>
    /// Queues one refresh; further requests before it runs are absorbed. Safe from any thread (the refresh itself runs where
    /// <c>post</c> puts it, the UI thread by default).
    /// </summary>
    internal void RequestRefresh(bool usageChanged = true)
    {
        if (usageChanged)
        {
            Volatile.Write(ref usageDirty, 1);
        }

        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) == 1)
        {
            return;
        }

        post(runQueuedRefresh);
    }

    /// <summary>Re-reads everything and reconciles the rows in place. UI thread only.</summary>
    internal void Refresh()
    {
        if (disposed)
        {
            return;
        }

        RefreshAgents();
        if (Interlocked.Exchange(ref usageDirty, 0) == 1)
        {
            usageSnapshot = source.DescribeUsage();
        }

        var usage = usageSnapshot;
        RefreshMcp(usage);
        RefreshTools(usage);
        RefreshPrompts(usage);

        var stale = source.PromptsStale;
        var reload = source.PromptReload;
        var staleMoved = stale != promptsStale;
        var reloadMoved = reload != reloadState;
        promptsStale = stale;
        reloadState = reload;
        if (staleMoved)
        {
            OnPropertyChanged(nameof(PromptsAreStale));
        }

        if (reloadMoved)
        {
            OnPropertyChanged(nameof(ReloadBlockedReason));
            OnPropertyChanged(nameof(ReloadTooltip));
            ReloadCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        source.RunningResourcesChanged -= onSourceChanged;
        if (mcp is not null)
        {
            mcp.Manager.StateChanged -= onMcpState;
            mcp.Manager.ToolsChanged -= onMcpTools;
        }

        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        agents.Clear();
        mcpRows.Clear();
        mcpByName.Clear();
        CloseCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
    }

    private void RefreshAgents()
    {
        agentBuffer.Clear();
        source.CollectAutomationAgents(agentBuffer);
        var before = agents.Count;
        Reconcile(agents, agentBuffer);
        agentBuffer.Clear();
        if ((before == 0) != (agents.Count == 0))
        {
            OnPropertyChanged(nameof(HasAgents));
        }
    }

    private void RefreshMcp(ResourceUsage usage)
    {
        var before = mcpRows.Count;
        if (mcp is null)
        {
            if (before > 0)
            {
                mcpRows.Clear();
                mcpByName.Clear();
                OnPropertyChanged(nameof(HasMcp));
            }

            return;
        }

        var states = mcp.States;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            seen.Add(state.Name);
            var tools = 0;
            var size = default(UsageSize);
            for (var u = 0; u < usage.McpServers.Count; u++)
            {
                if (string.Equals(usage.McpServers[u].Server, state.Name, StringComparison.Ordinal))
                {
                    tools = usage.McpServers[u].Tools;
                    size = usage.McpServers[u].Size;
                    break;
                }
            }

            if (!mcpByName.TryGetValue(state.Name, out var row))
            {
                row = new ResourceMcpRowViewModel(mcp, state, OnMcpError);
                mcpByName[state.Name] = row;
                mcpRows.Insert(Math.Min(index, mcpRows.Count), row);
            }
            else if (index < mcpRows.Count && !ReferenceEquals(mcpRows[index], row))
            {
                // The manager's order is the table's order, also after a server was removed and added again.
                mcpRows.Move(mcpRows.IndexOf(row), index);
            }

            row.Update(state, tools, size);
        }

        for (var index = mcpRows.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(mcpRows[index].Name))
            {
                mcpByName.Remove(mcpRows[index].Name);
                mcpRows.RemoveAt(index);
            }
        }

        if ((before == 0) != (mcpRows.Count == 0))
        {
            OnPropertyChanged(nameof(HasMcp));
        }
    }

    private void RefreshTools(ResourceUsage usage)
    {
        var lines = new List<ResourceUsageLineViewModel>(usage.Tools.Count + 1);
        var total = default(UsageSize);
        var count = 0;
        foreach (var row in usage.Tools)
        {
            lines.Add(new ResourceUsageLineViewModel(InstrumentsStrings.ToolsGroup(row.Group), row.Tools, row.Size));
            total += row.Size;
            count += row.Tools;
        }

        if (lines.Count > 0)
        {
            lines.Add(new ResourceUsageLineViewModel(ResourceInspectorStrings.Total, count, total, isTotal: true));
        }

        ReplaceIfChanged(toolRows, lines, nameof(HasTools), nameof(HasUsage));
    }

    private void RefreshPrompts(ResourceUsage usage)
    {
        var lines = new List<ResourceUsageLineViewModel>(usage.Prompts.Count + 1);
        var total = default(UsageSize);
        foreach (var row in usage.Prompts)
        {
            lines.Add(new ResourceUsageLineViewModel(ResourceInspectorStrings.PromptSource(row.Kind), 0, row.Size));
            total += row.Size;
        }

        if (lines.Count > 0)
        {
            lines.Add(new ResourceUsageLineViewModel(ResourceInspectorStrings.Total, 0, total, isTotal: true));
        }

        ReplaceIfChanged(promptRows, lines, nameof(HasPrompts), nameof(HasUsage));
    }

    /// <summary>Swaps in the new lines only when text or numbers differ, so an idle pass changes nothing the view sees.</summary>
    private void ReplaceIfChanged(
        ObservableCollection<ResourceUsageLineViewModel> target,
        List<ResourceUsageLineViewModel> lines,
        string hasProperty,
        string hasUsageProperty)
    {
        var same = target.Count == lines.Count;
        for (var index = 0; same && index < lines.Count; index++)
        {
            same = target[index].Name == lines[index].Name
                && target[index].ToolsText == lines[index].ToolsText
                && target[index].CharsText == lines[index].CharsText
                && target[index].TokensText == lines[index].TokensText;
        }

        if (same)
        {
            return;
        }

        var hadAny = target.Count > 0;
        target.Clear();
        foreach (var line in lines)
        {
            target.Add(line);
        }

        if (hadAny != (target.Count > 0))
        {
            OnPropertyChanged(hasProperty);
            OnPropertyChanged(hasUsageProperty);
        }
    }

    /// <summary>Brings <paramref name="target"/> to <paramref name="desired"/> keeping instances that stayed.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, List<T> desired)
        where T : class
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index]))
            {
                continue;
            }

            var at = -1;
            for (var scan = index + 1; scan < target.Count; scan++)
            {
                if (ReferenceEquals(target[scan], desired[index]))
                {
                    at = scan;
                    break;
                }
            }

            if (at >= 0)
            {
                target.Move(at, index);
            }
            else
            {
                target.Insert(index, desired[index]);
            }
        }
    }

    private void Reload()
    {
        if (disposed || reloadState != PromptReloadState.Ready)
        {
            return;
        }

        reloadPrompts?.Invoke();
        Volatile.Write(ref usageDirty, 1);
        Refresh();
    }

    /// <summary>Opens the automation agent a row names in its own tab and closes the window (called by the row's name button).</summary>
    public void OpenAgent(SubAgentViewModel? agent)
    {
        if (agent is null || disposed)
        {
            return;
        }

        source.OpenAgent(agent);
        close?.Invoke();
    }

    private static void OnMcpError(Exception exception, string name) =>
        AppLogs.Instruments.ZLogWarning($"resource window: MCP server '{name}' action failed: {exception.Message}");

    private void RunQueuedRefresh()
    {
        Volatile.Write(ref refreshQueued, 0);
        if (disposed)
        {
            return;
        }

        try
        {
            Refresh();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A posted callback that throws takes the dispatcher's loop with it; a bad row is not worth the window.
            AppLogs.Instruments.ZLogWarning($"resource window refresh failed: {exception.Message}");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone; re-reading off this one would
        // touch bound controls from the wrong thread.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(string.Empty);
        foreach (var row in mcpRows)
        {
            row.RefreshText();
        }

        foreach (var agent in agents)
        {
            agent.RefreshText();
        }

        // Names and totals are localized text baked into the lines, so they are rebuilt rather than re-read.
        toolRows.Clear();
        promptRows.Clear();
        Refresh();
    }
}
