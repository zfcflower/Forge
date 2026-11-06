using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Workspace;
using ICommand = System.Windows.Input.ICommand;

namespace Forge.App.ViewModels;

internal enum TreeNodeKind
{
    General,
    Project,
    Worktree,
    History,
    Session,
    MainAgent,
    SubAgent,
}

/// <summary>The state of an agent row, classified from its stored status text.</summary>
internal enum AgentState
{
    /// <summary>Finished but the stored messages do not say how, or the row has no agent status.</summary>
    Ended,
    Queued,
    Running,
    Completed,
    Failed,
    Stopped,
}

internal static class AgentStateClassifier
{
    /// <summary>
    /// Maps the status text of an agent row to its state; anything unknown is neutral "ended". The texts are compared in
    /// the current language: the snapshots are built at the same moment and rebuilt on a language switch, so the two
    /// always agree. Not constants any more, because the texts follow the UI language.
    /// </summary>
    public static AgentState Classify(string statusText)
    {
        if (Matches(statusText, SessionStrings.SubAgentQueued))
        {
            return AgentState.Queued;
        }

        if (Matches(statusText, SessionStrings.SubAgentRunning))
        {
            return AgentState.Running;
        }

        if (Matches(statusText, SessionStrings.SubAgentCompleted))
        {
            return AgentState.Completed;
        }

        if (Matches(statusText, SessionStrings.SubAgentFailed))
        {
            return AgentState.Failed;
        }

        return Matches(statusText, SessionStrings.SubAgentStopped) ? AgentState.Stopped : AgentState.Ended;
    }

    private static bool Matches(string statusText, string candidate) =>
        string.Equals(statusText, candidate, StringComparison.Ordinal);
}

/// <summary>
/// One row of the left project tree. A single node type keeps the view to one template: projects hold worktrees,
/// worktrees hold sessions plus a collapsible history group, and the general section holds sessions only.
/// </summary>
internal sealed class TreeNodeViewModel : ObservableObject, IFlatTreeNode<TreeNodeViewModel>
{
    private bool isExpanded = true;
    private bool isRunning;
    private string title = string.Empty;
    private string subtitle = string.Empty;
    private string statusText = string.Empty;
    private AgentState agentState = AgentState.Ended;
    private Geometry icon = TreeIcons.Session;
    private int historyCount;
    private bool archived;

    public TreeNodeViewModel(TreeNodeKind kind)
    {
        Kind = kind;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Stable identity across rebuilds, so a refresh keeps this instance (and its expansion state).</summary>
    public string Key { get; init; } = string.Empty;

    public TreeNodeKind Kind { get; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>The flat row list the panel virtualizes; a tree node only knows its own subtree.</summary>
    IReadOnlyList<TreeNodeViewModel> IFlatTreeNode<TreeNodeViewModel>.Children => Children;

    /// <summary>Told by the panel when this row expands or collapses, so the flat row list splices its subtree.</summary>
    public Action<TreeNodeViewModel>? ExpansionChanged { get; set; }

    /// <summary>The icon of the row: a folder/project/session shape, or the shape of the agent kind for an agent.</summary>
    public Geometry Icon
    {
        get => icon;
        set => Set(ref icon, value);
    }

    /// <summary>Indent step count; agent rows indent by level only and get no guide lines.</summary>
    public int Depth { get; private set; }

    public IReadOnlyList<IndentMark> Guides => Kind is TreeNodeKind.MainAgent or TreeNodeKind.SubAgent
        ? IndentMarks.Spacer(Depth)
        : IndentMarks.For(Depth);

    public bool IsAgent => Kind is TreeNodeKind.MainAgent or TreeNodeKind.SubAgent;

    public void SetDepth(int depth)
    {
        if (Depth == depth)
        {
            return;
        }

        Depth = depth;
        OnPropertyChanged(nameof(Depth));
        OnPropertyChanged(nameof(Guides));
    }

    /// <summary>
    /// The row label. Project, worktree, session and agent rows show stored data (a name, a title, a task); the general
    /// and history rows have no data of their own, so their label is resolved in the current language on every read.
    /// </summary>
    public string Title
    {
        get => Kind switch
        {
            TreeNodeKind.General => ProjectStrings.GeneralSection,
            TreeNodeKind.History => ProjectStrings.HistoryTitle(historyCount),
            _ => title,
        };
        set => Set(ref title, value);
    }

    /// <summary>Archived sessions of a history row; the row title is formatted from it, so it follows the language too.</summary>
    public int HistoryCount
    {
        get => historyCount;
        set
        {
            if (Set(ref historyCount, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>True for an archived session row: its subtitle is the archived label, resolved on read.</summary>
    public bool IsArchived
    {
        get => archived;
        set
        {
            if (archived == value)
            {
                return;
            }

            archived = value;
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    /// <summary>The grey second line: a path, the last-active stamp, or the archived label of a session row.</summary>
    public string Subtitle
    {
        get => archived ? ProjectStrings.ArchivedSubtitle : subtitle;
        set
        {
            if (Set(ref subtitle, value))
            {
                OnPropertyChanged(nameof(HasSubtitle));
            }
        }
    }

    public bool HasSubtitle => archived || subtitle.Length > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronRotated));
                ExpansionChanged?.Invoke(this);
            }
        }
    }

    /// <summary>The chevron points right when collapsed and down when expanded; the view rotates it.</summary>
    public bool ChevronRotated => isExpanded;

    public bool HasToggle => Children.Count > 0;

    public bool HasOpen => OpenCommand is not null;

    public bool HasNewSession => NewSessionCommand is not null;

    public bool IsRunning
    {
        get => isRunning;
        set
        {
            if (Set(ref isRunning, value))
            {
                OnPropertyChanged(nameof(RunningText));
            }
        }
    }

    public string RunningText => isRunning ? ProjectStrings.RunningLabel : string.Empty;

    /// <summary>
    /// Re-raises the labels this row resolves from the current language. The owning panel calls it on a language switch;
    /// a row must not subscribe to the language event itself, or every tree row would outlive the tree inside the string table.
    /// </summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(RunningText));
    }

    /// <summary>The state line of the row: an agent's run state, or empty when the row has none.</summary>
    public string StatusText
    {
        get => statusText;
        set
        {
            if (Set(ref statusText, value))
            {
                if (IsAgent)
                {
                    AgentState = AgentStateClassifier.Classify(statusText);
                }

                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => statusText.Length > 0;

    /// <summary>The classified state of an agent row, derived from <see cref="StatusText"/>; neutral for other rows.</summary>
    public AgentState AgentState
    {
        get => agentState;
        private set
        {
            if (agentState == value)
            {
                return;
            }

            agentState = value;
            OnPropertyChanged(nameof(AgentState));
            OnPropertyChanged(nameof(IsAgentRunning));
            OnPropertyChanged(nameof(IsAgentQueued));
            OnPropertyChanged(nameof(IsAgentCompleted));
            OnPropertyChanged(nameof(IsAgentFailed));
            OnPropertyChanged(nameof(IsAgentStopped));
            OnPropertyChanged(nameof(IsAgentRunningAnimated));
        }
    }

    public bool IsAgentRunning => agentState == AgentState.Running;

    public bool IsAgentQueued => agentState == AgentState.Queued;

    public bool IsAgentCompleted => agentState == AgentState.Completed;

    public bool IsAgentFailed => agentState == AgentState.Failed;

    public bool IsAgentStopped => agentState == AgentState.Stopped;

    /// <summary>True when the running agent's breathing animation may play (the reduced-motion gate is off).</summary>
    public bool IsAgentRunningAnimated => IsAgentRunning && !TreeMotion.ReducedMotion;

    public string SessionId { get; init; } = string.Empty;

    public string SessionStatus { get; init; } = string.Empty;

    public ICommand ToggleCommand { get; }

    public ICommand? OpenCommand { get; private set; }

    public ICommand? NewSessionCommand { get; private set; }

    public ICommand? ArchiveCommand { get; private set; }

    public ICommand? RenameCommand { get; private set; }

    public ICommand? RevealCommand { get; private set; }

    public ICommand? RemoveCommand { get; private set; }

    /// <summary>Replaces the label state of a reused node without touching its expansion state.</summary>
    public void Update(string newTitle, string newSubtitle, bool running)
    {
        Title = newTitle;
        Subtitle = newSubtitle;
        IsRunning = running;
    }

    /// <summary>Replaces the whole command set; a reused node must not keep the handlers of an older rebuild.</summary>
    public void SetCommands(
        ICommand? open,
        ICommand? newSession,
        ICommand? archive,
        ICommand? rename,
        ICommand? reveal,
        ICommand? remove)
    {
        OpenCommand = open;
        NewSessionCommand = newSession;
        ArchiveCommand = archive;
        RenameCommand = rename;
        RevealCommand = reveal;
        RemoveCommand = remove;
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(HasNewSession));
    }

    public void NotifyChildrenChanged() => OnPropertyChanged(nameof(HasToggle));
}

/// <summary>One planned node of a rebuild; plain data, so the tree can be planned on a pool thread.</summary>
internal sealed record TreeNodePlan(
    TreeNodeKind Kind,
    string Key,
    string Title,
    string Subtitle,
    bool IsRunning,
    ProjectInfo? Project,
    SessionInfo? Session,
    string WorktreePath,
    string? Branch,
    bool StartCollapsed,
    IReadOnlyList<TreeNodePlan> Children)
{
    /// <summary>Agent kind of a sub-agent row, empty for every other row; it selects the agent icon.</summary>
    public string AgentKind { get; init; } = string.Empty;

    /// <summary>The run state shown on an agent row, empty when the row has none.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Archived sessions of a history row; the row formats its title from it in the current language.</summary>
    public int HistoryCount { get; init; }

    /// <summary>True for an archived session row, whose subtitle is the archived label instead of the last-active stamp.</summary>
    public bool IsArchived { get; init; }
}

/// <summary>One agent of a session as the left panel shows it: the main agent, or a sub-agent and its parent link.</summary>
internal sealed record AgentNodeSnapshot(
    string AgentId,
    string? ParentAgentId,
    string Kind,
    string Task,
    string StatusText,
    bool IsLive,
    bool IsMain);

/// <summary>
/// The left panel: the general conversation area on top, then registered projects with their worktrees, sessions and the
/// archived history of each worktree. A new session on a project creates an isolated worktree; on a worktree it shares it.
/// A refresh is planned off the dispatcher and merged into the existing nodes, so expansion and scroll position survive.
/// </summary>
internal sealed class ProjectTreeViewModel : ObservableObject, IDisposable
{
    private readonly IProjectRegistry projects;
    private readonly ISessionCatalog catalog;
    private readonly WorktreeService worktrees;
    private readonly Func<string, CancellationToken, Task> openSession;
    private readonly Func<Func<CancellationToken, Task<SessionInfo>>, Task<bool>> createConfirmed;
    private readonly Func<string, string, Task<string?>> promptText;
    private readonly Func<string, Task> reveal;
    private readonly Func<CancellationToken, Task> addProject;
    private readonly FlatTree<TreeNodeViewModel> tree = new();
    private bool busy;
    private bool refreshPending;
    private LocText status;

    public ProjectTreeViewModel(
        IProjectRegistry projects,
        ISessionCatalog catalog,
        WorktreeService worktrees,
        Func<string, CancellationToken, Task> openSession,
        Func<Func<CancellationToken, Task<SessionInfo>>, Task<bool>> createConfirmed,
        Func<string, string, Task<string?>> promptText,
        Func<string, Task> reveal,
        Func<CancellationToken, Task> addProject)
    {
        this.projects = projects;
        this.catalog = catalog;
        this.worktrees = worktrees;
        this.openSession = openSession;
        this.createConfirmed = createConfirmed;
        this.promptText = promptText;
        this.reveal = reveal;
        this.addProject = addProject;
        AddProjectCommand = new AsyncRelayCommand(ct => addProject(ct), () => true, OnFailed);
        GeneralSessionCommand = new AsyncRelayCommand(ct => CreateGeneralSessionAsync(ct), () => true, OnFailed);
        RefreshCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => true, OnFailed);

        // The tree is a long-lived owner: it is the one that hears a language switch and pushes the rows to re-render.
        // Rows stay unsubscribed (RefreshText is called on them), so closing a window does not leave rows in the table.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TreeNodeViewModel> Sections { get; } = [];

    /// <summary>The visible rows of the whole tree in display order, one flat list the view virtualizes. Expanding or
    /// collapsing a row splices only that subtree, so the rest of the list keeps its containers and scroll offset.</summary>
    public RangeObservableCollection<TreeNodeViewModel> Rows => tree.Rows;

    public TreeNodeViewModel? GeneralSection { get; private set; }

    public AsyncRelayCommand AddProjectCommand { get; }

    public AsyncRelayCommand GeneralSessionCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public int SectionCount => Sections.Count;

    /// <summary>Ids of sessions whose turn is running, read on every rebuild so the green dot survives a refresh.</summary>
    public Func<HashSet<string>>? RunningSessions { get; set; }

    /// <summary>The agents of one session, as a flat list with parent links; called before a rebuild leaves the dispatcher.</summary>
    public Func<string, IReadOnlyList<AgentNodeSnapshot>>? AgentTrees { get; set; }

    /// <summary>Called after a session was archived, so the shell can drop its runtime when nothing else uses it.</summary>
    public Action<string>? SessionArchived { get; set; }

    /// <summary>The status line under the add-project button: stored as a key, so it re-renders when the language changes.</summary>
    public string StatusText => status.Resolve();

    public bool HasStatusText => !status.IsEmpty;

    /// <summary>
    /// Releases the language subscription. The tree is the owner the rows are refreshed from, so nothing else has to be
    /// unsubscribed; the call is idempotent.
    /// </summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>Registers the general workspace project and rebuilds the tree.</summary>
    public async Task InitializeAsync(string generalPath, CancellationToken ct)
    {
        try
        {
            await BackgroundRead.RunAsync(
                    token => projects.AddAsync(generalPath, WorktreeService.GeneralFolderName, ProjectKind.General, token),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            SetStatus(ProjectStrings.GeneralWorkspaceFailed(exception.Message));
        }

        await RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// One rebuild at a time; a request that arrives while a rebuild runs is coalesced into a single follow-up run instead
    /// of being dropped, so a turn or watcher tick during a refresh is never lost.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (busy)
        {
            refreshPending = true;
            return;
        }

        busy = true;
        RefreshCommands();
        try
        {
            do
            {
                refreshPending = false;
                await RefreshOnceAsync(ct).ConfigureAwait(true);
            }
            while (refreshPending);
        }
        finally
        {
            busy = false;
            RefreshCommands();
        }
    }

    /// <summary>
    /// Rebuilds only the agent subtree of every session row, so a sub-agent that starts, changes state or finishes shows up
    /// without re-reading the session catalog.
    /// </summary>
    public void RefreshAgentTrees()
    {
        var provider = AgentTrees;
        if (provider is null)
        {
            return;
        }

        foreach (var node in Enumerate(Sections).ToArray())
        {
            if (node.Kind != TreeNodeKind.Session || node.SessionId.Length == 0)
            {
                continue;
            }

            Merge(node.Children, PlanAgents(provider(node.SessionId)), node.Depth + 1);
            node.NotifyChildrenChanged();
        }

        RebuildRows();
    }

    public void MarkRunning(string sessionId, bool isRunning)
    {
        foreach (var node in Enumerate(Sections))
        {
            if (node.Kind == TreeNodeKind.Session && string.Equals(node.SessionId, sessionId, StringComparison.Ordinal))
            {
                node.IsRunning = isRunning;
            }
        }
    }

    /// <summary>Archives a session: it leaves the worktree list and shows up under "历史".</summary>
    public async Task ArchiveAsync(string sessionId, CancellationToken ct)
    {
        await BackgroundRead.RunAsync(token => catalog.SetStatusAsync(sessionId, SessionStatus.Archived, token), ct)
            .ConfigureAwait(true);
        SessionArchived?.Invoke(sessionId);
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    public async Task RenameAsync(string sessionId, string title, CancellationToken ct)
    {
        await BackgroundRead.RunAsync(token => catalog.RenameAsync(sessionId, title, token), ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Unregisters a project; sessions, worktrees and files are untouched.</summary>
    public async Task RemoveProjectAsync(string projectId, CancellationToken ct)
    {
        await BackgroundRead.RunAsync(token => projects.RemoveAsync(projectId, token), ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            var all = await BackgroundRead.RunAsync(token => projects.ListAsync(token), ct).ConfigureAwait(true);
            var sessions = await BackgroundRead.RunAsync(QueryAllSessionsAsync, ct).ConfigureAwait(true);

            // The session rows of the tree come from collections the dispatcher owns, so both the running set and the
            // agent trees are snapshotted here, before the planning leaves the UI thread.
            var running = RunningSessionIds();
            var agents = SnapshotAgentTrees(sessions);
            var plans = await Task.Run(() => Plan(all, sessions, running, agents), ct).ConfigureAwait(true);
            Merge(Sections, plans);
            RebuildRows();
            GeneralSection = Sections.FirstOrDefault(static node => node.Kind == TreeNodeKind.General);
            SetStatus(all.Count <= 1 ? ProjectStrings.EmptyProjects : default);
            OnPropertyChanged(nameof(SectionCount));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            SetStatus(ProjectStrings.TreeFailed(exception.Message));
        }
    }

    /// <summary>One immutable agent list per session; must be called on the dispatcher, which owns the session view models.</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> SnapshotAgentTrees(IReadOnlyList<SessionInfo> sessions)
    {
        var provider = AgentTrees;
        var snapshot = new Dictionary<string, IReadOnlyList<AgentNodeSnapshot>>(StringComparer.Ordinal);
        if (provider is null)
        {
            return snapshot;
        }

        foreach (var session in sessions)
        {
            var agents = provider(session.Id);
            if (agents.Count > 0)
            {
                snapshot[session.Id] = agents;
            }
        }

        return snapshot;
    }

    /// <summary>Builds the whole tree as plain data, so the queries and the mapping stay off the dispatcher.</summary>
    private static List<TreeNodePlan> Plan(
        IReadOnlyList<ProjectInfo> all,
        IReadOnlyList<SessionInfo> sessions,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents)
    {
        var plans = new List<TreeNodePlan>(all.Count);
        foreach (var project in all.OrderBy(static p => p.Kind == ProjectKind.General ? 0 : 1).ThenBy(static p => p.SortOrder))
        {
            var projectSessions = sessions
                .Where(session => string.Equals(session.ProjectId, project.Id, StringComparison.Ordinal))
                .ToArray();

            plans.Add(project.Kind == ProjectKind.General
                ? PlanGeneral(project, projectSessions, running, agents)
                : PlanProject(project, projectSessions, running, agents));
        }

        return plans;
    }

    private static TreeNodePlan PlanGeneral(
        ProjectInfo project,
        IReadOnlyList<SessionInfo> sessions,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents)
    {
        var children = new List<TreeNodePlan>();
        foreach (var session in sessions.Where(static s => s.Status == SessionStatus.Active))
        {
            children.Add(PlanSession(session, running, agents));
        }

        var archived = sessions.Where(static s => s.Status == SessionStatus.Archived).ToArray();
        if (archived.Length > 0)
        {
            children.Add(PlanHistory(project, project.RootPath, archived, running, agents));
        }

        return new TreeNodePlan(
            TreeNodeKind.General,
            "general:" + project.Id,
            string.Empty, // the row resolves its label in the current language
            project.RootPath,
            false,
            project,
            null,
            project.RootPath,
            null,
            false,
            children);
    }

    private static TreeNodePlan PlanProject(
        ProjectInfo project,
        IReadOnlyList<SessionInfo> sessions,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents)
    {
        var groups = sessions
            .Where(session => !string.IsNullOrEmpty(session.WorktreePath) || session.ParentSessionId is null)
            .GroupBy(session => session.WorktreePath ?? project.RootPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(static s => s.LastActiveAt));

        var children = new List<TreeNodePlan>();
        foreach (var group in groups)
        {
            var branch = group.First().Branch;
            var worktreeChildren = new List<TreeNodePlan>();
            foreach (var session in group.Where(static s => s.Status == SessionStatus.Active))
            {
                worktreeChildren.Add(PlanSession(session, running, agents));
            }

            var archived = group.Where(static s => s.Status == SessionStatus.Archived).ToArray();
            if (archived.Length > 0)
            {
                worktreeChildren.Add(PlanHistory(project, group.Key, archived, running, agents));
            }

            children.Add(new TreeNodePlan(
                TreeNodeKind.Worktree,
                "worktree:" + project.Id + "|" + group.Key,
                branch ?? Path.GetFileName(group.Key),
                group.Key,
                false,
                project,
                null,
                group.Key,
                branch,
                false,
                worktreeChildren));
        }

        return new TreeNodePlan(
            TreeNodeKind.Project,
            "project:" + project.Id,
            project.Name,
            project.RootPath,
            false,
            project,
            null,
            project.RootPath,
            null,
            false,
            children);
    }

    private static TreeNodePlan PlanHistory(
        ProjectInfo project,
        string worktreePath,
        IReadOnlyList<SessionInfo> sessions,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents) =>
        new(
            TreeNodeKind.History,
            "history:" + project.Id + "|" + worktreePath,
            string.Empty, // the row formats its label from HistoryCount in the current language
            string.Empty,
            false,
            project,
            null,
            worktreePath,
            null,
            true,
            sessions.Select(session => PlanSession(session, running, agents)).ToArray())
        {
            HistoryCount = sessions.Count,
        };

    private static TreeNodePlan PlanSession(
        SessionInfo session,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents) =>
        new(
            TreeNodeKind.Session,
            "session:" + session.Id,
            session.Title,
            session.Status == SessionStatus.Archived ? string.Empty : TextFormat.ShortStamp(session.LastActiveAt),
            running.Contains(session.Id),
            null,
            session,
            session.WorktreePath ?? session.WorkspacePath,
            session.Branch,
            false,
            agents.TryGetValue(session.Id, out var snapshot) && snapshot.Count > 0 ? PlanAgents(snapshot) : [])
        {
            IsArchived = session.Status == SessionStatus.Archived,
        };

    /// <summary>Nests a session's agents by the parent link into 主 Agent → 子 Agent → 孙 Agent rows.</summary>
    internal static List<TreeNodePlan> PlanAgents(IReadOnlyList<AgentNodeSnapshot> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return [];
        }

        var byParent = new Dictionary<string, List<AgentNodeSnapshot>>(StringComparer.Ordinal);
        foreach (var agent in snapshot)
        {
            if (agent.ParentAgentId is { Length: > 0 } parent)
            {
                if (!byParent.TryGetValue(parent, out var siblings))
                {
                    siblings = [];
                    byParent[parent] = siblings;
                }

                siblings.Add(agent);
            }
        }

        var plans = new List<TreeNodePlan>();
        foreach (var agent in snapshot.Where(static a => a.IsMain))
        {
            plans.Add(PlanAgent(agent, byParent));
        }

        foreach (var agent in snapshot)
        {
            if (!agent.IsMain && (agent.ParentAgentId is not { Length: > 0 } parent || snapshot.All(a => !string.Equals(a.AgentId, parent, StringComparison.Ordinal))))
            {
                plans.Add(PlanAgent(agent, byParent));
            }
        }

        return plans;
    }

    private static TreeNodePlan PlanAgent(AgentNodeSnapshot agent, Dictionary<string, List<AgentNodeSnapshot>> byParent)
    {
        var children = new List<TreeNodePlan>();
        if (byParent.TryGetValue(agent.AgentId, out var nested))
        {
            foreach (var child in nested)
            {
                children.Add(PlanAgent(child, byParent));
            }
        }

        return new TreeNodePlan(
            agent.IsMain ? TreeNodeKind.MainAgent : TreeNodeKind.SubAgent,
            "agent:" + agent.AgentId,
            agent.Task,
            string.Empty,
            agent.IsLive,
            null,
            null,
            string.Empty,
            null,
            false,
            children)
        {
            AgentKind = agent.Kind,
            StatusText = agent.StatusText,
        };
    }

    /// <summary>
    /// Applies <paramref name="plans"/> to <paramref name="target"/> while keeping every node that is still part of the
    /// tree, so expansion state and the scroll position survive a refresh.
    /// </summary>
    private void Merge(ObservableCollection<TreeNodeViewModel> target, IReadOnlyList<TreeNodePlan> plans, int depth = 0)
    {
        var existing = new Dictionary<string, TreeNodeViewModel>(StringComparer.Ordinal);
        foreach (var node in target)
        {
            if (node.Key.Length > 0)
            {
                existing[node.Key] = node;
            }
        }

        var ordered = new List<TreeNodeViewModel>(plans.Count);
        foreach (var plan in plans)
        {
            TreeNodeViewModel node;
            if (existing.Remove(plan.Key, out var reused))
            {
                node = reused;
                Apply(node, plan);
            }
            else
            {
                node = Create(plan);
            }

            node.SetDepth(depth);
            Merge(node.Children, plan.Children, depth + 1);
            node.NotifyChildrenChanged();
            ordered.Add(node);
        }

        CollectionSync.Sync(target, ordered);
    }

    private TreeNodeViewModel Create(TreeNodePlan plan)
    {
        var node = new TreeNodeViewModel(plan.Kind)
        {
            Key = plan.Key,
            SessionId = plan.Session?.Id ?? string.Empty,
            SessionStatus = plan.Session?.Status.ToString() ?? string.Empty,
            IsExpanded = !plan.StartCollapsed,
            ExpansionChanged = OnNodeExpansionChanged,
        };
        Apply(node, plan);
        return node;
    }

    /// <summary>Re-applies the flat row list to the merged tree, keeping the rows that are still there.</summary>
    private void RebuildRows() => CollectionSync.Sync(Rows, FlatTree<TreeNodeViewModel>.Flatten(Sections));

    private void OnNodeExpansionChanged(TreeNodeViewModel node) => tree.ExpansionChanged(node);

    private static Geometry IconOf(TreeNodePlan plan) => plan.Kind switch
    {
        TreeNodeKind.General => TreeIcons.General,
        TreeNodeKind.Project => TreeIcons.Project,
        TreeNodeKind.Worktree => TreeIcons.Worktree,
        TreeNodeKind.History => TreeIcons.History,
        TreeNodeKind.MainAgent => TreeIcons.MainAgent,
        TreeNodeKind.SubAgent => TreeIcons.IconOf(plan.AgentKind),
        _ => TreeIcons.Session,
    };

    private void Apply(TreeNodeViewModel node, TreeNodePlan plan)
    {
        node.Update(plan.Title, plan.Subtitle, plan.IsRunning);
        node.HistoryCount = plan.HistoryCount;
        node.IsArchived = plan.IsArchived;
        node.StatusText = plan.StatusText;
        node.Icon = IconOf(plan);
        switch (plan.Kind)
        {
            case TreeNodeKind.General:
                node.SetCommands(
                    open: null,
                    newSession: new AsyncRelayCommand(ct => CreateGeneralSessionAsync(ct), () => true, OnFailed),
                    archive: null,
                    rename: null,
                    reveal: new AsyncRelayCommand(ct => reveal(plan.WorktreePath), () => true, OnFailed),
                    remove: null);
                break;
            case TreeNodeKind.Project when plan.Project is { } project:
                node.SetCommands(
                    open: null,
                    newSession: new AsyncRelayCommand(ct => CreateWorktreeSessionAsync(project, ct), () => true, OnFailed),
                    archive: null,
                    rename: new AsyncRelayCommand(ct => RenameProjectAsync(project, ct), () => true, OnFailed),
                    reveal: new AsyncRelayCommand(ct => reveal(project.RootPath), () => true, OnFailed),
                    remove: new AsyncRelayCommand(ct => RemoveProjectAsync(project.Id, ct), () => true, OnFailed));
                break;
            case TreeNodeKind.Worktree when plan.Project is { } project:
                node.SetCommands(
                    open: null,
                    newSession: new AsyncRelayCommand(
                        ct => CreateSessionInWorktreeAsync(project, plan.WorktreePath, plan.Branch, ct),
                        () => !busy,
                        OnFailed),
                    archive: null,
                    rename: null,
                    reveal: new AsyncRelayCommand(ct => reveal(plan.WorktreePath), () => true, OnFailed),
                    remove: null);
                break;
            case TreeNodeKind.Session when plan.Session is { } session:
                node.SetCommands(
                    open: new AsyncRelayCommand(ct => OpenSessionAsync(session, ct), () => true, OnFailed),
                    newSession: null,
                    archive: new AsyncRelayCommand(ct => ArchiveAsync(session.Id, ct), () => session.Status == SessionStatus.Active, OnFailed),
                    rename: new AsyncRelayCommand(ct => RenameSessionAsync(session, ct), () => true, OnFailed),
                    reveal: new AsyncRelayCommand(ct => reveal(session.WorkspacePath), () => true, OnFailed),
                    remove: null);
                break;
            default:
                node.SetCommands(open: null, newSession: null, archive: null, rename: null, reveal: null, remove: null);
                break;
        }
    }

    private HashSet<string> RunningSessionIds() => RunningSessions?.Invoke() ?? [];

    private async Task OpenSessionAsync(SessionInfo session, CancellationToken ct)
    {
        if (session.Status == SessionStatus.Archived)
        {
            await BackgroundRead.RunAsync(token => catalog.SetStatusAsync(session.Id, SessionStatus.Active, token), ct)
                .ConfigureAwait(true);
        }

        await openSession(session.Id, ct).ConfigureAwait(true);
        await RefreshAsync(ct).ConfigureAwait(true);
    }

    private Task CreateGeneralSessionAsync(CancellationToken ct) =>
        CreateImmediatelyAsync(
            token => BackgroundRead.RunAsync(
                async backgroundToken =>
                {
                    var general = await projects.ListAsync(backgroundToken).ConfigureAwait(false);
                    var project = general.FirstOrDefault(static p => p.Kind == ProjectKind.General)
                                  ?? throw new InvalidOperationException(ProjectStrings.GeneralWorkspaceMissing);
                    var title = ProjectStrings.GeneralSessionTitle();
                    var scratch = WorktreeService.CreateScratchFolder(project.RootPath, title);
                    return await catalog.CreateAsync(
                            new SessionCreate(scratch, title) { ProjectId = project.Id },
                            backgroundToken)
                        .ConfigureAwait(false);
                },
                token),
            ct);

    /// <summary>
    /// A new session on a project gets its own worktree. The repository probe runs first on the thread pool, so a folder
    /// that is not a git repository is reported without ever asking for a branch name; only a repository then puts the
    /// branch-name prompt on the UI thread. A blank answer cancels silently, and an unusable name stops the creation and
    /// reports why through the same failure path as the rest of the flow. Everything after the prompt (the worktree and
    /// the session row) runs on the thread pool like the other tree operations.
    /// </summary>
    private async Task CreateWorktreeSessionAsync(ProjectInfo project, CancellationToken ct)
    {
        var isRepository = await BackgroundRead
            .RunAsync(token => worktrees.IsRepositoryAsync(project.RootPath, token), ct)
            .ConfigureAwait(true);
        if (!isRepository)
        {
            throw new InvalidOperationException(ProjectStrings.NotAGitRepository);
        }

        var branch = await promptText(
                Loc.Instance[GitStrings.WorktreeBranchPromptTitle],
                DefaultBranchName())
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        var branchName = branch.Trim();
        await CreateAsync(
                token => BackgroundRead.RunAsync(
                    async backgroundToken =>
                    {
                        var title = ProjectStrings.WorktreeSessionTitle();
                        WorktreeSession worktree;
                        try
                        {
                            worktree = await worktrees.CreateAsync(project.RootPath, title, branchName, backgroundToken)
                                .ConfigureAwait(false);
                        }
                        catch (ArgumentException exception)
                        {
                            throw new InvalidOperationException(
                                Loc.Instance.Format(GitStrings.WorktreeBranchInvalid, branchName, exception.Message),
                                exception);
                        }
                        catch (GitException exception)
                        {
                            throw new InvalidOperationException(
                                Loc.Instance.Format(GitStrings.WorktreeBranchUnavailable, branchName, exception.Message),
                                exception);
                        }

                        return await catalog.CreateAsync(
                                new SessionCreate(worktree.Path, title)
                                {
                                    ProjectId = project.Id,
                                    WorktreePath = worktree.Path,
                                    Branch = worktree.Branch,
                                },
                                backgroundToken)
                            .ConfigureAwait(false);
                    },
                    token),
                ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// The branch name a new worktree session is prompted with: the worktree branch prefix plus a timestamped slug. The
    /// timestamp is part of the name because an explicitly typed branch is never suffixed (unlike the title-based default,
    /// whose Chinese title always reduced to <c>session</c>), so the default has to look distinct on its own.
    /// </summary>
    private string DefaultBranchName() =>
        worktrees.Manager.Options.BranchPrefix
        + "session-"
        + DateTimeOffset.Now.ToString("MMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    private Task CreateSessionInWorktreeAsync(ProjectInfo project, string worktreePath, string? branch, CancellationToken ct) =>
        CreateAsync(
            token => BackgroundRead.RunAsync(
                token2 =>
                {
                    var title = ProjectStrings.WorktreeSessionTitle();
                    return catalog.CreateAsync(
                        new SessionCreate(worktreePath, title)
                        {
                            ProjectId = project.Id,
                            WorktreePath = worktreePath,
                            Branch = branch,
                        },
                        token2);
                },
                token),
            ct);

    private async Task CreateImmediatelyAsync(Func<CancellationToken, Task<SessionInfo>> factory, CancellationToken ct)
    {
        try
        {
            var session = await factory(ct).ConfigureAwait(true);
            await openSession(session.Id, ct).ConfigureAwait(true);
            await RefreshAsync(ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is GitException or InvalidOperationException or IOException or ArgumentException)
        {
            SetStatus(LocText.Literal(exception.Message));
        }
    }

    private async Task CreateAsync(Func<CancellationToken, Task<SessionInfo>> factory, CancellationToken ct)
    {
        var confirmed = await createConfirmed(factory).ConfigureAwait(true);
        if (confirmed)
        {
            await RefreshAsync(ct).ConfigureAwait(true);
        }
    }

    private async Task RenameSessionAsync(SessionInfo session, CancellationToken ct)
    {
        var title = await promptText(ProjectStrings.RenameSessionTitle, session.Title).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(title))
        {
            await RenameAsync(session.Id, title.Trim(), ct).ConfigureAwait(true);
        }
    }

    private async Task RenameProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        var name = await promptText(ProjectStrings.RenameProjectTitle, project.Name).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim();
            await BackgroundRead.RunAsync(token => projects.RenameAsync(project.Id, trimmed, token), ct).ConfigureAwait(true);
            await RefreshAsync(ct).ConfigureAwait(true);
        }
    }

    private Task<IReadOnlyList<SessionInfo>> QueryAllSessionsAsync(CancellationToken ct) =>
        catalog.QueryAsync(new SessionQuery { ParentSessionId = string.Empty }, ct);

    private void OnFailed(Exception exception) => SetStatus(LocText.Literal(exception.Message));

    /// <summary>A language switch re-renders the stored status line and every row's label; the rows compute their text on read.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshStatus();
        foreach (var node in Enumerate(Sections))
        {
            node.RefreshText();
        }
    }

    private void SetStatus(LocText text)
    {
        status = text;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusText));
    }

    private void RefreshCommands()
    {
        RefreshStatus();
        AddProjectCommand.NotifyCanExecuteChanged();
        GeneralSessionCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<TreeNodeViewModel> Enumerate(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Enumerate(node.Children))
            {
                yield return child;
            }
        }
    }
}
