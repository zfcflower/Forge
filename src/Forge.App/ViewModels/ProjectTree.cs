using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Media;
using Forge.Abstractions;
using Forge.App.Lifecycle;
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

/// <summary>
/// What one project-tree panel shows. General chats and registered projects are two docked panels of the left column, so
/// each owns its own tree (and its own refresh, commands and header tools) over the same registry and session catalog.
/// </summary>
internal enum TreeScope
{
    /// <summary>Both areas in one tree, as a single panel shows them.</summary>
    Both,

    /// <summary>Only the general workspace and its sessions.</summary>
    General,

    /// <summary>Only the registered projects, with their worktrees, sessions and history.</summary>
    Projects,
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
/// <summary>The state mark after a row's title (lifecycle 5.1, 7.3-7.5); resolved in the current language on read.</summary>
internal enum TreeRowMark
{
    None,

    /// <summary>A workspace row whose sessions were all removed and whose directory is gone: greyed, restore or purge only.</summary>
    Recycled,

    /// <summary>A workspace row whose directory no longer exists while it still has sessions (5.3 "目录缺失").</summary>
    Missing,

    /// <summary>A removed (Deleted) session whose directory is gone.</summary>
    DirectoryRecycled,

    /// <summary>
    /// A workspace row whose directory exists but which <c>git worktree list --porcelain</c> marks prunable (its <c>.git</c>
    /// link no longer leads to the repository). Stale, unlike <see cref="Missing"/>: the folder is still there.
    /// </summary>
    Prunable,

    /// <summary>A workspace row whose directory exists but which the repository's worktree list does not name.</summary>
    Unregistered,

    /// <summary>
    /// A workspace row whose directory exists but whose git registration could not be read (the list failed): neither
    /// "normal" nor "stale", so nothing is disabled for it and the flows check after the click.
    /// </summary>
    Unverified,

    /// <summary>A removed (Deleted) session whose directory is still there (kept, shared, or rebuilt).</summary>
    Removed,
}

/// <summary>A repository workspace row as the removal / restore / purge commands see it (7.3, 7.5).</summary>
internal sealed record WorkspaceTarget(ProjectInfo Project, string Directory, string? Branch, string? ForkOfBranch);

internal sealed partial class TreeNodeViewModel : NotifyingObject, IFlatTreeNode<TreeNodeViewModel>
{
    private bool isExpanded = true;
    private bool isRunning;
    private bool isActiveSession;
    private bool isActiveAgent;
    private string title = string.Empty;
    private string subtitle = string.Empty;
    private string statusText = string.Empty;
    private AgentState agentState = AgentState.Ended;
    private Geometry icon = TreeIcons.Session;
    private Geometry newSessionGlyph = TreeIcons.Plus;
    private string newSessionTipKey = ProjectStrings.NewSessionTooltipKey;
    private int historyCount;
    private bool archived;
    private bool isMainCheckout;
    private string? forkOfBranch;
    private bool automationSession;
    private bool isConflicted;
    private TreeRowMark rowMark;

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

    /// <summary>
    /// One step per level for every row, agents included: a sub-agent row draws no chevron of its own, so a separate smaller
    /// step for it left its icon level with the parent's instead of under it.
    /// </summary>
    public IReadOnlyList<IndentMark> Guides => IndentMarks.Plain(Depth);

    public bool IsAgent => Kind is TreeNodeKind.MainAgent or TreeNodeKind.SubAgent;

    /// <summary>True for a project row: the heading of one registered project's subtree.</summary>
    public bool IsProject => Kind == TreeNodeKind.Project;

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
            // The repository root checkout has no branch name while HEAD is detached, so the plan leaves its stored title
            // empty and the label is resolved from the string table on every read, like the general and history rows.
            TreeNodeKind.Worktree when isMainCheckout && title.Length == 0 => ProjectStrings.MainCheckoutDetached,
            _ => title,
        };
        set => Set(ref title, value);
    }

    /// <summary>
    /// True for the repository root checkout row, the first child of a project row. It is a worktree row in every other
    /// way (same expansion, row action, context menu and trailing "+"), so only the icon and the detached label differ.
    /// </summary>
    public bool IsMainCheckout
    {
        get => isMainCheckout;
        set => SetAndNotify(ref isMainCheckout, value, nameof(Title));
    }

    /// <summary>Archived sessions of a history row; the row title is formatted from it, so it follows the language too.</summary>
    public int HistoryCount
    {
        get => historyCount;
        set => SetAndNotify(ref historyCount, value, nameof(Title));
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
        set => SetAndNotify(ref subtitle, value, nameof(HasSubtitle));
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

    /// <summary>
    /// The glyph on the row's trailing "+": the plain plus where the button starts a conversation, the branch glyph on a
    /// project row where it starts a worktree. Two different actions, so one shared plus would hide the difference.
    /// </summary>
    public Geometry NewSessionGlyph
    {
        get => newSessionGlyph;
        set => Set(ref newSessionGlyph, value);
    }

    /// <summary>The trailing "+" tooltip, resolved from its key on read so it follows a language switch like every row label.</summary>
    public string NewSessionTooltip => Loc.Instance[newSessionTipKey];

    /// <summary>The key the trailing "+" tooltip is resolved from; the project row names a branch, every other row a session.</summary>
    public string NewSessionTipKey
    {
        get => newSessionTipKey;
        set => SetAndNotify(ref newSessionTipKey, value, nameof(NewSessionTooltip));
    }

    /// <summary>The trailing "+"; a project that is not a git repository has no "new workspace…" to offer (5.2).</summary>
    public bool HasNewSession => NewSessionCommand is not null && !IsPlainProject;

    public bool IsRunning
    {
        get => isRunning;
        set => SetAndNotify(ref isRunning, value, nameof(RunningText));
    }

    public bool IsActiveSession
    {
        get => isActiveSession;
        set => SetAndNotify(ref isActiveSession, value, nameof(IsActiveRow));
    }

    /// <summary>True for the agent row whose tab the bottom group is showing, so the tree and that group agree.</summary>
    public bool IsActiveAgent
    {
        get => isActiveAgent;
        set => SetAndNotify(ref isActiveAgent, value, nameof(IsActiveRow));
    }

    /// <summary>The row the main region (a session) or the bottom group (a sub-agent) is currently showing.</summary>
    public bool IsActiveRow => isActiveSession || isActiveAgent;

    /// <summary>Agent id of an agent row, empty for every other row; the shell matches it against the open tabs.</summary>
    public string AgentId { get; init; } = string.Empty;

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
        OnPropertyChanged(nameof(NewSessionTooltip));
        OnPropertyChanged(nameof(ForkLabel));
        OnPropertyChanged(nameof(ForkHeader));
        OnPropertyChanged(nameof(MergeBackHeader));
        OnPropertyChanged(nameof(SyncHeader));
        OnPropertyChanged(nameof(AbortMergeHeader));
        OnPropertyChanged(nameof(RowMarkText));
        OnPropertyChanged(nameof(RemoveHeader));
        OnPropertyChanged(nameof(RestoreHeader));
        OnPropertyChanged(nameof(PurgeHeader));
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
                OnPropertyChanged(nameof(HasStatusGlyph));
                OnPropertyChanged(nameof(ShowsStatusDot));
                OnPropertyChanged(nameof(ShowsStatusCheck));
            }
        }
    }

    public bool HasStatusText => statusText.Length > 0;

    /// <summary>
    /// An agent row's state is drawn, not spelled: a dot while it is queued, running or ended badly, a check once it finished
    /// fine. The text stays as the row's tooltip, so nothing is lost for a reader who needs the words.
    /// </summary>
    public bool HasStatusGlyph => IsAgent && HasStatusText;

    public bool ShowsStatusDot => HasStatusGlyph && agentState != AgentState.Completed;

    public bool ShowsStatusCheck => HasStatusGlyph && agentState == AgentState.Completed;

    /// <summary>The neutral dot: an agent whose state the transcript did not state, drawn like a queued one but muted.</summary>
    public bool IsAgentEndedGlyph => agentState == AgentState.Ended;

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
            OnPropertyChanged(nameof(IsAgentEndedGlyph));
            OnPropertyChanged(nameof(ShowsStatusDot));
            OnPropertyChanged(nameof(ShowsStatusCheck));
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

    /// <summary>"Parallel fork…" (a workspace row) or "parallel fork (inherit this session)…" (an active session row, 6.5).</summary>
    public ICommand? ForkCommand { get; private set; }

    /// <summary>"Merge back into &lt;source&gt;…" on a fork workspace row (6.6).</summary>
    public ICommand? MergeBackCommand { get; private set; }

    /// <summary>"Sync from &lt;source&gt;…" on a fork workspace row (6.6).</summary>
    public ICommand? SyncCommand { get; private set; }

    /// <summary>"Abort the merge" on a workspace row in the merge-conflict state (6.6 step 5).</summary>
    public ICommand? AbortMergeCommand { get; private set; }

    public bool HasFork => ForkCommand is not null;

    public bool HasMergeBack => MergeBackCommand is not null;

    public bool HasSync => SyncCommand is not null;

    /// <summary>The abort entry shows only while the row's workspace is in a merge conflict (the in-memory snapshot).</summary>
    public bool HasAbortMerge => AbortMergeCommand is not null && isConflicted;

    /// <summary>A repository workspace row's directory, the key of its in-memory states; null for every other row.</summary>
    public string? WorkspaceDirectory { get; private set; }

    /// <summary>The merge-conflict flag of <see cref="WorkspaceDirectory"/>, copied from the snapshot (section 2).</summary>
    public bool IsConflicted
    {
        get => isConflicted;
        set => SetAndNotify(ref isConflicted, value, nameof(HasAbortMerge));
    }

    /// <summary>The fork entry's label, resolved on read: a session row inherits the session, a workspace row does not.</summary>
    public string ForkHeader => Kind == TreeNodeKind.Session ? ForkStrings.MenuForkInherit : ForkStrings.MenuFork;

    public string MergeBackHeader => forkOfBranch is null ? string.Empty : ForkStrings.MenuMergeBack(forkOfBranch);

    public string SyncHeader => forkOfBranch is null ? string.Empty : ForkStrings.MenuSync(forkOfBranch);

    public string AbortMergeHeader => ForkStrings.MenuAbort;

    /// <summary>
    /// The source branch of a fork workspace row (5.4 rule 6: the workspace's most recent session that records one,
    /// history included); null for every other row.
    /// </summary>
    public string? ForkOfBranch
    {
        get => forkOfBranch;
        set
        {
            if (Set(ref forkOfBranch, value))
            {
                OnPropertyChanged(nameof(ForkLabel));
                OnPropertyChanged(nameof(HasForkLabel));
                OnPropertyChanged(nameof(MergeBackHeader));
                OnPropertyChanged(nameof(SyncHeader));
            }
        }
    }

    /// <summary>A session row an automation created (a scratch run or an automation's own branch session).</summary>
    public bool IsAutomationSession
    {
        get => automationSession;
        set => Set(ref automationSession, value);
    }

    /// <summary>"⑂ forked from &lt;source&gt;" after a fork workspace row's title.</summary>
    public string ForkLabel => forkOfBranch is null ? string.Empty : ForkStrings.ForkedFrom(forkOfBranch);

    public bool HasForkLabel => forkOfBranch is not null;

    /// <summary>The row's state mark (recycled, directory missing, removed); <see cref="TreeRowMark.None"/> for most rows.</summary>
    public TreeRowMark RowMark
    {
        get => rowMark;
        set
        {
            if (Set(ref rowMark, value))
            {
                OnPropertyChanged(nameof(RowMarkText));
                OnPropertyChanged(nameof(HasRowMark));
                OnPropertyChanged(nameof(IsRecycled));
            }
        }
    }

    public string RowMarkText => rowMark switch
    {
        TreeRowMark.Recycled => RemovalStrings.RowRecycled,
        TreeRowMark.Missing => RemovalStrings.RowMissing,
        TreeRowMark.DirectoryRecycled => RemovalStrings.RowDirectoryRecycled,
        TreeRowMark.Removed => RemovalStrings.RowRemoved,
        TreeRowMark.Prunable => RemovalStrings.RowPrunable,
        TreeRowMark.Unregistered => RemovalStrings.RowUnregistered,
        TreeRowMark.Unverified => RemovalStrings.RowUnverified,
        _ => string.Empty,
    };

    public bool HasRowMark => rowMark != TreeRowMark.None;

    /// <summary>A recycled workspace row: drawn greyed (5.1).</summary>
    public bool IsRecycled => rowMark == TreeRowMark.Recycled;

    /// <summary>"Restore…" on a removed session row, "restore workspace…" on a recycled workspace row (7.4, 7.5).</summary>
    public ICommand? RestoreCommand { get; private set; }

    /// <summary>"Delete permanently…" on a removed session row, "delete all sessions permanently…" on a recycled workspace row (7.5).</summary>
    public ICommand? PurgeCommand { get; private set; }

    public bool HasRemove => RemoveCommand is not null;

    public bool HasRestore => RestoreCommand is not null;

    public bool HasPurge => PurgeCommand is not null;

    /// <summary>The remove entry's label by row: close the project, remove the workspace, remove the session (7.2, 7.3, 7.7).</summary>
    public string RemoveHeader => Kind switch
    {
        TreeNodeKind.Project => RemovalStrings.MenuCloseProject,
        TreeNodeKind.Worktree => RemovalStrings.MenuRemoveWorkspace,
        _ => RemovalStrings.MenuRemoveSession,
    };

    public string RestoreHeader => Kind == TreeNodeKind.Worktree ? RemovalStrings.MenuRestoreWorkspace : RemovalStrings.MenuRestore;

    public string PurgeHeader => Kind == TreeNodeKind.Worktree ? RemovalStrings.MenuPurgeAll : RemovalStrings.MenuPurge;

    /// <summary>Replaces the restore / purge entries of a reused node; null hides the entry.</summary>
    public void SetRecoveryCommands(ICommand? restore, ICommand? purge)
    {
        RestoreCommand = restore;
        PurgeCommand = purge;
        OnPropertyChanged(nameof(RestoreCommand));
        OnPropertyChanged(nameof(PurgeCommand));
        OnPropertyChanged(nameof(HasRestore));
        OnPropertyChanged(nameof(HasPurge));
    }

    /// <summary>Replaces the lifecycle commands (6.5, 6.6) of a reused node; null hides the entry.</summary>
    public void SetLifecycleCommands(string? workspaceDirectory, ICommand? fork, ICommand? mergeBack, ICommand? sync, ICommand? abortMerge)
    {
        WorkspaceDirectory = workspaceDirectory;
        ForkCommand = fork;
        MergeBackCommand = mergeBack;
        SyncCommand = sync;
        AbortMergeCommand = abortMerge;
        OnPropertyChanged(nameof(ForkCommand));
        OnPropertyChanged(nameof(MergeBackCommand));
        OnPropertyChanged(nameof(SyncCommand));
        OnPropertyChanged(nameof(AbortMergeCommand));
        OnPropertyChanged(nameof(HasFork));
        OnPropertyChanged(nameof(HasMergeBack));
        OnPropertyChanged(nameof(HasSync));
        OnPropertyChanged(nameof(HasAbortMerge));
    }

    /// <summary>Re-asks every command of the row whether it can run, after an in-memory lifecycle state changed.</summary>
    public void NotifyCommandStates()
    {
        foreach (var command in new[] { OpenCommand, NewSessionCommand, ArchiveCommand, RenameCommand, RevealCommand, RemoveCommand, ForkCommand, MergeBackCommand, SyncCommand, AbortMergeCommand, RestoreCommand, PurgeCommand, LinkWorkspaceCommand, ReplaceSessionCommand, RenameBranchCommand, ContinueCommand })
        {
            (command as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

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
        OnPropertyChanged(nameof(HasNewSession));
        OnPropertyChanged(nameof(OpenCommand));
        OnPropertyChanged(nameof(NewSessionCommand));
        OnPropertyChanged(nameof(ArchiveCommand));
        OnPropertyChanged(nameof(RenameCommand));
        OnPropertyChanged(nameof(RevealCommand));
        OnPropertyChanged(nameof(RemoveCommand));
        OnPropertyChanged(nameof(HasRemove));
    }

    public void NotifyChildrenChanged() => OnPropertyChanged(nameof(HasToggle));

    /// <summary>
    /// What a click anywhere on the row does: a row that opens something (a session) opens it, any other row with
    /// children expands or collapses, and a childless project/worktree/general row starts a new session. Returns false
    /// when the row has no action.
    /// </summary>
    public bool Activate()
    {
        if (OpenCommand is { } open)
        {
            if (open.CanExecute(null))
            {
                open.Execute(null);
            }

            return true;
        }

        if (HasToggle)
        {
            ToggleCommand.Execute(null);
            return true;
        }

        // A leaf container row (an empty project, worktree or the general group) starts a session, its row action.
        if (NewSessionCommand is { } create)
        {
            if (create.CanExecute(null))
            {
                create.Execute(null);
            }

            return true;
        }

        return false;
    }
}

/// <summary>The result of one tree plan: its rows, plus the child-session lookup later incremental refreshes reuse.</summary>
internal sealed record PlannedTree(
    List<TreeNodePlan> Plans,
    ILookup<string, SessionInfo> StoredChildren);

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

    /// <summary>Agent id of an agent row, empty for every other row; a click on the row opens that agent's tab.</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Session an agent row belongs to, empty for every other row; the row needs it to find its agent again.</summary>
    public string AgentSessionId { get; init; } = string.Empty;

    /// <summary>True for an archived session row, whose subtitle is the archived label instead of the last-active stamp.</summary>
    public bool IsArchived { get; init; }

    /// <summary>
    /// True for the repository root checkout row: it reuses <see cref="TreeNodeKind.Worktree"/> so expansion, activation
    /// and the row's tools behave like any worktree, and only the icon and the detached label name the difference.
    /// </summary>
    public bool IsMainCheckout { get; init; }

    /// <summary>A workspace row's fork source branch (5.4 rule 6); null when the workspace is not a fork.</summary>
    public string? ForkOfBranch { get; init; }

    /// <summary>The fork's start commit, recorded with <see cref="ForkOfBranch"/>.</summary>
    public string? ForkBaseSha { get; init; }

    /// <summary>The workspace's active session, if any: the source of a fork from the workspace row.</summary>
    public SessionInfo? ActiveSession { get; init; }

    /// <summary>The main checkout's HEAD kind; a detached or unborn HEAD cannot be forked (5.3).</summary>
    public GitHeadKind? HeadKind { get; init; }

    /// <summary>The row's state mark, decided while planning (the directory probes run on the planning thread, never on a menu).</summary>
    public TreeRowMark RowMark { get; init; }

    /// <summary>A linked workspace's checkout state as the refresh read it; null when it was not read (5.3).</summary>
    public WorkspaceGitState? GitState { get; init; }

    /// <summary>The workspace has the repository's default branch checked out: no "rename branch" (5.4 rule 2).</summary>
    public bool IsDefaultBranch { get; init; }

    /// <summary>An archived session whose directory is gone: "continue" is disabled (6.3). Probed while planning.</summary>
    public bool DirectoryMissing { get; init; }

    /// <summary>Git's reason for a <see cref="TreeRowMark.Prunable"/> workspace row (or its archived sessions), from the worktree list.</summary>
    public string? PrunableReason { get; init; }

    /// <summary>
    /// An archived session's workspace mark when git no longer treats it as a worktree (<see cref="TreeRowMark.Prunable"/>
    /// or <see cref="TreeRowMark.Unregistered"/>): "continue" is disabled with git's reason. None otherwise.
    /// </summary>
    public TreeRowMark WorkspaceMark { get; init; }

    /// <summary>
    /// The workspace's directory exists but git no longer treats it as a worktree (<see cref="TreeRowMark.Prunable"/> or
    /// <see cref="TreeRowMark.Unregistered"/>): what runs git in it is disabled, as for a missing directory.
    /// </summary>
    public bool IsStaleWorktree => RowMark is TreeRowMark.Prunable or TreeRowMark.Unregistered;
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
internal sealed partial class ProjectTreeViewModel : ObservableObject, IDisposable
{
    private readonly IProjectRegistry projects;
    private readonly ISessionCatalog catalog;
    private readonly WorktreeService worktrees;
    private readonly Func<string, CancellationToken, Task<GitHead>> readHead;
    private readonly Func<string, CancellationToken, Task> openSession;
    private readonly Func<Func<CancellationToken, Task<SessionInfo>>, Task<bool>> createConfirmed;
    private readonly Func<string, string, Task<string?>> promptText;
    private readonly Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> pickNewBranch;
    private readonly Func<string, Task> reveal;
    private readonly Func<CancellationToken, Task> addProject;
    private readonly TreeScope scope;
    private readonly FlatTree<TreeNodeViewModel> tree = new();
    private bool busy;
    private bool refreshPending;
    private LocText status;
    private string activeSessionId = string.Empty;
    private ILookup<string, SessionInfo> storedChildren = Array.Empty<SessionInfo>().ToLookup(static session => session.ParentSessionId ?? string.Empty);
    private string activeAgentId = string.Empty;

    /// <summary>Upper bound of the concurrent HEAD reads of one refresh: a refresh must not fan out over every project at once.</summary>
    private const int MaxConcurrentHeadReads = 4;

    /// <summary>The HEAD reads of a refresh that has none to do: no projects, or a panel that plans no project rows.</summary>
    private static readonly IReadOnlyDictionary<string, GitHead> EmptyHeads =
        new Dictionary<string, GitHead>(StringComparer.OrdinalIgnoreCase);

    public ProjectTreeViewModel(
        IProjectRegistry projects,
        ISessionCatalog catalog,
        WorktreeService worktrees,
        Func<string, CancellationToken, Task<GitHead>> readHead,
        Func<string, CancellationToken, Task> openSession,
        Func<Func<CancellationToken, Task<SessionInfo>>, Task<bool>> createConfirmed,
        Func<string, string, Task<string?>> promptText,
        Func<NewBranchRequest, CancellationToken, Task<NewBranchChoice?>> pickNewBranch,
        Func<string, Task> reveal,
        Func<CancellationToken, Task> addProject,
        TreeScope scope = TreeScope.Both)
    {
        this.projects = projects;
        this.catalog = catalog;
        this.worktrees = worktrees;
        this.readHead = readHead;
        this.openSession = openSession;
        this.createConfirmed = createConfirmed;
        this.promptText = promptText;
        this.pickNewBranch = pickNewBranch;
        this.reveal = reveal;
        this.addProject = addProject;
        this.scope = scope;
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

    /// <summary>
    /// Opens the session of an agent row and its tab in the main region's bottom group, by (session id, agent id). Set by the
    /// shell, which owns the session runtimes; a click on an agent row is the only way in from the tree, so without it the
    /// row does nothing.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? OpenSubAgent { get; set; }

    /// <summary>Called after a session was archived, so the shell can drop its runtime when nothing else uses it.</summary>
    public Action<string>? SessionArchived { get; set; }
    public Func<string, Task<bool>>? ArchiveGuard { get; set; }

    /// <summary>Runs the parallel fork dialog (6.5) for a source workspace with the given default context; set by the shell.</summary>
    public Func<ForkSource, HandoverContextMode, CancellationToken, Task>? ForkWorkspace { get; set; }

    /// <summary>Runs the merge-back / sync dialog (6.6) of a fork workspace; set by the shell.</summary>
    public Func<ForkRelation, MergeDirection, CancellationToken, Task>? MergeFork { get; set; }

    /// <summary>Aborts the merge in a conflicted workspace (6.6 step 5); set by the shell.</summary>
    public Func<string, CancellationToken, Task>? AbortMerge { get; set; }

    /// <summary>
    /// In-memory workspace state by directory (section 2): an operation under way or a merge conflict. Read when a menu
    /// asks whether a command can run, never a git or database call.
    /// </summary>
    public Func<string, bool>? IsWorkspaceBlocked { get; set; }

    /// <summary>In-memory merge-conflict flag by directory, refreshed before every tree rebuild.</summary>
    public Func<string, bool>? IsWorkspaceConflicted { get; set; }

    /// <summary>True when the session is busy or handing over (its open tab's flags); in memory only.</summary>
    public Func<string, bool>? IsSessionBusy { get; set; }

    /// <summary>"Remove session…" (7.2): the session row's snapshot and its project; the shell asks, re-checks and acts.</summary>
    public Func<SessionInfo, ProjectInfo, CancellationToken, Task>? RemoveSession { get; set; }

    /// <summary>"Remove workspace…" (7.3) of a repository workspace row other than the root checkout.</summary>
    public Func<WorkspaceTarget, CancellationToken, Task>? RemoveWorkspace { get; set; }

    /// <summary>"Restore…" of a removed session row (7.4).</summary>
    public Func<SessionInfo, ProjectInfo, CancellationToken, Task>? RestoreSession { get; set; }

    /// <summary>"Restore workspace…" of a recycled workspace row (7.5): rebuilds the directory only.</summary>
    public Func<WorkspaceTarget, CancellationToken, Task>? RestoreWorkspace { get; set; }

    /// <summary>"Delete permanently…" of a removed session row (7.5).</summary>
    public Func<SessionInfo, CancellationToken, Task>? PurgeSession { get; set; }

    /// <summary>"Delete all sessions permanently…" of a recycled workspace row (7.5).</summary>
    public Func<WorkspaceTarget, CancellationToken, Task>? PurgeAll { get; set; }

    /// <summary>"Close project…" (7.7): the soft close; the hard <see cref="IProjectRegistry.RemoveAsync"/> is never offered.</summary>
    public Func<ProjectInfo, CancellationToken, Task>? CloseProject { get; set; }

    /// <summary>
    /// Re-reads the in-memory lifecycle states for every row: the conflict flag that shows "abort the merge", and whether
    /// each command can run. Called after the shell changed a state and after every conflict snapshot.
    /// </summary>
    public void NotifyCommandStates()
    {
        foreach (var node in Enumerate(Sections))
        {
            if (node.WorkspaceDirectory is { } directory)
            {
                node.IsConflicted = IsWorkspaceConflicted?.Invoke(directory) == true;
            }

            node.NotifyCommandStates();
        }
    }

    /// <summary>The directories of the repository workspace rows now in the tree, for the merge-conflict snapshot.</summary>
    public IReadOnlyList<string> WorkspaceDirectories() =>
        Enumerate(Sections).Select(static node => node.WorkspaceDirectory).OfType<string>().ToArray();

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

            var snapshot = provider(node.SessionId);
            Merge(
                node.Children,
                snapshot.Count > 0
                    ? PlanSessionAgents(snapshot, node.SessionId)
                    : PlanStoredAgents(storedChildren, node.SessionId, node.SessionId, RunningSessionIds()),
                node.Depth + 1);
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

    public void SetActiveSession(string? sessionId)
    {
        activeSessionId = sessionId ?? string.Empty;
        foreach (var node in Enumerate(Sections))
        {
            node.IsActiveSession = node.Kind == TreeNodeKind.Session
                && string.Equals(node.SessionId, activeSessionId, StringComparison.Ordinal);
        }

        ActiveRowChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the agent row whose tab the bottom group shows; null clears the mark.</summary>
    public void SetActiveAgent(string? agentId)
    {
        activeAgentId = agentId ?? string.Empty;
        foreach (var node in Enumerate(Sections))
        {
            node.IsActiveAgent = node.IsAgent
                && string.Equals(node.AgentId, activeAgentId, StringComparison.Ordinal);
        }

        ActiveRowChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the row the shell is showing changes (another session tab, another sub-agent tab). The view drops the
    /// mouse selection then: the highlight follows what is on screen, and a row picked earlier must not keep it too.
    /// </summary>
    public event EventHandler? ActiveRowChanged;

    /// <summary>Archives a session: it leaves the worktree list and shows up under "历史".</summary>
    public async Task ArchiveAsync(string sessionId, CancellationToken ct)
    {
        if (ArchiveGuard is { } guard && !await guard(sessionId).ConfigureAwait(true))
        {
            return;
        }

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

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            var all = await BackgroundRead.RunAsync(token => projects.ListAsync(token), ct).ConfigureAwait(true);
            var sessions = await BackgroundRead.RunAsync(QueryAllSessionsAsync, ct).ConfigureAwait(true);
            var roots = sessions.Where(static session => string.IsNullOrEmpty(session.ParentSessionId)).ToArray();

            // The session rows of the tree come from collections the dispatcher owns, so both the running set and the
            // agent trees are snapshotted here, before the planning leaves the UI thread.
            var running = RunningSessionIds();
            var agents = SnapshotAgentTrees(roots);
            // The root HEADs and the menus' workspace reads are independent: they run side by side, not one after the other.
            var headsRead = ReadProjectHeadsAsync(all, ct);
            var gitRead = ReadMenuGitStateAsync(all, roots, ct);
            await Task.WhenAll(headsRead, gitRead).ConfigureAwait(true);
            var heads = await headsRead.ConfigureAwait(true);
            var git = await gitRead.ConfigureAwait(true);
            plainProjects = PlainProjectsOf(all, heads);
            var planned = await Task.Run(() => Plan(all, roots, sessions, running, agents, heads, git), ct).ConfigureAwait(true);
            var plans = planned.Plans;
            storedChildren = planned.StoredChildren;
            Merge(Sections, plans);
            RebuildRows();
            GeneralSection = Sections.FirstOrDefault(static node => node.Kind == TreeNodeKind.General);
            SetStatus(scope != TreeScope.General && all.Count <= 1 ? ProjectStrings.EmptyProjects : default);
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

    /// <summary>
    /// The HEAD of every registered repository, read once per refresh so no row (and no render) starts a git process. The
    /// reads run on the thread pool, at most <see cref="MaxConcurrentHeadReads"/> at a time; a repository whose read fails
    /// is planned as "not a repository" and simply gets no main-checkout row. The general-chats panel plans no project rows,
    /// so it reads nothing.
    /// </summary>
    private Task<IReadOnlyDictionary<string, GitHead>> ReadProjectHeadsAsync(IReadOnlyList<ProjectInfo> all, CancellationToken ct)
    {
        if (scope == TreeScope.General)
        {
            return Task.FromResult<IReadOnlyDictionary<string, GitHead>>(EmptyHeads);
        }

        var roots = new List<string>(all.Count);
        foreach (var project in all)
        {
            if (project.Kind == ProjectKind.Repository && project.RootPath.Length > 0)
            {
                roots.Add(project.RootPath);
            }
        }

        return roots.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<string, GitHead>>(EmptyHeads)
            : BackgroundRead.RunAsync(token => ReadHeadsAsync(roots, token), ct);
    }

    private async Task<IReadOnlyDictionary<string, GitHead>> ReadHeadsAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var heads = new ConcurrentDictionary<string, GitHead>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
                roots,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentHeadReads, CancellationToken = ct },
                async (root, token) =>
                {
                    GitHead head;
                    try
                    {
                        head = await readHead(root, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // One unreadable repository must not fail the whole refresh: it plans as "not a repository",
                        // which is exactly "no main-checkout row for this project".
                        Trace.WriteLine($"[tree] could not read the HEAD of '{root}': {exception.Message}");
                        head = GitHead.NotRepository;
                    }

                    heads[root] = head;
                })
            .ConfigureAwait(false);
        return heads;
    }

    /// <summary>
    /// Builds the whole tree as plain data, so the queries and the mapping stay off the dispatcher.
    /// <paramref name="sessions"/> is every session, roots and children: the rows come from the roots, the agent rows of each
    /// session from its child links (or from <paramref name="agents"/> when that session is loaded in this run).
    /// </summary>
    private PlannedTree Plan(
        IReadOnlyList<ProjectInfo> all,
        IReadOnlyList<SessionInfo> sessions,
        IReadOnlyList<SessionInfo> every,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents,
        IReadOnlyDictionary<string, GitHead> heads,
        MenuGitReads git)
    {
        var storedChildren = every
            .Where(static session => !string.IsNullOrEmpty(session.ParentSessionId))
            .ToLookup(static session => session.ParentSessionId!, StringComparer.Ordinal);
        var plans = new List<TreeNodePlan>(all.Count);
        foreach (var project in all.OrderBy(static p => p.Kind == ProjectKind.General ? 0 : 1).ThenBy(static p => p.SortOrder))
        {
            // A panel shows one of the two areas: the general workspace, or the registered projects.
            if (scope != TreeScope.Both && (project.Kind == ProjectKind.General) != (scope == TreeScope.General))
            {
                continue;
            }

            var projectSessions = sessions
                .Where(session => string.Equals(session.ProjectId, project.Id, StringComparison.Ordinal))
                .ToArray();

            var plan = project.Kind == ProjectKind.General
                ? PlanGeneral(project, projectSessions, storedChildren, running, agents)
                : PlanProject(project, projectSessions, storedChildren, running, agents, heads, git);

            // In its own panel the general area needs no heading row: the panel title already says "常规对话", so its
            // sessions and history are the panel's rows, not a group that repeats the title.
            if (scope == TreeScope.General && plan.Kind == TreeNodeKind.General)
            {
                plans.AddRange(plan.Children);
                continue;
            }

            plans.Add(plan);
        }

        return new PlannedTree(plans, storedChildren);
    }

    private static TreeNodePlan PlanGeneral(
        ProjectInfo project,
        IReadOnlyList<SessionInfo> sessions,
        ILookup<string, SessionInfo> storedChildren,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents)
    {
        var children = new List<TreeNodePlan>();
        foreach (var session in sessions.Where(static s => s.Status == SessionStatus.Active))
        {
            children.Add(PlanSession(session, storedChildren, running, agents, project));
        }

        var archived = HistoryOf(sessions);
        if (archived.Length > 0)
        {
            children.Add(PlanHistory(project, project.RootPath, archived, storedChildren, running, agents));
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
        ILookup<string, SessionInfo> storedChildren,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents,
        IReadOnlyDictionary<string, GitHead> heads,
        MenuGitReads git)
    {
        git.DefaultBranches.TryGetValue(project.RootPath, out var defaultBranch);
        var groups = sessions
            .Where(session => !string.IsNullOrEmpty(session.WorktreePath) || session.ParentSessionId is null)
            .GroupBy(session => session.WorktreePath ?? project.RootPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(static s => s.LastActiveAt))
            .ToArray();

        // The repository root checkout leads the children and is always there, even with no session in it. Its sessions
        // are the ones the grouping keyed on the root path, so they belong to this row instead of a second row for the
        // same directory; without a readable HEAD (not a repository) there is no such row and the grouping stays as it was.
        GitHead? mainHead = heads.TryGetValue(project.RootPath, out var found) && found.Kind != GitHeadKind.NotRepository
            ? found
            : null;
        var children = new List<TreeNodePlan>(groups.Length + 1);
        if (mainHead is not null)
        {
            SessionInfo[] rootSessions = [];
            foreach (var group in groups)
            {
                if (string.Equals(group.Key, project.RootPath, StringComparison.OrdinalIgnoreCase))
                {
                    rootSessions = group.ToArray();
                    break;
                }
            }

            children.Add(PlanMainCheckout(project, mainHead, rootSessions, storedChildren, running, agents) with
            {
                IsDefaultBranch = IsDefaultBranchName(mainHead.Branch, defaultBranch),
            });
        }

        foreach (var group in groups)
        {
            if (mainHead is not null && string.Equals(group.Key, project.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var branch = group.First().Branch;

            // A Forge-owned workspace whose directory is gone: recycled when every session of it was removed (7.3 step
            // 5), "directory missing" while it still has sessions (5.3). Probed here, on the planning thread. A directory
            // that is there is then checked against the repository's worktree list read at refresh (prunable, not listed,
            // or unknown when the list could not be read).
            var mark = TreeRowMark.None;
            string? prunableReason = null;
            if (!string.Equals(group.Key, project.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(group.Key))
                {
                    mark = group.Any(static s => s.ParentSessionId is null && s.Status != SessionStatus.Deleted)
                        ? TreeRowMark.Missing
                        : TreeRowMark.Recycled;
                }
                else
                {
                    (mark, prunableReason) = WorktreeMark(git, project.RootPath, group.Key);
                }
            }

            var worktreeChildren = new List<TreeNodePlan>();
            foreach (var session in group.Where(static s => s.Status == SessionStatus.Active))
            {
                worktreeChildren.Add(PlanSession(session, storedChildren, running, agents, project));
            }

            var archived = HistoryOf(group);
            if (archived.Length > 0)
            {
                var history = PlanHistory(project, group.Key, archived, storedChildren, running, agents);
                if (mark is TreeRowMark.Prunable or TreeRowMark.Unregistered)
                {
                    // An archived session continues in this workspace: a stale worktree disables "continue" like a missing one.
                    history = history with
                    {
                        Children = history.Children
                            .Select(child => child.Session is { Status: SessionStatus.Archived }
                                ? child with { WorkspaceMark = mark, PrunableReason = prunableReason }
                                : child)
                            .ToArray(),
                    };
                }

                worktreeChildren.Add(history);
            }

            var fork = ForkOf(group);
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
                worktreeChildren)
            {
                ForkOfBranch = fork?.ForkOfBranch,
                ForkBaseSha = fork?.ForkBaseSha,
                ActiveSession = ActiveRootOf(group),
                RowMark = mark,
                PrunableReason = prunableReason,
                GitState = git.States.TryGetValue(group.Key, out var state) ? state : null,
                IsDefaultBranch = IsDefaultBranchName(branch, defaultBranch),
            });
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

    /// <summary>
    /// The repository root checkout row: a worktree row whose label is the real HEAD branch of the root directory, or the
    /// detached label (resolved in the current language by the row) when HEAD points at a commit instead of a branch.
    /// </summary>
    private static TreeNodePlan PlanMainCheckout(
        ProjectInfo project,
        GitHead head,
        IReadOnlyList<SessionInfo> sessions,
        ILookup<string, SessionInfo> storedChildren,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents)
    {
        var children = new List<TreeNodePlan>();
        foreach (var session in sessions.Where(static s => s.Status == SessionStatus.Active))
        {
            children.Add(PlanSession(session, storedChildren, running, agents, project));
        }

        var archived = HistoryOf(sessions);
        if (archived.Length > 0)
        {
            children.Add(PlanHistory(project, project.RootPath, archived, storedChildren, running, agents));
        }

        var fork = ForkOf(sessions);
        return new TreeNodePlan(
            TreeNodeKind.Worktree,
            "main:" + project.Id,
            head.Kind == GitHeadKind.Detached ? string.Empty : head.Branch ?? string.Empty,
            project.RootPath,
            false,
            project,
            null,
            project.RootPath,
            head.Branch,
            false,
            children)
        {
            IsMainCheckout = true,
            HeadKind = head.Kind,
            ForkOfBranch = fork?.ForkOfBranch,
            ForkBaseSha = fork?.ForkBaseSha,
            ActiveSession = ActiveRootOf(sessions),
        };
    }

    /// <summary>The workspace's fork relation: its most recent session that records a fork source, history included (5.4 rule 6).</summary>
    private static SessionInfo? ForkOf(IEnumerable<SessionInfo> sessions) =>
        sessions
            .Where(static s => s.ParentSessionId is null && !string.IsNullOrEmpty(s.ForkOfBranch))
            .OrderByDescending(static s => s.CreatedAt)
            .FirstOrDefault();

    private static SessionInfo? ActiveRootOf(IEnumerable<SessionInfo> sessions) =>
        sessions.FirstOrDefault(static s => s.ParentSessionId is null && s.Status == SessionStatus.Active);

    /// <summary>The "history (n)" sessions of a group: the archived ones, then the removed (Deleted) ones (5.1).</summary>
    private static SessionInfo[] HistoryOf(IEnumerable<SessionInfo> sessions) =>
        sessions
            .Where(static s => s.Status is SessionStatus.Archived or SessionStatus.Deleted)
            .OrderBy(static s => s.Status == SessionStatus.Deleted ? 1 : 0)
            .ToArray();

    private static TreeNodePlan PlanHistory(
        ProjectInfo project,
        string worktreePath,
        IReadOnlyList<SessionInfo> sessions,
        ILookup<string, SessionInfo> storedChildren,
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
            sessions.Select(session => PlanSession(session, storedChildren, running, agents, project)).ToArray())
        {
            HistoryCount = sessions.Count,
        };

    /// <param name="project">The session's project: the source of its fork entry (a repository) and of its removal entries.</param>
    private static TreeNodePlan PlanSession(
        SessionInfo session,
        ILookup<string, SessionInfo> storedChildren,
        HashSet<string> running,
        IReadOnlyDictionary<string, IReadOnlyList<AgentNodeSnapshot>> agents,
        ProjectInfo? project = null) =>
        new(
            TreeNodeKind.Session,
            "session:" + session.Id,
            session.Title,
            session.Status == SessionStatus.Active ? TextFormat.ShortStamp(session.LastActiveAt) : string.Empty,
            running.Contains(session.Id),
            project,
            session,
            session.WorktreePath ?? session.WorkspacePath,
            session.Branch,
            false,
            agents.TryGetValue(session.Id, out var snapshot) && snapshot.Count > 0
                ? PlanSessionAgents(snapshot, session.Id)
                : PlanStoredAgents(storedChildren, session.Id, session.Id, running))
        {
            IsArchived = session.Status == SessionStatus.Archived,
            // A removed session says where its directory went: gone (recycled) or still there (kept, shared, rebuilt).
            RowMark = session.Status != SessionStatus.Deleted
                ? TreeRowMark.None
                : Directory.Exists(session.WorktreePath ?? session.WorkspacePath) ? TreeRowMark.Removed : TreeRowMark.DirectoryRecycled,
            DirectoryMissing = session.Status == SessionStatus.Archived
                               && session.ParentSessionId is null
                               && !Directory.Exists(session.WorktreePath ?? session.WorkspacePath),
        };

    /// <summary>
    /// The rows under a session that is not loaded in this run: its sub-agent sessions come straight from the store, by the
    /// parent link, so a restarted window still shows the agent rows (and the chevron that expands them). Their kind picks the
    /// icon; the run state lives in the transcript, which is not read here, so these rows carry no state mark until the session
    /// is opened and the live roster replaces them.
    /// </summary>
    private static List<TreeNodePlan> PlanStoredAgents(
        ILookup<string, SessionInfo> children,
        string parentSessionId,
        string rootSessionId,
        HashSet<string> running)
    {
        var plans = new List<TreeNodePlan>();
        foreach (var child in children[parentSessionId])
        {
            plans.Add(new TreeNodePlan(
                TreeNodeKind.SubAgent,
                "agent:" + child.Id,
                string.IsNullOrWhiteSpace(child.Task) ? child.Title : child.Task!,
                string.Empty,
                running.Contains(child.Id),
                null,
                null,
                child.WorktreePath ?? child.WorkspacePath,
                child.Branch,
                false,
                PlanStoredAgents(children, child.Id, rootSessionId, running))
            {
                AgentKind = child.AgentKind ?? string.Empty,
                AgentId = child.Id,
                AgentSessionId = rootSessionId,
            });
        }

        return plans;
    }

    /// <summary>
    /// The rows under a session: the main agent is the session itself, so its sub-agents are lifted into the session row
    /// instead of hanging under a second row that repeated the same conversation and needed a second chevron to open.
    /// </summary>
    internal static List<TreeNodePlan> PlanSessionAgents(IReadOnlyList<AgentNodeSnapshot> snapshot, string sessionId)
    {
        var plans = PlanAgents(snapshot, sessionId);
        if (plans.Count == 0)
        {
            return plans;
        }

        var lifted = new List<TreeNodePlan>(plans.Count);
        foreach (var plan in plans)
        {
            if (plan.Kind == TreeNodeKind.MainAgent)
            {
                lifted.AddRange(plan.Children);
            }
            else
            {
                lifted.Add(plan);
            }
        }

        return lifted;
    }

    /// <summary>Nests a session's agents by the parent link into 子 Agent → 孙 Agent rows.</summary>
    internal static List<TreeNodePlan> PlanAgents(IReadOnlyList<AgentNodeSnapshot> snapshot, string sessionId)
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
            plans.Add(PlanAgent(agent, byParent, sessionId));
        }

        foreach (var agent in snapshot)
        {
            if (!agent.IsMain && (agent.ParentAgentId is not { Length: > 0 } parent || snapshot.All(a => !string.Equals(a.AgentId, parent, StringComparison.Ordinal))))
            {
                plans.Add(PlanAgent(agent, byParent, sessionId));
            }
        }

        return plans;
    }

    private static TreeNodePlan PlanAgent(
        AgentNodeSnapshot agent,
        Dictionary<string, List<AgentNodeSnapshot>> byParent,
        string sessionId)
    {
        var children = new List<TreeNodePlan>();
        if (byParent.TryGetValue(agent.AgentId, out var nested))
        {
            foreach (var child in nested)
            {
                children.Add(PlanAgent(child, byParent, sessionId));
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
            AgentId = agent.AgentId,
            AgentSessionId = sessionId,
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
            AgentId = plan.AgentId,
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
        TreeNodeKind.Worktree when plan.IsMainCheckout => TreeIcons.MainCheckout,
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
        node.IsMainCheckout = plan.IsMainCheckout;
        node.IsAutomationSession = plan.Kind == TreeNodeKind.Session && plan.Session?.AutomationId is not null;
        node.ForkOfBranch = plan.Kind == TreeNodeKind.Worktree && plan.RowMark != TreeRowMark.Recycled ? plan.ForkOfBranch : null;
        node.RowMark = plan.RowMark;
        node.StatusText = plan.StatusText;
        node.IsActiveSession = plan.Kind == TreeNodeKind.Session
            && string.Equals(node.SessionId, activeSessionId, StringComparison.Ordinal);
        node.IsActiveAgent = node.IsAgent && string.Equals(plan.AgentId, activeAgentId, StringComparison.Ordinal);
        node.Icon = IconOf(plan);
        // A project's "+" starts a worktree on a new branch; every other row's "+" only starts a conversation. The glyph and
        // the tooltip name that difference, so the two buttons are not read as the same action.
        node.NewSessionGlyph = plan.Kind == TreeNodeKind.Project ? TreeIcons.NewBranch : TreeIcons.Plus;
        node.NewSessionTipKey = plan.Kind == TreeNodeKind.Project
            ? ProjectStrings.NewWorkspaceKey
            : ProjectStrings.NewSessionTooltipKey;
        switch (plan.Kind)
        {
            case TreeNodeKind.General:
                node.SetCommands(
                    open: null,
                    newSession: GeneralSessionCommand,
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
                    remove: new AsyncRelayCommand(
                        ct => CloseProject?.Invoke(project, ct) ?? Task.CompletedTask,
                        () => CloseProject is not null,
                        OnFailed));
                break;
            case TreeNodeKind.Worktree when plan.RowMark == TreeRowMark.Recycled:
                // A recycled workspace row (5.2): restore the directory, or purge its removed sessions; nothing else.
                node.SetCommands(open: null, newSession: null, archive: null, rename: null, reveal: null, remove: null);
                break;
            case TreeNodeKind.Worktree when plan.Project is { } project:
                var directoryMissing = plan.RowMark == TreeRowMark.Missing;
                var stale = plan.IsStaleWorktree;
                node.SetCommands(
                    open: null,
                    newSession: new AsyncRelayCommand(
                        ct => CreateSessionInWorktreeAsync(project, plan.WorktreePath, plan.Branch, ct),
                        () => !busy && !directoryMissing && !stale && !Blocked(plan.WorktreePath),
                        OnFailed),
                    archive: null,
                    rename: null,
                    reveal: new AsyncRelayCommand(ct => reveal(plan.WorktreePath), () => !directoryMissing, OnFailed),
                    remove: RemoveWorkspaceCommand(plan, project));
                break;
            case TreeNodeKind.Session when plan.Session is { Status: SessionStatus.Deleted } session:
                // A removed session row (5.2): read-only view, restore, purge; no rename (on purpose) and no archive.
                node.SetCommands(
                    open: new AsyncRelayCommand(ct => OpenSessionAsync(session, ct), () => true, OnFailed),
                    newSession: null,
                    archive: null,
                    rename: null,
                    reveal: null,
                    remove: null);
                break;
            case TreeNodeKind.Session when plan.Session is { } session:
                node.SetCommands(
                    open: new AsyncRelayCommand(ct => OpenSessionAsync(session, ct), () => true, OnFailed),
                    newSession: null,
                    archive: new AsyncRelayCommand(
                        ct => ArchiveAsync(session.Id, ct),
                        () => session.Status == SessionStatus.Active && !Blocked(DirectoryOf(session)),
                        OnFailed),
                    rename: new AsyncRelayCommand(ct => RenameSessionAsync(session, ct), () => !Blocked(DirectoryOf(session)), OnFailed),
                    reveal: new AsyncRelayCommand(ct => reveal(session.WorkspacePath), () => true, OnFailed),
                    remove: plan.Project is { } owner && session.ParentSessionId is null
                        ? new AsyncRelayCommand(
                            ct => RemoveSession?.Invoke(session, owner, ct) ?? Task.CompletedTask,
                            () => RemoveSession is not null && !Blocked(DirectoryOf(session)) && IsSessionBusy?.Invoke(session.Id) != true,
                            OnFailed)
                        : null);
                break;
            case TreeNodeKind.MainAgent or TreeNodeKind.SubAgent when plan.AgentId.Length > 0:
                var agentId = plan.AgentId;
                var agentSessionId = plan.AgentSessionId;
                node.SetCommands(
                    open: new AsyncRelayCommand(
                        ct => OpenSubAgent?.Invoke(agentSessionId, agentId, ct) ?? Task.CompletedTask,
                        () => true,
                        OnFailed),
                    newSession: null,
                    archive: null,
                    rename: null,
                    reveal: null,
                    remove: null);
                break;
            default:
                node.SetCommands(open: null, newSession: null, archive: null, rename: null, reveal: null, remove: null);
                break;
        }

        ApplyLifecycleCommands(node, plan);
        ApplyRecoveryCommands(node, plan);
        ApplyMenuCommands(node, plan);
    }

    /// <summary>
    /// "Remove workspace…" of a Forge-owned repository workspace row (7.3); the root checkout (and a project root shown
    /// as a workspace row) has none. Disabled from in-memory state only: an operation or conflict on the directory, or
    /// a busy / handing-over active session.
    /// </summary>
    private AsyncRelayCommand? RemoveWorkspaceCommand(TreeNodePlan plan, ProjectInfo project)
    {
        if (plan.IsMainCheckout
            || project.Kind != ProjectKind.Repository
            || string.Equals(plan.WorktreePath, project.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = new WorkspaceTarget(project, plan.WorktreePath, plan.Branch, plan.ForkOfBranch);
        var activeId = plan.ActiveSession?.Id;
        return new AsyncRelayCommand(
            ct => RemoveWorkspace?.Invoke(target, ct) ?? Task.CompletedTask,
            () => RemoveWorkspace is not null
                  && !Blocked(target.Directory)
                  && (activeId is null || IsSessionBusy?.Invoke(activeId) != true),
            OnFailed);
    }

    /// <summary>The restore / purge entries (7.4, 7.5) of removed session rows and recycled workspace rows.</summary>
    private void ApplyRecoveryCommands(TreeNodeViewModel node, TreeNodePlan plan)
    {
        ICommand? restore = null;
        ICommand? purge = null;
        if (plan.Kind == TreeNodeKind.Session
            && plan.Session is { Status: SessionStatus.Deleted, ParentSessionId: null } session
            && plan.Project is { } owner)
        {
            restore = new AsyncRelayCommand(
                ct => RestoreSession?.Invoke(session, owner, ct) ?? Task.CompletedTask,
                () => RestoreSession is not null && !Blocked(DirectoryOf(session)),
                OnFailed);
            purge = new AsyncRelayCommand(
                ct => PurgeSession?.Invoke(session, ct) ?? Task.CompletedTask,
                () => PurgeSession is not null,
                OnFailed);
        }
        else if (plan.Kind == TreeNodeKind.Worktree && plan.RowMark == TreeRowMark.Recycled && plan.Project is { } project)
        {
            var target = new WorkspaceTarget(project, plan.WorktreePath, plan.Branch, plan.ForkOfBranch);
            restore = new AsyncRelayCommand(
                ct => RestoreWorkspace?.Invoke(target, ct) ?? Task.CompletedTask,
                () => RestoreWorkspace is not null && !Blocked(target.Directory),
                OnFailed);
            purge = new AsyncRelayCommand(
                ct => PurgeAll?.Invoke(target, ct) ?? Task.CompletedTask,
                () => PurgeAll is not null,
                OnFailed);
        }

        node.SetRecoveryCommands(restore, purge);
    }

    /// <summary>
    /// The fork / merge-back / sync / abort entries (6.5, 6.6), hung on the shared menu until the menu generation (A5).
    /// Whether they can run is read from in-memory state only (I3): the workspace's operation / conflict flags and the
    /// busy flags of its active session. Git is consulted by the flows after the click, never by the menu.
    /// </summary>
    private void ApplyLifecycleCommands(TreeNodeViewModel node, TreeNodePlan plan)
    {
        ICommand? fork = null;
        ICommand? mergeBack = null;
        ICommand? sync = null;
        ICommand? abort = null;
        string? workspace = null;
        if (plan.Kind == TreeNodeKind.Worktree
            && plan.RowMark != TreeRowMark.Recycled
            && plan.Project is { Kind: ProjectKind.Repository } project)
        {
            var directory = plan.WorktreePath;
            workspace = directory;
            var source = new ForkSource(project, directory, plan.ActiveSession);
            var headForkable = plan.HeadKind is null or GitHeadKind.Branch;
            var present = plan.RowMark != TreeRowMark.Missing && !plan.IsStaleWorktree;
            fork = new AsyncRelayCommand(
                ct => ForkWorkspace?.Invoke(source, HandoverContextMode.Summary, ct) ?? Task.CompletedTask,
                () => present && headForkable && !Blocked(directory),
                OnFailed);
            if (plan.ForkOfBranch is { } sourceBranch)
            {
                var relation = new ForkRelation(project, directory, plan.Branch, sourceBranch, plan.ForkBaseSha);
                var activeId = plan.ActiveSession?.Id;
                bool CanMerge() => present && !Blocked(directory) && (activeId is null || IsSessionBusy?.Invoke(activeId) != true);
                mergeBack = new AsyncRelayCommand(
                    ct => MergeFork?.Invoke(relation, MergeDirection.MergeBack, ct) ?? Task.CompletedTask,
                    CanMerge,
                    OnFailed);
                sync = new AsyncRelayCommand(
                    ct => MergeFork?.Invoke(relation, MergeDirection.Sync, ct) ?? Task.CompletedTask,
                    CanMerge,
                    OnFailed);
            }

            // Always built; the conflict flag decides whether it shows, so a new snapshot needs no tree rebuild.
            abort = new AsyncRelayCommand(
                ct => AbortMerge?.Invoke(directory, ct) ?? Task.CompletedTask,
                () => true, // the shell refuses it while another operation runs there
                OnFailed);
        }
        else if (plan.Kind == TreeNodeKind.Session
                 && plan.Session is { Status: SessionStatus.Active, ParentSessionId: null } session
                 && plan.Project is { Kind: ProjectKind.Repository } sessionProject)
        {
            var directory = DirectoryOf(session);
            var source = new ForkSource(sessionProject, directory, session);
            fork = new AsyncRelayCommand(
                ct => ForkWorkspace?.Invoke(source, HandoverContextMode.Full, ct) ?? Task.CompletedTask,
                () => !Blocked(directory) && IsSessionBusy?.Invoke(session.Id) != true,
                OnFailed);
        }

        node.SetLifecycleCommands(workspace, fork, mergeBack, sync, abort);
        node.IsConflicted = workspace is not null && IsWorkspaceConflicted?.Invoke(workspace) == true;
    }

    private bool Blocked(string directory) => IsWorkspaceBlocked?.Invoke(directory) == true;

    private static string DirectoryOf(SessionInfo session) => session.WorktreePath ?? session.WorkspacePath;

    private HashSet<string> RunningSessionIds() => RunningSessions?.Invoke() ?? [];

    /// <summary>Opening a row never changes the session's status (6.3): a history row opens as a read-only view.</summary>
    private async Task OpenSessionAsync(SessionInfo session, CancellationToken ct)
    {
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
    /// that is not a git repository is reported without ever opening the new-branch dialog; only a repository then asks
    /// for the start point and the branch name in that dialog, which validates the name itself. A cancelled dialog creates
    /// nothing, and an unusable name that slipped through stops the creation and reports why through the same failure path
    /// as the rest of the flow. Everything after the dialog (the worktree and the session row) runs on the thread pool
    /// like the other tree operations.
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

        var choice = await pickNewBranch(new NewBranchRequest(project.RootPath, DefaultBranchName(), OfferSparse: true), ct)
            .ConfigureAwait(true);
        if (choice is null)
        {
            return;
        }

        // The dialog can outlive the command (the window closing, the flow being cancelled): nothing is created then.
        ct.ThrowIfCancellationRequested();

        var branchName = choice.BranchName;
        var startPoint = choice.StartPoint;
        var sparse = choice.Sparse;
        await CreateAsync(
                token => BackgroundRead.RunAsync(
                    async backgroundToken =>
                    {
                        var title = ProjectStrings.WorktreeSessionTitle();
                        WorktreeSession worktree;
                        try
                        {
                            worktree = await worktrees.CreateAsync(
                                    project.RootPath,
                                    title,
                                    branchName,
                                    startPoint,
                                    sparse,
                                    backgroundToken)
                                .ConfigureAwait(false);
                        }
                        catch (ArgumentException exception)
                        {
                            // ArgumentException.Message ends in "(Parameter 'x')"; the user gets the branch name and a
                            // fixed reason instead, the exception stays as the inner exception for the trace.
                            throw new InvalidOperationException(
                                Loc.Instance.Format(GitStrings.WorktreeBranchInvalid, branchName),
                                exception);
                        }
                        catch (GitException exception)
                        {
                            throw new InvalidOperationException(
                                sparse is null
                                    ? Loc.Instance.Format(GitStrings.WorktreeBranchUnavailable, branchName, exception.Message)
                                    : TreeMenuStrings.SparseCreateFailed(branchName, exception.Message),
                                exception);
                        }

                        try
                        {
                            return await catalog.CreateAsync(
                                    new SessionCreate(worktree.Path, title)
                                    {
                                        ProjectId = project.Id,
                                        WorktreePath = worktree.Path,
                                        Branch = worktree.Branch,
                                    },
                                    backgroundToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The session row was not created (or the flow was cancelled right after the worktree),
                            // so nothing refers to the folder: take the fresh, untouched worktree away again.
                            await TryRemoveOrphanedWorktreeAsync(worktree).ConfigureAwait(false);
                            throw;
                        }
                    },
                    token),
                ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Best-effort cleanup of a worktree whose session row could not be written: <see cref="WorktreeService.TryRemoveCreatedAsync"/>
    /// removes the worktree without force and then the branch it created without force. A worktree or branch that holds
    /// anything stays (a leftover <c>forge/…</c> branch is visible and deletable in the Git panel). Failures are traced,
    /// never thrown: the caller is already reporting the original error.
    /// </summary>
    private Task TryRemoveOrphanedWorktreeAsync(WorktreeSession worktree) =>
        worktrees.TryRemoveCreatedAsync(worktree, CancellationToken.None);

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
                async token2 =>
                {
                    var title = ProjectStrings.WorktreeSessionTitle();
                    var create = new SessionCreate(worktreePath, title)
                    {
                        ProjectId = project.Id,
                        WorktreePath = worktreePath,
                        Branch = branch,
                    };
                    try
                    {
                        return await catalog.CreateAsync(create, token2).ConfigureAwait(false);
                    }
                    catch (WorktreeOccupiedException occupied)
                    {
                        // The shell offers to take the workspace over with exactly this session (6.1).
                        throw new SessionCreateOccupiedException(occupied, create);
                    }
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

    /// <summary>
    /// Every session, roots and sub-agent children alike: the child links are what draws a session's agent rows (and so its
    /// chevron) even when that session was never opened in this run.
    /// </summary>
    private async Task<IReadOnlyList<SessionInfo>> QueryAllSessionsAsync(CancellationToken ct)
    {
        // QueryAsync never returns removed sessions; the history read adds them (with their sub-agent sessions), so the
        // tree can draw the removed-session and recycled-workspace rows (5.1).
        var live = await catalog.QueryAsync(new SessionQuery(), ct).ConfigureAwait(false);
        IReadOnlyList<SessionInfo> history;
        try
        {
            history = await catalog.ListHistoryAsync(new SessionQuery(), ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // A catalog without the lifecycle history (a test double) has no removed sessions to draw; the real stores
            // and their decorators all implement it.
            return live;
        }

        var removed = history.Where(static s => s.Status == SessionStatus.Deleted).ToArray();
        return removed.Length == 0 ? live : [.. live, .. removed];
    }

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
