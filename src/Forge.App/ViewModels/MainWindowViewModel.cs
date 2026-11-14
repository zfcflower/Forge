using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Services;
using Forge.App.Settings;
using Forge.App.Terminal;
using Forge.App.Git;
using Forge.App.Workbench;
using Forge.App.Lifecycle;
using Forge.Core.Storage;
using Forge.Editor.Lsp;
using Forge.HostClient;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>The shell's modal interactions, implemented by the view so the view models stay free of window types.</summary>
internal interface IDialogService
{
    Task<string?> PickFolderAsync(string title);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);

    Task<string?> PromptTextAsync(string title, string initial);

    /// <summary>Asks for the start point and the name of a new worktree branch; null means the user cancelled.</summary>
    Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct);

    /// <summary>
    /// Asks for the pull request's title, body and draft flag, prefilled from <paramref name="draft"/>; null means the user
    /// cancelled and nothing is created. A host that cannot show it (a recording double, a headless test) answers null, so
    /// the row reports "this host cannot show the dialog" instead of creating something nobody described.
    /// </summary>
    Task<Git.PullRequestDraft?> ComposePullRequestAsync(Git.PullRequestDraft draft, CancellationToken ct) =>
        Task.FromResult<Git.PullRequestDraft?>(null);

    /// <summary>
    /// Asks which merge strategy to use and whether the head branch goes away on the platform; null means the user cancelled
    /// and nothing is merged. A host that cannot show it answers null, so the menu row stays disabled.
    /// </summary>
    Task<Git.PullRequestMergeChoice?> ComposePullRequestMergeAsync(Git.PullRequestMergeRequest request, CancellationToken ct) =>
        Task.FromResult<Git.PullRequestMergeChoice?>(null);

    /// <summary>An information message with a single OK button.</summary>
    Task NotifyAsync(string title, string message) => ConfirmAsync(title, message, ShellStrings.Ok, ShellStrings.Ok);

    /// <summary>
    /// A question with several buttons; the last option is the cancel button. Returns the chosen index, -1 when the
    /// dialog was closed. The default offers the first and the last option only.
    /// </summary>
    async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options) =>
        await ConfirmAsync(title, message, options[0], options[^1]).ConfigureAwait(true) ? 0 : -1;

    /// <summary>
    /// The handover dialog (lifecycle 6.2): shows <paramref name="model"/>'s choices, runs
    /// <see cref="HandoverDialogModel.ExecuteAsync"/> and stays open with its error until it succeeds or the user
    /// cancels. True = handed over, false = cancelled; null = this host cannot show it (the caller reports that).
    /// </summary>
    Task<bool?> RunHandoverAsync(HandoverDialogModel model, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>
    /// The parallel fork dialog (lifecycle 6.5), hosted like <see cref="RunHandoverAsync"/>: true = forked, false =
    /// cancelled; null = this host cannot show it (the caller reports that).
    /// </summary>
    Task<bool?> RunForkAsync(ForkDialogModel model, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>
    /// A single choice from a list (7.10); entries with a <see cref="PickEntry.DisabledReason"/> are shown greyed out with
    /// that reason and cannot be chosen. Returns the chosen index, -1 when cancelled. The default offers the enabled
    /// entries as buttons of <see cref="ChooseAsync"/> (with the default <see cref="ChooseAsync"/> only the first of them),
    /// and with none enabled shows every entry's reason.
    /// </summary>
    async Task<int> PickAsync(string title, string message, IReadOnlyList<PickEntry> entries)
    {
        var enabled = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].DisabledReason is null)
            {
                enabled.Add(i);
            }
        }

        if (enabled.Count == 0)
        {
            var reasons = entries.Select(static entry => "• " + entry.Label + "\n  " + entry.DisabledReason);
            await NotifyAsync(title, message + "\n" + string.Join("\n", reasons)).ConfigureAwait(true);
            return -1;
        }

        var options = enabled.Select(i => entries[i].Label).Append(ShellStrings.Cancel).ToArray();
        var choice = await ChooseAsync(title, message, options).ConfigureAwait(true);
        return choice >= 0 && choice < enabled.Count ? enabled[choice] : -1;
    }
}

/// <summary>
/// A session could not be created because its workspace already has an active session; carries what would have been
/// created, so the shell can offer to take the workspace over with exactly that session.
/// </summary>
internal sealed class SessionCreateOccupiedException(WorktreeOccupiedException occupied, SessionCreate create)
    : InvalidOperationException(occupied.Message, occupied)
{
    public WorktreeOccupiedException Occupied { get; } = occupied;

    public SessionCreate Create { get; } = create;
}

/// <summary>
/// Root view model: owns the workbench layout, the open sessions, the panels and the persistence of the layout and the
/// open tabs. Every background task is started here; the view only renders.
/// </summary>
internal sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string OpenChangesToolId = "files-open-changes";

    /// <summary>Automation id (and header tool id) of the bottom group's "new terminal" button.</summary>
    private const string NewTerminalToolId = "terminal-new";

    /// <summary>Automation id (and header tool id) of the project panel's "add project" button.</summary>
    private const string AddProjectToolId = "add-project";

    /// <summary>Automation id (and header tool id) of the general-chats panel's "new chat" button.</summary>
    private const string NewGeneralSessionToolId = "new-general-session";

    /// <summary>Automation id (and header tool id) of the general-chats panel's refresh button.</summary>
    private const string RefreshGeneralChatsToolId = "refresh-general-chats";

    /// <summary>Automation id (and header tool id) of the project panel's refresh button.</summary>
    private const string RefreshProjectsToolId = "refresh-projects";

    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>Upper bound of the shutdown wait for the terminals; the coordinator itself stops waiting after four.</summary>
    private static readonly TimeSpan TerminalShutdownBudget = TimeSpan.FromSeconds(5);

    private readonly AppComposition composition;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private readonly LayoutStateLoader layoutState;
    private readonly Dictionary<string, SessionWorkspaceViewModel> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> handingOver = new(StringComparer.Ordinal);
    private readonly HashSet<string> openTabs = new(StringComparer.Ordinal);
    private readonly FinishedSubAgentsViewModel emptyFinishedMenu = new();
    private readonly Dictionary<string, string> projectRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> projectNames = new(StringComparer.Ordinal);
    private readonly SessionInstrumentSources instruments;
    private readonly DispatcherTimer clock;
    private readonly DispatcherTimer layoutSaveTimer;
    private readonly TerminalWorkbench terminals;
    private readonly WorkspaceDiagnostics? problemsDiagnostics;
    private PanelToolButton newTerminalButton;
    private ReviewStore reviews;
    private PanelToolButton? openChangesTool;
    private PanelToolButton? addProjectTool;
    private PanelToolButton? refreshProjectsTool;
    private PanelToolButton? newGeneralSessionTool;
    private PanelToolButton? refreshGeneralChatsTool;
    private SessionWorkspaceViewModel? activeSession;
    private FinishedSubAgentsViewModel? watchedFinishedMenu;
    private IDialogService? dialogs;

    /// <summary>Set once the restored layout's bottom-group state has been handed to the first attached session.</summary>
    private bool bottomMinimizeAdopted;
    private bool disposed;
    private SettingsWindow? settingsWindow;
    private bool closing;

    public MainWindowViewModel(AppComposition composition)
    {
        this.composition = composition;
        layoutState = new LayoutStateLoader(composition.UiState);
        reviews = composition.Workspace?.Reviews ?? new ReviewStore(composition.Paths.ReviewsDirectory);

        Status = new StatusBarViewModel(composition);
        Settings = new SettingsViewModel(composition);
        instruments = new SessionInstrumentSources(composition, CountPendingApprovals, OpenSettingsSection);
        composition.ActiveSessionTools = () => activeSession?.ToolNames() ?? [];
        composition.ActiveSessionToolCatalog = () => activeSession?.DescribeTools() ?? [];
        composition.ActiveSessionWorkspace = () => activeSession?.WorkDir;
        if (ApprovalServices.Current is { } approvalServices)
        {
            approvalServices.Queue.Changed += OnApprovalQueueChanged;
        }

        Projects = new ProjectTreeViewModel(
            composition.Projects,
            composition.Catalog,
            composition.Worktrees,
            composition.Worktrees.GetHeadAsync,
            OpenSessionByIdAsync,
            ConfirmAndCreateAsync,
            PromptTextAsync,
            PickNewBranchAsync,
            RevealAsync,
            AddProjectAsync,
            TreeScope.Projects)
        {
            RunningSessions = () => new HashSet<string>(
                sessions.Values.Where(static session => session.IsRunning).Select(static session => session.SessionId),
                StringComparer.Ordinal),
        };
        Projects.SessionArchived = OnSessionArchived;
        Projects.ArchiveGuard = ConfirmArchiveAsync;
        WireLifecycle(Projects);
        Projects.AgentTrees = sessionId =>
            sessions.TryGetValue(sessionId, out var session) ? session.AgentSnapshots() : [];
        Projects.OpenSubAgent = OpenSubAgentTabAsync;

        // The general chats are their own panel above the projects; it is a second tree over the same registry, scoped to
        // the general workspace so neither panel has to hide the other's rows.
        GeneralChats = new ProjectTreeViewModel(
            composition.Projects,
            composition.Catalog,
            composition.Worktrees,
            composition.Worktrees.GetHeadAsync,
            OpenSessionByIdAsync,
            ConfirmAndCreateAsync,
            PromptTextAsync,
            PickNewBranchAsync,
            RevealAsync,
            AddProjectAsync,
            TreeScope.General)
        {
            RunningSessions = Projects.RunningSessions,
            AgentTrees = Projects.AgentTrees,
            OpenSubAgent = OpenSubAgentTabAsync,
            SessionArchived = OnSessionArchived,
            ArchiveGuard = ConfirmArchiveAsync,
        };
        WireLifecycle(GeneralChats);
        Changes = new ChangesViewModel(
            composition.Git,
            composition.TurnCheckpoints,
            path => OpenFile(path, newTab: false));
        Changes.ConfigureReview(
            CreateReviewServices(),
            (title, message, confirm, cancel) => dialogs is null
                ? Task.FromResult(false)
                : dialogs.ConfirmAsync(title, message, confirm, cancel));
        GitSync = new GitSyncViewModel(
            composition.Git,
            composition.GitPanelSettings,
            path => OpenFile(path, newTab: false),
            composition.Notifications,
            composition.PullRequests,
            ComposePullRequestAsync,
            ComposePullRequestMergeAsync,
            ConfirmGitRowActionAsync,
            DescribePullRequestAsync,
            OpenChecksForPullRequest);
        Files = new FilesPanelViewModel(
            composition.Git,
            path => OpenFile(path, newTab: false),
            path => OpenFile(path, newTab: true),
            path => ShowChangesAsync(path),
            lineCountsFactory: null,
            changeReview: Changes,
            gitSync: GitSync);
        Todos = new TodoListViewModel(composition.Todos, composition.TodoStepCommitter);
        Reviews = new ReviewPanelViewModel(reviews, path => ShowChangesAsync(path));
        Specs = new SpecTreeViewModel(root => new SpecGraph(root), path => OpenFile(path, newTab: false));

        // One aggregator for every language session of the app: the pool spans workspaces, so the list does too. Null when
        // there is no editor layer at all (a headless composition), and the panel then simply has nothing to show.
        problemsDiagnostics = composition.Editor is { } editor ? new WorkspaceDiagnostics(editor.Sessions) : null;
        Problems = new ProblemsPanelViewModel(problemsDiagnostics, OpenProblem);
        ShowChangesCommand = new RelayCommand(() => ShowChangesAsync(null));
        NewTerminalCommand = new RelayCommand(OpenTerminal);
        Workbench = new WorkbenchLayout(Projects, Files, Todos, Reviews, Specs, GeneralChats, Problems);
        terminals = new TerminalWorkbench(
            Workbench,
            composition.Preferences,
            composition.Host?.Client,
            () => RevealBottomGroup(activeSession));
        newTerminalButton = CreateNewTerminalButton();

        GitSync.PropertyChanged += OnGitSyncPropertyChanged;
        openChangesTool = new PanelToolButton(
            OpenChangesToolId,
            ShellStrings.OpenChangesTooltip,
            "IconChangesDocument",
            ShowChangesCommand);
        Workbench.FilesPanel.HeaderTools.Add(openChangesTool);
        // Each left-column panel keeps its own tools in its title bar: the panel chrome owns the bar, so a toolbar inside the
        // body would read as a second, broken title row. General chats add a chat, projects add a project.
        addProjectTool = CreateAddProjectTool();
        refreshProjectsTool = CreateRefreshProjectsTool();
        newGeneralSessionTool = CreateNewGeneralSessionTool();
        Workbench.ProjectPanel.HeaderTools.Add(addProjectTool);
        Workbench.ProjectPanel.HeaderTools.Add(refreshProjectsTool);
        refreshGeneralChatsTool = CreateRefreshGeneralChatsTool();
        if (Workbench.GeneralPanel is { } generalPanel)
        {
            generalPanel.HeaderTools.Add(newGeneralSessionTool);
            generalPanel.HeaderTools.Add(refreshGeneralChatsTool);
        }
        Workbench.BottomTabs.HeaderTools.Add(emptyFinishedMenu);
        Workbench.BottomTabs.HeaderTools.Add(newTerminalButton);
        Workbench.TabClosed += OnTabClosed;
        Workbench.ContentCloseGuard = ConfirmCloseTabAsync;
        Workbench.ActiveTabChanged += OnActiveTabChanged;
        Workbench.LayoutChanged += OnLayoutChanged;
        Workbench.PropertyChanged += OnWorkbenchPropertyChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        AddProjectCommand = new AsyncRelayCommand(AddProjectAsync, () => !disposed, OnCommandFailed);
        ToggleSettingsCommand = new RelayCommand(ToggleSettings);
        ReloadSettingsCommand = new RelayCommand(ReloadSettings);
        RevealGeneralCommand = new AsyncRelayCommand(
            ct => RevealAsync(composition.Paths.GeneralWorkspaceDirectory),
            () => true,
            OnCommandFailed);

        // The plan panel's view model and the command that opens it (wave W7). Created before the window binds, so the
        // overlay in MainWindow.axaml has its data context from the first frame.
        InitializePlan();

        clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += OnClockTick;
        clock.Start();

        layoutSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LayoutSaveDelay };
        layoutSaveTimer.Tick += OnLayoutSaveTick;

        if (composition.Host is { } host)
        {
            host.StatusChanged += OnHostStatusChanged;
            host.SurfaceChanged += OnHostSurfaceChanged;
            host.Start();
        }

        if (composition.Mcp is { } mcp)
        {
            _ = Task.Run(mcp.StartAsync);
        }
    }

    public StatusBarViewModel Status { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>
    /// The window the settings window is centred over; set by the shell once it exists, so the view model can open a
    /// window without holding a reference to the window it lives in (which would be a cycle).
    /// </summary>
    public Func<Avalonia.Controls.Window?>? SettingsOwner { get; set; }

    /// <summary>
    /// The settings window the shell created, or <c>null</c> while none is open. A test seam: a headless test has no way to
    /// observe the window otherwise, and the alternative (a test-only public API on the window) would be worse.
    /// </summary>
    internal Avalonia.Controls.Window? SettingsWindow => settingsWindow;

    public ProjectTreeViewModel Projects { get; }

    /// <summary>The general-chats tree of the panel above the projects.</summary>
    public ProjectTreeViewModel GeneralChats { get; }

    public FilesPanelViewModel Files { get; }

    public TodoListViewModel Todos { get; }

    public ReviewPanelViewModel Reviews { get; }

    /// <summary>The Problems tab: every diagnostic the language sessions hold, across workspaces.</summary>
    public ProblemsPanelViewModel Problems { get; }

    public SpecTreeViewModel Specs { get; }

    public ChangesViewModel Changes { get; }

    /// <summary>
    /// The git row at the bottom of the files panel: the active worktree's branch state, the three sync verbs and the last
    /// result. Its ahead/behind and branch also feed the composer strip and the session title row.
    /// </summary>

    public GitSyncViewModel GitSync { get; }

    public WorkbenchLayout Workbench { get; }

    public AsyncRelayCommand AddProjectCommand { get; }

    public RelayCommand ToggleSettingsCommand { get; }

    public RelayCommand ReloadSettingsCommand { get; }

    public RelayCommand ShowChangesCommand { get; }

    /// <summary>Opens a terminal in the active session; the status bar button, the bottom group's "+" and Ctrl+` use it.</summary>
    public RelayCommand NewTerminalCommand { get; }

    /// <summary>Text of the status-bar terminal button; re-read after a language switch.</summary>
    public string TerminalButtonText => Loc.Instance[TerminalStrings.StatusButton];

    public AsyncRelayCommand RevealGeneralCommand { get; }

    public string ProviderNotice => composition.Providers.StatusText.Resolve();

    public bool ShowProviderNotice => !composition.Providers.IsConfigured;

    public string GeneralWorkspaceText => composition.Paths.GeneralWorkspaceDirectory;

    public SessionWorkspaceViewModel? ActiveSession
    {
        get => activeSession;
        private set
        {
            var previous = activeSession;
            if (Set(ref activeSession, value))
            {
                previous?.Instruments.SetGit(null);
                CloseChecks();
                SetTreesActiveSession(activeSession?.SessionId);
                OnPropertyChanged(nameof(HasActiveSession));
                OnPropertyChanged(nameof(ActiveSessionTitle));
                ApplyPanelContext();
                PushGitText();
                terminals.OnActiveSessionChanged(activeSession?.SessionId, activeSession?.WorkDir);
            }
        }
    }

    public bool HasActiveSession => activeSession is not null;

    public string ActiveSessionTitle => activeSession?.Title ?? ShellStrings.NoOpenSession;

    /// <summary>The active session's branch as the Git tab last read it; empty outside a repository.</summary>
    public string ActiveBranchText => activeSession is null ? string.Empty : GitSync.BranchLabel;

    public bool HasActiveBranch => ActiveBranchText.Length > 0;

    public void AttachDialogs(IDialogService dialogService) => dialogs = dialogService;

    /// <summary>The git reader the shell's dialogs use: the new-branch dialog lists that repository's branches.</summary>
    internal GitCli GitClient => composition.Git;

    /// <summary>Awaits the schema migration, registers the general workspace, restores the layout and the open tabs.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await composition.Initialization.ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status.SetWorkspaceProblem(ShellStrings.DatabaseProblem(exception.Message));
            composition.Notifications.Notify(
                NotificationLevel.Error,
                NotificationsStrings.StartupProblemTitle,
                ShellStrings.DatabaseProblem(exception.Message),
                "startup-failed");
            return;
        }

        // The settings file falling back to defaults is silent everywhere else (the settings page shows it, but only to a
        // user who opens it), and losing every preference without a word is worse than one toast.
        if (composition.PreferencesProblem is { Length: > 0 } preferencesProblem)
        {
            composition.Notifications.Notify(
                NotificationLevel.Warning,
                NotificationsStrings.PreferencesProblemTitle,
                LocText.Literal(preferencesProblem),
                "preferences-problem");
        }

        // Invalid network values silently falling back to the system resolver or proxy would route traffic somewhere the
        // user did not choose, so it gets the same one toast.
        if (composition.NetworkProblem is { Length: > 0 } networkProblem)
        {
            composition.Notifications.Notify(
                NotificationLevel.Warning,
                Forge.App.Localization.NetworkStrings.LoadProblemTitle,
                LocText.Literal(Loc.Instance.Format(Forge.App.Localization.NetworkStrings.LoadProblem, networkProblem)),
                "network-problem");
        }

        await RunStartupLifecycleAsync().ConfigureAwait(true);
        RefreshProjectCacheAsync();
        await Projects.InitializeAsync(composition.Paths.GeneralWorkspaceDirectory, CancellationToken.None).ConfigureAwait(true);
        await GeneralChats.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        await RestoreAsync().ConfigureAwait(true);
        AttachAutomations();

        // The sessions (and with them the agent trees of each session) only exist after the tabs are restored, while the
        // tree was built before that: without this second pass a session row shows no chevron until the first click
        // refreshes the whole tree.
        RefreshTreeAgents();
    }

    /// <summary>
    /// The two trees of the left column answer the same questions about the same sessions; every "refresh", "mark running"
    /// or "active session" notification has to reach both, or one panel shows stale rows.
    /// </summary>
    private async Task RefreshTreesAsync(CancellationToken ct)
    {
        await GeneralChats.RefreshAsync(ct).ConfigureAwait(true);
        await Projects.RefreshAsync(ct).ConfigureAwait(true);
        await RefreshConflictSnapshotAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Refreshes the merge-conflict flag (section 2) of every repository workspace row in the trees and of the workspace
    /// of every live project session tab, off the dispatcher (<c>MERGE_HEAD</c> in the checkout's own git dir, four reads
    /// at a time), then lets the trees re-read it (the "abort the merge" entry, the disabled items). Only the checked
    /// workspaces are updated, so a flag a flow set elsewhere stays; a workspace git cannot read keeps its previous flag,
    /// and a read that overlapped an operation in that workspace is dropped (<see cref="WorkspaceStates.Epoch"/>).
    /// </summary>
    private async Task RefreshConflictSnapshotAsync(CancellationToken ct)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        void Add(string directory)
        {
            if (directories.Add(OccupancyKey.Compute(null, directory)))
            {
                unique.Add(directory);
            }
        }

        foreach (var directory in Projects.WorkspaceDirectories())
        {
            Add(directory);
        }

        foreach (var directory in GeneralChats.WorkspaceDirectories())
        {
            Add(directory);
        }

        foreach (var session in sessions.Values)
        {
            if (!session.IsReadOnlyView && session.Info.ProjectId is not null)
            {
                Add(session.Info.WorktreePath ?? session.Info.WorkspacePath);
            }
        }

        if (unique.Count > 0)
        {
            var git = composition.Git;
            var states = composition.WorkspaceStates;
            await Task.Run(
                    () => Parallel.ForEachAsync(
                        unique,
                        new ParallelOptions { MaxDegreeOfParallelism = ConflictReadConcurrency, CancellationToken = ct },
                        async (directory, token) =>
                        {
                            // A workspace a flow is working in is the flow's to flag; a read that raced an operation
                            // (started, or ended with its own flag, while git ran) is dropped, not written over it.
                            var epoch = states.Epoch(directory);
                            if (states.IsOperating(directory))
                            {
                                return;
                            }

                            try
                            {
                                var inMerge = await git.HasMergeInProgressAsync(directory, token).ConfigureAwait(false);
                                states.TrySetConflictedFromRead(directory, inMerge, epoch);
                            }
                            catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                            {
                                AppLogs.Lifecycle.ZLogWarning($"merge state read failed: {exception.Message}");
                            }
                        }),
                    ct)
                .ConfigureAwait(true);
        }

        NotifyTreeCommandStates();
    }

    /// <summary>Concurrent <c>MERGE_HEAD</c> reads of the conflict snapshot.</summary>
    private const int ConflictReadConcurrency = 4;

    private void NotifyTreeCommandStates()
    {
        GeneralChats.NotifyCommandStates();
        Projects.NotifyCommandStates();
    }

    private void RefreshTreeAgents()
    {
        GeneralChats.RefreshAgentTrees();
        Projects.RefreshAgentTrees();
    }

    private void SetTreeRunning(string sessionId, bool isRunning)
    {
        GeneralChats.MarkRunning(sessionId, isRunning);
        Projects.MarkRunning(sessionId, isRunning);

        // A session going busy or idle changes whether its fork / merge entries can run.
        NotifyTreeCommandStates();
    }

    private void SetTreesActiveSession(string? sessionId)
    {
        GeneralChats.SetActiveSession(sessionId);
        Projects.SetActiveSession(sessionId);
    }

    private void SetTreesActiveAgent(string? agentId)
    {
        GeneralChats.SetActiveAgent(agentId);
        Projects.SetActiveAgent(agentId);
    }

    /// <summary>
    /// An agent row of the project tree was clicked: bring its session to the front of the main region (opening it when it is
    /// not loaded), reopen the bottom group if the user had collapsed it, and select that agent's tab there. Finished agents
    /// live only in the bottom group's dropdown, so the lookup falls back to the finished list.
    /// </summary>
    private async Task OpenSubAgentTabAsync(string sessionId, string agentId, CancellationToken ct)
    {
        if (!sessions.ContainsKey(sessionId))
        {
            await OpenSessionByIdAsync(sessionId, ct).ConfigureAwait(true);
        }
        else
        {
            ActivateSessionTab(sessionId);
        }

        if (!sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        // The row is what opened the group, so the session's own "never pop up" choice does not apply to this click.
        RevealBottomGroup(session);

        var agent = FindSubAgent(session, agentId);
        if (agent is null)
        {
            return;
        }

        // Exactly what choosing it in the bottom group's dropdown does: a finished agent opens read-only and its transcript
        // is replayed from the store, so the tab has content even after a restart.
        if (session.Finished.Contains(agent))
        {
            session.OpenFinished(agent);
        }
        else
        {
            OpenSubAgentTab(agent);
        }

        SetTreesActiveAgent(agent.AgentId);
    }

    /// <summary>
    /// Matches by the agent's own id or by the child session it ran in: an agent row drawn from the store before this session
    /// was loaded only knows the child session id.
    /// </summary>
    private static SubAgentViewModel? FindSubAgent(SessionWorkspaceViewModel session, string agentId)
    {
        foreach (var agent in session.SubAgents)
        {
            if (MatchesAgent(agent, agentId))
            {
                return agent;
            }
        }

        foreach (var agent in session.Finished)
        {
            if (MatchesAgent(agent, agentId))
            {
                return agent;
            }
        }

        return null;

        static bool MatchesAgent(SubAgentViewModel agent, string id) =>
            string.Equals(agent.AgentId, id, StringComparison.Ordinal)
            || string.Equals(agent.ChildSessionId, id, StringComparison.Ordinal);
    }

    private void RefreshProjectCacheAsync() =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var projects = await composition.Projects.ListAsync(CancellationToken.None).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        projectRoots.Clear();
                        projectNames.Clear();
                        foreach (var project in projects)
                        {
                            projectRoots[project.Id] = project.RootPath;
                            projectNames[project.Id] = project.Name;
                        }

                        // Restored tabs are built before this read lands, so their strips get the project here instead of
                        // never: the project button is the only place the session's repository is named.
                        foreach (var session in sessions.Values)
                        {
                            ApplyProject(session);
                        }
                    });
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var problem = ShellStrings.ProjectsProblem(exception.Message);
                        Status.SetWorkspaceProblem(problem);
                        composition.Notifications.Notify(
                            NotificationLevel.Warning,
                            NotificationsStrings.StartupProblemTitle,
                            problem,
                            "projects-problem");
                    });
                }
            });

    private async Task RestoreAsync()
    {
        var layout = await layoutState.ReadLayoutAsync(CancellationToken.None).ConfigureAwait(true);
        LayoutSnapshot? restored = null;
        if (layout is not null)
        {
            restored = layout;
            try
            {
                Workbench.Apply(layout);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                AppLogs.Layout.ZLogWarning($"restore failed, using the default layout: {exception.Message}");
                return;
            }
        }

        var tabs = await layoutState.ReadOpenTabsAsync(CancellationToken.None).ConfigureAwait(true);
        if (tabs is null)
        {
            ApplyFloating(restored);
            return;
        }

        foreach (var id in tabs.Sessions)
        {
            try
            {
                await OpenSessionByIdAsync(id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
            {
                AppLogs.Layout.ZLogWarning($"skipped session {id}: {exception.Message}");
            }
        }

        if (tabs.PreviewFile is { } file && File.Exists(file))
        {
            await OpenFileAsync(file, newTab: false, CancellationToken.None).ConfigureAwait(true);
        }

        foreach (var pinned in tabs.PinnedFiles)
        {
            if (File.Exists(pinned))
            {
                await OpenFileAsync(pinned, newTab: true, CancellationToken.None).ConfigureAwait(true);
            }
        }

        if (tabs.ChangesOpen)
        {
            ShowChangesTab(activate: false);
        }

        if (tabs.ActiveSession is { } active && sessions.TryGetValue(active, out var session))
        {
            ActivateSessionTab(session.SessionId);
            ActiveSession = session;
        }

        if (tabs.ActivePreview is { } previewId && Workbench.FindTab(previewId) is { } previewTab)
        {
            Workbench.ActivateTab(previewTab);
        }

        ApplyFloating(restored);
    }

    /// <summary>A floating window can only be restored once the tab it holds exists again.</summary>
    private void ApplyFloating(LayoutSnapshot? restored)
    {
        if (restored is null)
        {
            return;
        }

        try
        {
            Workbench.ApplyFloating(restored);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            AppLogs.Layout.ZLogWarning($"floating restore failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Captures the layout on the UI thread (the layout model is not thread safe) and writes it on the caller's thread. The
    /// shutdown path passes a pool thread, so the dispatcher never blocks on the database.
    /// </summary>
    private async Task SaveLayoutAsync(CancellationToken ct)
    {
        if (disposed && ct.IsCancellationRequested)
        {
            return;
        }

        var state = CaptureState();
        await WriteStateAsync(state, ct).ConfigureAwait(false);
    }

    private (LayoutSnapshot Layout, OpenTabsSnapshot Tabs) CaptureState() =>
        (
            Workbench.Capture(),
            new OpenTabsSnapshot
            {
                Sessions = [.. openTabs],
                ActiveSession = activeSession?.SessionId,
                PreviewFile = PreviewFilePath(),
                PinnedFiles = PinnedFilePaths(),
                ChangesOpen = Workbench.FindTab(WorkbenchIds.ChangesTab) is not null,
                ActivePreview = Workbench.PreviewTabs.ActiveTab?.Id,
            });

    private string? PreviewFilePath() =>
        Workbench.FindTab(WorkbenchIds.PreviewTab)?.Content is FileDocumentViewModel { Path.Length: > 0 } file
            ? file.Path
            : null;

    private List<string> PinnedFilePaths()
    {
        var paths = new List<string>();
        var tabs = Workbench.PreviewTabs.Tabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Id is { } id && id.StartsWith(WorkbenchIds.PinnedFileTabPrefix, StringComparison.Ordinal))
            {
                paths.Add(id[WorkbenchIds.PinnedFileTabPrefix.Length..]);
            }
        }

        return paths;
    }

    private async Task WriteStateAsync((LayoutSnapshot Layout, OpenTabsSnapshot Tabs) state, CancellationToken ct)
    {
        await layoutState.WriteLayoutAsync(state.Layout, ct).ConfigureAwait(false);
        await layoutState.WriteOpenTabsAsync(state.Tabs, ct).ConfigureAwait(false);
    }

    /// <summary>Final save: snapshot on the UI thread, write on a pool thread, bounded wait.</summary>
    private void SaveLayoutOnShutdown()
    {
        var state = CaptureState();
        try
        {
            if (!Task.Run(() => WriteStateAsync(state, CancellationToken.None), CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(3)))
            {
                AppLogs.Layout.ZLogWarning($"final save timed out");
            }
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            AppLogs.Layout.ZLogError(exception, $"final save failed");
        }
    }

    private void OnLayoutSaveTick(object? sender, EventArgs e)
    {
        layoutSaveTimer.Stop();
        _ = SaveLayoutAsync(CancellationToken.None);
    }

    private void ScheduleLayoutSave()
    {
        // Teardown ends the terminals, and every terminal tab that closes raises TabClosed: after closing began, a save
        // would only be scheduled for a window that is already gone.
        if (disposed || closing)
        {
            return;
        }

        layoutSaveTimer.Stop();
        layoutSaveTimer.Start();
    }

    // ---------------------------------------------------------------- sessions

    internal async Task OpenSessionByIdAsync(string sessionId, CancellationToken ct)
    {
        var sessions = composition.Sessions;
        var info = await BackgroundRead.RunAsync(token => sessions.GetSessionAsync(sessionId, token), ct)
            .ConfigureAwait(true);
        if (info is null || info.ParentSessionId is not null)
        {
            return;
        }

        // Opening never changes a session's status (6.3): a session that is not Active opens as a read-only view, and
        // only "continue" (ActivateAsync, which honours the one-active-session-per-workspace rule) makes it Active again.
        await OpenSessionAsync(info, ct).ConfigureAwait(true);
    }

    private async Task OpenSessionAsync(SessionInfo info, CancellationToken ct)
    {
        ComposerCarry? carried = null;
        if (sessions.TryGetValue(info.Id, out var existing))
        {
            if (!(existing.IsReadOnlyView && info.Status == SessionStatus.Active) || existing.IsRunning)
            {
                ShowSessionTab(existing);
                openTabs.Add(info.Id);
                ActiveSession = existing;
                ScheduleLayoutSave();
                return;
            }

            // The read-only view of a session that became Active again ("continue"): the live view replaces it in the
            // same tab (ShowSessionTab re-binds the tab), and takes over its unsent draft and queue (R6).
            sessions.Remove(info.Id);
            existing.BecameReadOnly -= OnSessionBecameReadOnly;
            carried = existing.TakeComposerState();
            UnhookAutomations(existing);
            existing.Dispose();
        }

        var readOnly = info.Status != SessionStatus.Active;
        var catalog = composition.Catalog;
        var session = new SessionWorkspaceViewModel(
            info,
            readOnly ? null : composition.Runtimes.Get(info),
            composition.Checkpoints,
            composition.Sessions,
            composition.Providers,
            OnSubAgentToolRequested,
            instrumentSources: instruments,
            preferences: composition.Preferences)
        {
            ChildQuery = readOnly
                ? (parent, token) => catalog.QueryAsync(new SessionQuery { ParentSessionId = parent }, token)
                : null,
        };
        if (readOnly)
        {
            AttachReadOnlyCommands(session);
        }
        else
        {
            session.TurnGate = token => CheckTurnAsync(session, token);
        }

        session.BecameReadOnly += OnSessionBecameReadOnly;
        ApplyProject(session);
        session.TurnCompleted += OnTurnCompleted;
        if (!readOnly)
        {
            HookAutomations(session);
        }

        session.SubAgentsChanged += OnSubAgentsChanged;
        session.AgentTreeChanged += OnAgentTreeChanged;
        sessions[info.Id] = session;
        if (carried is { } state)
        {
            session.RestoreComposerState(state);
        }

        await session.LoadHistoryAsync(ct).ConfigureAwait(true);
        ShowSessionTab(session);
        openTabs.Add(info.Id);
        ActiveSession = session;
        ScheduleLayoutSave();
    }

    /// <summary>
    /// The turn-start check (6.3), read off the dispatcher: the store must still have the session as Active (a handover
    /// or a restore elsewhere may have replaced it), and its workspace must be neither in an operation (a merge back /
    /// sync running in it: this also covers a tab opened after the operation began) nor in a merge conflict (the snapshot
    /// the tree refresh keeps; no git runs here). A store failure lets the turn start: the check guards against a stale tab,
    /// it is not a second source of truth.
    /// </summary>
    private async Task<TurnRejection> CheckTurnAsync(SessionWorkspaceViewModel session, CancellationToken ct)
    {
        var store = composition.Sessions;
        var id = session.SessionId;
        SessionInfo? current;
        try
        {
            current = await BackgroundRead.RunAsync(token => store.GetSessionAsync(id, token), ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Turn.ZLogWarning($"status check failed: {exception.Message}");
            Status.SetWorkspaceProblem(LifecycleStrings.TurnCheckFailed(exception.Message));
            return TurnRejection.None;
        }

        if (current is null || current.Status == SessionStatus.Deleted)
        {
            return TurnRejection.Removed;
        }

        if (current.Status != SessionStatus.Active)
        {
            return TurnRejection.TakenOver;
        }

        var directory = current.WorktreePath ?? current.WorkspacePath;
        if (composition.WorkspaceStates.IsOperating(directory))
        {
            return TurnRejection.WorkspaceOperating;
        }

        return composition.WorkspaceStates.IsConflicted(directory)
            ? TurnRejection.MergeConflict
            : TurnRejection.None;
    }

    /// <summary>A live tab found taken over at turn start: its runtime goes, the tab stays as a read-only view.</summary>
    private void OnSessionBecameReadOnly(SessionWorkspaceViewModel session)
    {
        composition.Runtimes.Release(session.SessionId);
        AttachReadOnlyCommands(session);
        _ = RefreshTreesAfterLifecycleAsync();
    }

    /// <summary>
    /// "Continue this session" on an archived read-only view; disabled when its workspace folder is gone (6.3). The folder
    /// probe runs on the thread pool (a dead network drive must not stall the dispatcher); until it answers the command
    /// stays enabled and <see cref="ContinueSessionAsync"/> checks the folder again itself.
    /// </summary>
    private void AttachReadOnlyCommands(SessionWorkspaceViewModel session)
    {
        var info = session.Info;
        var directory = info.WorktreePath ?? info.WorkspacePath;
        var command = new AsyncRelayCommand(
            ct => ContinueSessionAsync(session.SessionId, ct),
            () => session.ContinueBlockedReason is null,
            exception => Status.SetWorkspaceProblem(LocText.Literal(exception.Message)));
        session.ContinueCommand = command;
        _ = ProbeContinueAsync(session, command, info);
    }

    private async Task ProbeContinueAsync(SessionWorkspaceViewModel session, AsyncRelayCommand command, SessionInfo info)
    {
        string? reason;
        try
        {
            reason = await ContinueBlockAsync(info, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The probe only greys the button; the click checks again and reports what it finds.
            AppLogs.Lifecycle.ZLogWarning($"could not probe whether '{info.Id}' can continue: {exception.Message}");
            reason = null;
        }

        session.ContinueBlockedReason = reason;
        command.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Why a session cannot continue where it is (6.3), or null: its folder is gone, or it is a repository worktree that
    /// git marks prunable or does not list (a turn there would run git against a broken or an enclosing repository). A
    /// worktree list that cannot be read is unknown and blocks nothing: it is neither healthy nor stale, and the turn's own
    /// git calls report what is wrong. Runs off the dispatcher.
    /// </summary>
    internal Task<string?> ContinueBlockAsync(SessionInfo info, CancellationToken ct)
    {
        var git = composition.Git;
        var projects = composition.Projects;
        return Task.Run(
            async () =>
            {
                var directory = info.WorktreePath ?? info.WorkspacePath;
                if (!Directory.Exists(directory))
                {
                    return LifecycleStrings.DirectoryMissing;
                }

                if (info.ProjectId is not { } projectId || string.IsNullOrEmpty(info.WorktreePath))
                {
                    return null;
                }

                var project = (await projects.ListAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
                if (project is not { Kind: ProjectKind.Repository } || RemovalFlow.SameDirectory(directory, project.RootPath))
                {
                    return null;
                }

                IReadOnlyList<GitWorktree>? list = null;
                try
                {
                    list = await git.ListWorktreesAsync(project.RootPath, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    AppLogs.Lifecycle.ZLogWarning($"could not list the worktrees of '{project.RootPath}': {exception.Message}");
                }

                return WorktreeList.Judge(list, directory) switch
                {
                    (WorktreeRegistration.Prunable, var prunableReason) => LifecycleStrings.WorktreePrunable(prunableReason),
                    (WorktreeRegistration.Unregistered, _) => LifecycleStrings.WorktreeUnregistered,
                    _ => (string?)null,
                };
            },
            ct);
    }

    /// <summary>
    /// Makes a session Active again through <see cref="ISessionCatalog.ActivateAsync"/>, the one path that honours the
    /// one-active-session-per-workspace rule; a workspace that already has an active session ends in the occupied dialog.
    /// </summary>
    internal async Task ContinueSessionAsync(string sessionId, CancellationToken ct)
    {
        var catalog = composition.Catalog;
        var store = composition.Sessions;
        try
        {
            var info = await BackgroundRead.RunAsync(token => store.GetSessionAsync(sessionId, token), ct).ConfigureAwait(true);
            var block = info is null ? LifecycleStrings.DirectoryMissing : await ContinueBlockAsync(info, ct).ConfigureAwait(true);
            if (block is not null)
            {
                Status.SetWorkspaceProblem(LocText.Literal(LifecycleStrings.ContinueBlocked(block)));
                return;
            }

            var activated = await BackgroundRead.RunAsync(token => catalog.ActivateAsync(sessionId, token), ct)
                .ConfigureAwait(true);
            if (!activated)
            {
                // The session is gone or no longer resumable (removed elsewhere): say so instead of doing nothing.
                Status.SetWorkspaceProblem(LifecycleStrings.ContinueFailed);
                await OpenSessionByIdAsync(sessionId, ct).ConfigureAwait(true);
                return;
            }
        }
        catch (WorktreeOccupiedException occupied)
        {
            await ShowOccupiedAsync(occupied, HandoverIntent.Resume(sessionId), ct).ConfigureAwait(true);
            return;
        }

        await OpenSessionByIdAsync(sessionId, ct).ConfigureAwait(true);
        await RefreshTreesAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The one place a <see cref="WorktreeOccupiedException"/> ends up (6.1): the workspace's active session is named,
    /// and the user can take it over with <paramref name="intent"/> (the session that was to be created or continued),
    /// view it, or cancel. Nothing changes before the handover dialog.
    /// </summary>
    internal async Task ShowOccupiedAsync(WorktreeOccupiedException occupied, HandoverIntent? intent, CancellationToken ct)
    {
        var store = composition.Sessions;
        var holderId = occupied.HolderSessionId;
        var holder = await BackgroundRead.RunAsync(token => store.GetSessionAsync(holderId, token), ct).ConfigureAwait(true);
        var title = holder?.Title ?? holderId;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(LifecycleStrings.OccupiedMessage(title)));
            return;
        }

        if (holder is null)
        {
            await dialogs.NotifyAsync(LifecycleStrings.OccupiedTitle, LifecycleStrings.OccupiedMessage(title)).ConfigureAwait(true);
            return;
        }

        var takeOver = intent is not null;
        IReadOnlyList<string> options = takeOver
            ? [HandoverStrings.TakeOver, LifecycleStrings.ViewHolder(title), ShellStrings.Cancel]
            : [LifecycleStrings.ViewHolder(title), ShellStrings.Cancel];
        var choice = await dialogs.ChooseAsync(
                LifecycleStrings.OccupiedTitle,
                takeOver ? HandoverStrings.OccupiedQuestion(title) : LifecycleStrings.OccupiedMessage(title),
                options)
            .ConfigureAwait(true);
        if (takeOver && choice == 0)
        {
            await TakeOverAsync(holder, intent!, ct).ConfigureAwait(true);
        }
        else if (choice >= 0 && choice == (takeOver ? 1 : 0))
        {
            await OpenSessionAsync(holder, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The handover (6.2) of <paramref name="holder"/>'s workspace to <paramref name="intent"/>: the idle check (busy
    /// reasons listed, only "wait" and "cancel" offered, nothing queued or forced), the unsaved-buffer guard, then the
    /// "handing over" flag for the whole dialog, in which the commit / carry choice and the context mode are made and
    /// executed (<see cref="HandoverFlow"/>). On success the old runtime goes, its tab turns read-only, the session that
    /// took over opens and the trees refresh. Every failure is reported in the dialog or as a notice.
    /// </summary>
    internal async Task TakeOverAsync(SessionInfo holder, HandoverIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(intent);
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(HandoverStrings.Unavailable));
            return;
        }

        sessions.TryGetValue(holder.Id, out var holderView);
        if (await ShowBusyAsync(holder, holderView).ConfigureAwait(true))
        {
            return;
        }

        if (!await ConfirmArchiveAsync(holder.Id).ConfigureAwait(true))
        {
            return;
        }

        // The guard awaited a dialog: the session may have started something meanwhile. The UI thread owns every busy
        // source, so nothing changes between this second check and setting the flag.
        if (await ShowBusyAsync(holder, holderView).ConfigureAwait(true))
        {
            return;
        }

        handingOver.Add(holder.Id);
        if (holderView is not null)
        {
            holderView.IsHandingOver = true;
        }

        try
        {
            await RunHandoverDialogAsync(holder, holderView, intent, ct).ConfigureAwait(true);
        }
        finally
        {
            handingOver.Remove(holder.Id);
            if (holderView is not null)
            {
                holderView.IsHandingOver = false;
            }
        }
    }

    /// <summary>The idle check (6.2 step 1): when busy, lists the reasons with "wait" and "cancel" and returns true.</summary>
    private async Task<bool> ShowBusyAsync(SessionInfo holder, SessionWorkspaceViewModel? holderView)
    {
        var reasons = HandoverBusyReasons(holder, holderView);
        if (reasons.Count == 0)
        {
            return false;
        }

        await dialogs!.ConfirmAsync(
                HandoverStrings.Title,
                HandoverStrings.BusyMessage(holder.Title, reasons),
                HandoverStrings.Wait,
                ShellStrings.Cancel)
            .ConfigureAwait(true);
        return true;
    }

    private async Task RunHandoverDialogAsync(
        SessionInfo holder,
        SessionWorkspaceViewModel? holderView,
        HandoverIntent intent,
        CancellationToken ct)
    {
        var flow = composition.Handover;
        var directory = HandoverFlow.DirectoryOf(holder);
        int uncommitted;
        try
        {
            var git = composition.Git;
            uncommitted = await BackgroundRead.RunAsync(
                    async token => Directory.Exists(directory) && await git.IsRepositoryAsync(directory, token).ConfigureAwait(false)
                        ? (await git.GetStatusAsync(directory, includeUntracked: true, token).ConfigureAwait(false)).Count
                        : 0,
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await dialogs!.NotifyAsync(HandoverStrings.Title, HandoverStrings.StatusFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        HandoverAttempt? done = null;
        var model = new HandoverDialogModel(
            holder.Title,
            uncommitted,
            IsRootCheckout(holder),
            intent.IsNewSession,
            async (choice, token) =>
            {
                var attempt = await Task.Run(() => flow.ExecuteAsync(holder, intent, choice, token), CancellationToken.None)
                    .ConfigureAwait(true);
                if (!attempt.Succeeded)
                {
                    return attempt.Error;
                }

                done = attempt;
                return null;
            });
        var shown = await dialogs!.RunHandoverAsync(model, ct).ConfigureAwait(true);
        if (shown is null && done is null)
        {
            await dialogs.NotifyAsync(HandoverStrings.Title, HandoverStrings.Unavailable).ConfigureAwait(true);
            return;
        }

        if (done?.Session is not { } taker)
        {
            return;
        }

        // The old session: its runtime goes and an open tab stays as a read-only view (6.2, "the old session").
        var store = composition.Sessions;
        var oldInfo = await BackgroundRead.RunAsync(token => store.GetSessionAsync(holder.Id, token), CancellationToken.None)
            .ConfigureAwait(true);
        if (holderView is not null && oldInfo is not null)
        {
            holderView.IsHandingOver = false;
            holderView.EnterReadOnly(oldInfo);
        }
        else
        {
            composition.Runtimes.Release(holder.Id);
        }

        await OpenSessionAsync(taker, CancellationToken.None).ConfigureAwait(true);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        if (done.Warning is { } warning)
        {
            await dialogs.NotifyAsync(HandoverStrings.Title, warning).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Why <paramref name="holder"/> cannot be taken over now (section 2, idle): the busy sources of its open tab, a
    /// handover already running for it, and the workspace-level states (an operation under way, a merge conflict).
    /// </summary>
    private List<string> HandoverBusyReasons(SessionInfo holder, SessionWorkspaceViewModel? holderView)
    {
        var reasons = new List<string>(holderView?.BusyReasons() ?? []);
        if (handingOver.Contains(holder.Id) && !reasons.Contains(HandoverStrings.BusyHandingOver))
        {
            reasons.Add(HandoverStrings.BusyHandingOver);
        }

        var directory = HandoverFlow.DirectoryOf(holder);
        if (composition.WorkspaceStates.IsOperating(directory))
        {
            reasons.Add(HandoverStrings.BusyWorkspaceOperating);
        }

        if (composition.WorkspaceStates.IsConflicted(directory))
        {
            reasons.Add(HandoverStrings.BusyMergeConflict);
        }

        return reasons;
    }

    /// <summary>
    /// Archives the fork session and removes its workspace after a merge back (6.6 step 6), through the removal pipeline
    /// (7.3) with <c>baseBranch</c> = the fork's source branch; returns null on success (or a cancel), else the reason.
    /// Wired to <see cref="RemoveForkWorkspaceAsync"/>; settable so shell tests can substitute it.
    /// </summary>
    internal Func<ForkRelation, string, CancellationToken, Task<string?>>? RemoveForkWorkspace { get; set; }

    /// <summary>Hands a tree the lifecycle callbacks (6.5, 6.6) and the in-memory states its menus read (I3).</summary>
    private void WireLifecycle(ProjectTreeViewModel tree)
    {
        var states = composition.WorkspaceStates;
        tree.ForkWorkspace = ForkWorkspaceAsync;
        tree.MergeFork = MergeForkAsync;
        tree.AbortMerge = AbortMergeAsync;
        tree.IsWorkspaceBlocked = directory => states.IsOperating(directory) || states.IsConflicted(directory);
        tree.IsWorkspaceConflicted = states.IsConflicted;
        tree.IsSessionBusy = IsSessionBusy;
        WireRemoval(tree);
        WireMenu(tree);
        RemoveForkWorkspace ??= RemoveForkWorkspaceAsync;
    }

    /// <summary>The busy flags of a session's open tab and a handover running for it; in memory only.</summary>
    private bool IsSessionBusy(string sessionId) =>
        handingOver.Contains(sessionId)
        || (sessions.TryGetValue(sessionId, out var view) && view.BusyReasons().Count > 0);

    /// <summary>
    /// The parallel fork (6.5) of <paramref name="source"/>: reads the live HEAD, changes and sparse config off the UI
    /// thread (detached / unborn / not a repository refuse with a notice), then runs the fork dialog. A busy source only
    /// allows "fork anyway"; "commit first" re-checks the source right before committing and holds it in the handing-over
    /// state until the attempt ends, so no turn starts under the commit (its queued input waits, nothing is dropped). On
    /// success the new session opens and the trees refresh; the source session goes on untouched.
    /// </summary>
    internal async Task ForkWorkspaceAsync(ForkSource source, HandoverContextMode defaultMode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(ForkStrings.Unavailable));
            return;
        }

        var flow = composition.Fork;
        var preparation = await Task.Run(() => flow.PrepareAsync(source, ct), ct).ConfigureAwait(true);
        if (!preparation.CanFork)
        {
            await dialogs.NotifyAsync(ForkStrings.Title, preparation.Error!).ConfigureAwait(true);
            if (source.Session is not null)
            {
                // A stale source is the tree's: let it show the workspace's current session.
                await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            }

            return;
        }

        var owner = source.Session;
        SessionWorkspaceViewModel? ownerView = null;
        if (owner is not null)
        {
            sessions.TryGetValue(owner.Id, out ownerView);
        }

        var sourceBusy = owner is not null && HandoverBusyReasons(owner, ownerView).Count > 0;
        ForkAttempt? done = null;
        var model = new ForkDialogModel(
            preparation.Branch!,
            preparation.StartCommit!,
            owner?.Title,
            preparation.Uncommitted.Count,
            sourceBusy,
            preparation.Sparse is not null,
            ForkFlow.DefaultBranchName(composition.Worktrees.Manager.Options.BranchPrefix),
            defaultMode,
            async (choice, token) =>
            {
                // "Commit first": checked and claimed on the UI thread, which owns every busy source, so nothing can start
                // between the check and the flag; the flow's own check then only confirms it. The source workspace is
                // also "operating" for the attempt, so no merge, sync or abort starts in it under the commit.
                var holding = choice.CommitFirst && owner is not null;
                if (holding)
                {
                    if (HandoverBusyReasons(owner!, ownerView).Count > 0 || !composition.WorkspaceStates.TryBeginOperation(source.Directory))
                    {
                        return ForkStrings.BusyNow(owner!.Title);
                    }

                    NotifyTreeCommandStates();
                    handingOver.Add(owner!.Id);
                    if (ownerView is not null)
                    {
                        ownerView.IsHandingOver = true;
                    }
                }

                ForkAttempt attempt;
                try
                {
                    attempt = await Task.Run(() => flow.ExecuteAsync(source, choice, static () => true, token), CancellationToken.None)
                        .ConfigureAwait(true);
                }
                finally
                {
                    if (holding)
                    {
                        composition.WorkspaceStates.EndOperation(source.Directory);
                        NotifyTreeCommandStates();
                        handingOver.Remove(owner!.Id);
                        if (ownerView is not null)
                        {
                            ownerView.IsHandingOver = false;
                        }
                    }
                }

                if (!attempt.Succeeded)
                {
                    return attempt.Error;
                }

                done = attempt;
                return null;
            });
        var shown = await dialogs.RunForkAsync(model, ct).ConfigureAwait(true);
        if (shown is null && done is null)
        {
            await dialogs.NotifyAsync(ForkStrings.Title, ForkStrings.Unavailable).ConfigureAwait(true);
            return;
        }

        if (done?.Session is not { } fork)
        {
            return;
        }

        await OpenSessionAsync(fork, CancellationToken.None).ConfigureAwait(true);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Merge back / sync (6.6): the preview read off the UI thread (an unplannable merge is a notice; a merge back with
    /// no workspace on the source branch degrades to pushing the fork branch; nothing to merge is a notice), the target
    /// workspace's idle check, the confirmation listing the commits, then the merge with the target in the "operation in
    /// progress" state. A conflict marks the target conflicted (its session cannot start a turn) and offers to abort now
    /// or later; a merge back that went through offers to remove the fork when <see cref="RemoveForkWorkspace"/> is set.
    /// </summary>
    internal async Task MergeForkAsync(ForkRelation relation, MergeDirection direction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relation);
        var title = direction == MergeDirection.MergeBack ? ForkStrings.MergeBackTitle : ForkStrings.SyncTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(ForkStrings.Unavailable));
            return;
        }

        var flow = composition.Merge;
        var preview = await Task.Run(() => flow.PreviewAsync(relation, direction, ct), ct).ConfigureAwait(true);
        if (preview.Error is { } error)
        {
            await dialogs.NotifyAsync(title, error).ConfigureAwait(true);
            return;
        }

        if (preview.TargetMissing)
        {
            await OfferPushForkAsync(relation, preview, title, ct).ConfigureAwait(true);
            return;
        }

        if (preview.AlreadyDone)
        {
            await dialogs.NotifyAsync(
                    title,
                    direction == MergeDirection.MergeBack
                        ? ForkStrings.AlreadyMerged(preview.ForkBranch, preview.SourceBranch)
                        : ForkStrings.UpToDate(preview.ForkBranch, preview.SourceBranch))
                .ConfigureAwait(true);
            return;
        }

        var target = preview.TargetDirectory!;
        if (await ShowTargetBusyAsync(title, target).ConfigureAwait(true))
        {
            return;
        }

        var choice = await dialogs.ChooseAsync(
                title,
                MergeMessage(preview),
                [direction == MergeDirection.MergeBack ? ForkStrings.Merge : ForkStrings.Sync, ShellStrings.Cancel])
            .ConfigureAwait(true);
        if (choice != 0)
        {
            return;
        }

        // The confirmation awaited a dialog: the target may have started something meanwhile. The UI thread owns every
        // busy source, so nothing starts between this check and the operation flag.
        var states = composition.WorkspaceStates;
        if (await ShowTargetBusyAsync(title, target).ConfigureAwait(true))
        {
            return;
        }

        if (!states.TryBeginOperation(target))
        {
            await dialogs.NotifyAsync(title, ForkStrings.Busy(target, [HandoverStrings.BusyWorkspaceOperating])).ConfigureAwait(true);
            return;
        }

        // Step 3: the target's live session is held in the handing-over state for the merge (nothing can be sent or
        // queued; what is already queued stays); a tab opened meanwhile is stopped by the turn-start check.
        MergeOutcome outcome;
        var held = HoldWorkspaceSessions(target);
        NotifyTreeCommandStates();
        try
        {
            outcome = await Task.Run(() => flow.ExecuteAsync(preview), CancellationToken.None).ConfigureAwait(true);
            if (outcome.Kind == MergeOutcomeKind.Conflict)
            {
                // Step 5: the conflict state takes over from "operation in progress" and keeps blocking by itself.
                states.SetConflicted(target, true);
            }
        }
        finally
        {
            states.EndOperation(target);
            ReleaseWorkspaceSessions(held);
            NotifyTreeCommandStates();
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        switch (outcome.Kind)
        {
            case MergeOutcomeKind.Conflict:
                if (await dialogs.ChooseAsync(title, outcome.Message, [ForkStrings.MenuAbort, ForkStrings.Later]).ConfigureAwait(true) == 0)
                {
                    await AbortMergeAsync(target, CancellationToken.None).ConfigureAwait(true);
                }

                break;
            case MergeOutcomeKind.Merged when direction == MergeDirection.MergeBack && RemoveForkWorkspace is { } remove:
                if (await dialogs.ChooseAsync(title, outcome.Message, [ForkStrings.OfferRemove, ForkStrings.KeepFork]).ConfigureAwait(true) == 0)
                {
                    // baseBranch is the fork's source branch, which the fork was just merged into (6.6 step 6).
                    if (await remove(relation, preview.SourceBranch, CancellationToken.None).ConfigureAwait(true) is { } reason)
                    {
                        await dialogs.NotifyAsync(title, ForkStrings.RemoveFailed(reason)).ConfigureAwait(true);
                    }

                    await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
                }

                break;
            default:
                await dialogs.NotifyAsync(title, outcome.Message).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// <c>merge --abort</c> in a conflicted workspace (6.6 step 5), with the workspace in the "operation in progress" state
    /// while it runs; success clears the conflict flag, a failure keeps it and says why.
    /// </summary>
    internal async Task AbortMergeAsync(string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var states = composition.WorkspaceStates;
        if (!states.TryBeginOperation(directory))
        {
            await NotifyOrReportAsync(ForkStrings.MenuAbort, ForkStrings.Busy(directory, [HandoverStrings.BusyWorkspaceOperating]))
                .ConfigureAwait(true);
            return;
        }

        string? error;
        NotifyTreeCommandStates();
        try
        {
            var flow = composition.Merge;
            error = await Task.Run(() => flow.AbortAsync(directory), CancellationToken.None).ConfigureAwait(true);
            if (error is null)
            {
                states.SetConflicted(directory, false);
            }
        }
        finally
        {
            states.EndOperation(directory);
            NotifyTreeCommandStates();
        }

        await NotifyOrReportAsync(ForkStrings.MenuAbort, error ?? ForkStrings.Aborted).ConfigureAwait(true);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>Puts every live tab of the workspace into the handing-over state; returns the ones this call set.</summary>
    private List<SessionWorkspaceViewModel> HoldWorkspaceSessions(string directory)
    {
        var key = OccupancyKey.Compute(null, directory);
        var held = new List<SessionWorkspaceViewModel>();
        foreach (var session in sessions.Values)
        {
            if (!session.IsReadOnlyView
                && OccupancyKey.Compute(null, session.Info.WorktreePath ?? session.Info.WorkspacePath) == key
                && handingOver.Add(session.SessionId))
            {
                session.IsHandingOver = true;
                held.Add(session);
            }
        }

        return held;
    }

    private void ReleaseWorkspaceSessions(List<SessionWorkspaceViewModel> held)
    {
        foreach (var session in held)
        {
            handingOver.Remove(session.SessionId);
            session.IsHandingOver = false;
        }
    }

    private async Task NotifyOrReportAsync(string title, string message)
    {
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(message));
            return;
        }

        await dialogs.NotifyAsync(title, message).ConfigureAwait(true);
    }

    /// <summary>The degraded merge back (6.6): no workspace has the source branch, so only pushing the fork branch is offered.</summary>
    private async Task OfferPushForkAsync(ForkRelation relation, MergePreview preview, string title, CancellationToken ct)
    {
        var choice = await dialogs!.ChooseAsync(
                title,
                ForkStrings.TargetMissing(preview.SourceBranch, preview.ForkBranch),
                [ForkStrings.PushFork(preview.ForkBranch), ShellStrings.Cancel])
            .ConfigureAwait(true);
        if (choice != 0)
        {
            return;
        }

        var flow = composition.Merge;
        var forkBranch = preview.ForkBranch;
        var failure = await Task.Run(() => flow.PushForkAsync(relation, forkBranch, ct), CancellationToken.None).ConfigureAwait(true);
        await dialogs.NotifyAsync(title, failure ?? ForkStrings.Pushed(forkBranch)).ConfigureAwait(true);
    }

    /// <summary>
    /// The idle check of the merge target (section 2): the busy sources of the active session open in that workspace and
    /// the workspace's own states. When busy, lists the reasons and returns true; nothing is queued or forced.
    /// </summary>
    private async Task<bool> ShowTargetBusyAsync(string title, string target)
    {
        var key = OccupancyKey.Compute(null, target);
        var live = sessions.Values.FirstOrDefault(session =>
            !session.IsReadOnlyView
            && OccupancyKey.Compute(null, session.Info.WorktreePath ?? session.Info.WorkspacePath) == key);
        List<string> reasons;
        string owner;
        if (live is not null)
        {
            reasons = HandoverBusyReasons(live.Info, live);
            owner = live.Info.Title;
        }
        else
        {
            reasons = [];
            if (composition.WorkspaceStates.IsOperating(target))
            {
                reasons.Add(HandoverStrings.BusyWorkspaceOperating);
            }

            if (composition.WorkspaceStates.IsConflicted(target))
            {
                reasons.Add(HandoverStrings.BusyMergeConflict);
            }

            owner = target;
        }

        if (reasons.Count == 0)
        {
            return false;
        }

        await dialogs!.NotifyAsync(title, ForkStrings.Busy(owner, reasons)).ConfigureAwait(true);
        return true;
    }

    /// <summary>The confirmation text: what merges where, and the commits it brings in (the newest listed, the rest counted).</summary>
    internal static string MergeMessage(MergePreview preview)
    {
        var builder = new System.Text.StringBuilder(512);
        builder.Append(preview.Direction == MergeDirection.MergeBack
            ? ForkStrings.MergeBackIntro(preview.ForkBranch, preview.SourceBranch, preview.TargetDirectory ?? string.Empty)
            : ForkStrings.SyncIntro(preview.SourceBranch, preview.ForkBranch));
        if (preview.CommitCount > 0)
        {
            builder.Append("\n\n").Append(ForkStrings.Commits(preview.CommitCount));
            foreach (var commit in preview.Commits)
            {
                builder.Append('\n').Append(commit.ShortId).Append("  ").Append(commit.Subject);
            }

            if (preview.CommitCount > preview.Commits.Count)
            {
                builder.Append('\n').Append(ForkStrings.MoreCommits(preview.CommitCount - preview.Commits.Count));
            }
        }

        return builder.ToString();
    }

    /// <summary>The repository's own checkout: a project session with no worktree of its own, or whose worktree is the project root.</summary>
    private bool IsRootCheckout(SessionInfo session)
    {
        if (session.ProjectId is null)
        {
            return false;
        }

        if (session.WorktreePath is null)
        {
            return true;
        }

        return projectRoots.TryGetValue(session.ProjectId, out var root)
               && string.Equals(
                   OccupancyKey.Compute(null, root),
                   OccupancyKey.Compute(session.WorktreePath, session.WorkspacePath),
                   StringComparison.Ordinal);
    }

    private async Task RefreshTreesAfterLifecycleAsync()
    {
        // A lifecycle flow may have moved a branch, a checkout or a merge: the menus' cached git reads are stale now.
        Projects.InvalidateGitState();
        GeneralChats.InvalidateGitState();
        try
        {
            await RefreshTreesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            AppLogs.Lifecycle.ZLogWarning($"refresh failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The files an earlier permanent delete could not clean up (7.5) are cleaned up now; what still fails is a status-bar
    /// problem and is tried again at the next start.
    /// </summary>
    internal async Task RetryPurgeCleanupAsync()
    {
        var cleanup = composition.PurgeCleanup;
        if (cleanup is null)
        {
            return;
        }

        IReadOnlyList<string> failures;
        try
        {
            failures = await BackgroundRead.RunAsync(token => cleanup.RetryPendingAsync(token), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            AppLogs.Lifecycle.ZLogWarning($"the purge cleanup retry failed: {exception.Message}");
            failures = [exception.Message];
        }

        if (failures.Count > 0)
        {
            var problem = LocText.Literal(RemovalStrings.PurgeRetryFailed(string.Join("; ", failures.Distinct(StringComparer.Ordinal))));
            Status.SetWorkspaceProblem(problem);
            composition.Notifications.Notify(
                NotificationLevel.Warning,
                NotificationsStrings.StartupProblemTitle,
                problem,
                "purge-cleanup");
        }
    }

    /// <summary>
    /// Startup, after the migration and before the trees load (7.6, R6): heal sessions whose workspace folder is gone,
    /// then show every unacknowledged storage notice (the v5 de-duplication, the heal) once and acknowledge it. A shell
    /// without dialogs acknowledges nothing, so the notices wait for a window that can show them, and only notice kinds
    /// this build shows are acknowledged. A heal or notice failure is a status-bar problem (R6); the tidy-up never stops
    /// the window from opening.
    /// </summary>
    private async Task RunStartupLifecycleAsync()
    {
        var catalog = composition.Catalog;
        try
        {
            await BackgroundRead.RunAsync(
                    token => catalog.HealMissingDirectoriesAsync(Directory.Exists, Directory.Exists, token),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Lifecycle.ZLogWarning($"self-heal failed: {exception.Message}");
            Status.SetWorkspaceProblem(LifecycleStrings.StartupFailed(exception.Message));
        }

        await RetryPurgeCleanupAsync().ConfigureAwait(true);

        IReadOnlyList<StorageNotice> notices;
        try
        {
            notices = await BackgroundRead.RunAsync(token => catalog.ReadNoticesAsync(token), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Lifecycle.ZLogWarning($"notices failed: {exception.Message}");
            Status.SetWorkspaceProblem(LifecycleStrings.StartupFailed(exception.Message));
            return;
        }

        if (notices.Count == 0 || dialogs is null)
        {
            return;
        }

        // Only the kinds this build can show are acknowledged; a kind it does not know waits for a build that does.
        var lines = new List<string>();
        var shown = new List<long>();
        foreach (var notice in notices)
        {
            switch (notice.Kind)
            {
                case StorageNoticeKinds.DedupeArchived:
                    lines.Add(LifecycleStrings.DedupeArchived(notice.SessionIds.Count));
                    shown.Add(notice.Id);
                    break;
                case StorageNoticeKinds.HealDeleted:
                    lines.Add(LifecycleStrings.HealDeleted(notice.SessionIds.Count));
                    shown.Add(notice.Id);
                    break;
            }
        }

        if (lines.Count > 0)
        {
            await dialogs.NotifyAsync(LifecycleStrings.NoticeTitle, string.Join(Environment.NewLine, lines)).ConfigureAwait(true);
        }

        if (shown.Count == 0)
        {
            return;
        }

        var ids = shown.ToArray();
        try
        {
            await BackgroundRead.RunAsync(
                    async token =>
                    {
                        await catalog.AcknowledgeNoticesAsync(ids, token).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Lifecycle.ZLogWarning($"acknowledge failed: {exception.Message}");
            Status.SetWorkspaceProblem(LifecycleStrings.StartupFailed(exception.Message));
        }
    }

    /// <summary>
    /// Shows the tab of one session. A session has at most one tab: the match is by session id, so a tab left over from an
    /// earlier view model of the same session is re-bound instead of a second <c>session:</c> tab being added.
    /// </summary>
    private void ShowSessionTab(SessionWorkspaceViewModel session)
    {
        var id = WorkbenchIds.SessionTab(session.SessionId);
        if (Workbench.FindTab(id) is { } existing)
        {
            if (!ReferenceEquals(existing.Content, session))
            {
                if (existing.Content is SessionWorkspaceViewModel previous)
                {
                    previous.TabTitleChanged -= OnSessionTitleChanged;
                }

                existing.Content = session;
                session.TabTitleChanged += OnSessionTitleChanged;
            }

            existing.Title = session.TabTitle;
            Workbench.ActivateTab(existing);
            return;
        }

        Workbench.OpenTab(Workbench.MainTabs, id, TabKind.Session, session, session.TabTitle);
        session.TabTitleChanged += OnSessionTitleChanged;
    }

    private void ActivateSessionTab(string sessionId)
    {
        if (Workbench.FindTab(WorkbenchIds.SessionTab(sessionId)) is { } tab)
        {
            Workbench.ActivateTab(tab);
        }
    }

    private void CloseSessionTab(string sessionId)
    {
        if (Workbench.FindTab(WorkbenchIds.SessionTab(sessionId)) is { } tab)
        {
            Workbench.CloseTab(tab);
        }
    }

    private void OnSessionTitleChanged(object? sender, EventArgs e)
    {
        if (sender is not SessionWorkspaceViewModel session)
        {
            return;
        }

        if (Workbench.FindTab(WorkbenchIds.SessionTab(session.SessionId)) is { } tab)
        {
            tab.Title = session.TabTitle;
        }
    }

    private void OnTurnCompleted(SessionWorkspaceViewModel session)
    {
        SetTreeRunning(session.SessionId, false);
        NotifyAutomationsTurnCompleted(session);
        if (!openTabs.Contains(session.SessionId))
        {
            ReleaseClosedSession(session);
        }

        GitSync.NotifyTurnCompleted(session.SessionId);
        _ = RefreshAfterTurnAsync(session);
    }

    /// <summary>A tab closed while its turn ran keeps running; once the turn ends nothing references it any more.</summary>
    private void ReleaseClosedSession(SessionWorkspaceViewModel session)
    {
        if (session.IsRunning || !sessions.Remove(session.SessionId))
        {
            return;
        }

        UnhookAutomations(session);
        session.Dispose();
        composition.Runtimes.Release(session.SessionId);
    }

    /// <summary>
    /// An archived session leaves the tree together with its tab: the tab is closed first, so no dead tab survives the
    /// view model it shows, and a later reopen finds no duplicate <c>session:</c> tab.
    /// </summary>
    private void OnSessionArchived(string sessionId)
    {
        terminals.OnSessionArchived(sessionId);
        CloseSessionTab(sessionId);
        openTabs.Remove(sessionId);

        if (sessions.TryGetValue(sessionId, out var session) && !session.IsRunning)
        {
            sessions.Remove(sessionId);
            UnhookAutomations(session);
            session.Dispose();
        }

        composition.Runtimes.Release(sessionId);
    }

    private async Task RefreshAfterTurnAsync(SessionWorkspaceViewModel session)
    {
        try
        {
            await Files.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            if (ReferenceEquals(session, activeSession))
            {
                await Reviews.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await RefreshTreesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            AppLogs.Turn.ZLogWarning($"refresh failed: {exception.Message}");
        }
    }

    private void OnSubAgentsChanged()
    {
        SyncSubAgentRegion();
        RefreshTreeAgents();
    }

    /// <summary>
    /// Reveals the bottom group for <paramref name="session"/> - the one door every "show me this" entry uses (a sub-agent
    /// row in the tree, the new-terminal tool). It writes the session's flag and lets <see cref="SyncSubAgentRegion"/> push
    /// it onto the layout, so the panel never has a second writer; only the title bar's own restore button reports back the
    /// other way, because that button lives in the panel.
    /// </summary>
    private static void RevealBottomGroup(SessionWorkspaceViewModel? session)
    {
        if (session is { SubAgentsCollapsed: true })
        {
            session.SubAgentsCollapsed = false;
        }
    }

    private void OnAgentTreeChanged() => Dispatcher.UIThread.Post(RefreshTreeAgents);

    private void OnSubAgentToolRequested(SubAgentViewModel agent) => OpenSubAgentTab(agent);

    /// <summary>Opens (or re-binds) the bottom-group tab of one sub-agent.</summary>
    private void OpenSubAgentTab(SubAgentViewModel agent)
    {
        Workbench.OpenTab(
            Workbench.BottomTabs,
            WorkbenchIds.SubAgentTab(agent.AgentId),
            TabKind.SubAgent,
            agent,
            agent.TabTitle);
    }

    /// <summary>
    /// Brings the bottom group in line with the active session: the finished dropdown, the manual collapse state (which
    /// belongs to the session, so it survives a switch), and one tab per live sub-agent. Tabs of another session and tabs
    /// whose sub-agent is gone leave the group; a read-only tab chosen from the dropdown stays until it is closed. A
    /// minimized group keeps its whole title bar - tabs, the finished menu and the new-terminal tool included.
    /// </summary>
    private void SyncSubAgentRegion()
    {
        var session = activeSession;
        SyncFinishedMenu(session);
        if (session is null)
        {
            PruneSubAgentTabs(null, []);
            return;
        }

        // The restored layout owns the first attach: a session stores no collapse of its own, so without this the bottom group
        // would pop open on every start no matter what the snapshot said. After that the session's own choice wins, which is
        // what keeps a hand-minimized area minimized when the user switches back to it.
        if (!bottomMinimizeAdopted)
        {
            bottomMinimizeAdopted = true;
            session.SubAgentsCollapsed = Workbench.IsBottomMinimized;
        }

        Workbench.IsBottomMinimized = session.SubAgentsCollapsed;

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in session.SubAgents)
        {
            live.Add(agent.AgentId);
        }

        PruneSubAgentTabs(session, live);

        foreach (var agent in session.SubAgents)
        {
            OpenSubAgentTab(agent);
        }
    }

    private void PruneSubAgentTabs(SessionWorkspaceViewModel? session, HashSet<string> live)
    {
        var tabs = Workbench.BottomTabs.Tabs;
        for (var i = tabs.Count - 1; i >= 0; i--)
        {
            if (tabs[i].Content is not SubAgentViewModel agent)
            {
                continue;
            }

            var sameSession = session is not null &&
                              string.Equals(agent.SessionId, session.SessionId, StringComparison.Ordinal);
            if (!sameSession || (!live.Contains(agent.AgentId) && !agent.IsReadOnly))
            {
                Workbench.CloseTab(tabs[i]);
            }
        }
    }

    /// <summary>Points the bottom group's finished dropdown at the active session; without one it shows an empty menu.</summary>
    private void SyncFinishedMenu(SessionWorkspaceViewModel? session)
    {
        var menu = session?.FinishedMenu ?? emptyFinishedMenu;
        var tools = Workbench.BottomTabs.HeaderTools;
        if (tools.Count != 2 || !ReferenceEquals(tools[0], menu) || !ReferenceEquals(tools[1], newTerminalButton))
        {
            tools.Clear();
            tools.Add(menu);
            tools.Add(newTerminalButton);
        }

        WatchFinishedMenu(menu);
    }

    /// <summary>The bottom group's "new terminal" tool; rebuilt on a language switch so its tooltip follows.</summary>
    private PanelToolButton CreateNewTerminalButton() =>
        new(NewTerminalToolId, Loc.Instance[TerminalStrings.NewTerminal], "IconPlus", NewTerminalCommand);

    /// <summary>
    /// The project panel's primary tool: adding a project. It is the only way to add one - the green button that used to sit
    /// at the top of the panel body repeated this action a few pixels below it - so it is drawn green and at 24 px, larger
    /// than the outlined tools beside it.
    /// </summary>
    private PanelToolButton CreateAddProjectTool() =>
        new(AddProjectToolId, ProjectStrings.AddProjectTooltip, "IconProjectNew", Projects.AddProjectCommand)
        {
            IconSize = 20,
            IsAccent = true,
        };

    /// <summary>The project panel's refresh tool; rebuilt on a language switch so its tooltip follows.</summary>
    private PanelToolButton CreateRefreshProjectsTool() =>
        new(RefreshProjectsToolId, ProjectStrings.RefreshProjectsName, "IconRefresh", Projects.RefreshCommand);

    /// <summary>
    /// The general-chats panel's "new chat" tool. It is the panel's primary action, so it wears the same accent green and the
    /// same 24 px icon as the projects panel's add-project tool; rebuilt on a language switch so its tooltip follows.
    /// </summary>
    private PanelToolButton CreateNewGeneralSessionTool() =>
        new(NewGeneralSessionToolId, ProjectStrings.NewGeneralSessionTooltip, "IconPlus", GeneralChats.GeneralSessionCommand)
        {
            IconSize = 20,
            IsAccent = true,
        };

    /// <summary>The general-chats panel's refresh tool.</summary>
    private PanelToolButton CreateRefreshGeneralChatsTool() =>
        new(RefreshGeneralChatsToolId, ProjectStrings.RefreshProjectsName, "IconRefresh", GeneralChats.RefreshCommand);

    /// <summary>
    /// Replaces the left column's header tools in place, keeping their positions in the two title bars: a language switch
    /// rebuilds each tool because its tooltip is a resolved string.
    /// </summary>
    private void RefreshProjectTools()
    {
        var tools = Workbench.ProjectPanel.HeaderTools;
        if (addProjectTool is { } projectTool && tools.IndexOf(projectTool) is var projectIndex && projectIndex >= 0)
        {
            addProjectTool = CreateAddProjectTool();
            tools[projectIndex] = addProjectTool;
        }

        if (refreshProjectsTool is { } refreshTool && tools.IndexOf(refreshTool) is var refreshIndex && refreshIndex >= 0)
        {
            refreshProjectsTool = CreateRefreshProjectsTool();
            tools[refreshIndex] = refreshProjectsTool;
        }

        if (Workbench.GeneralPanel is not { } generalPanel)
        {
            return;
        }

        var generalTools = generalPanel.HeaderTools;
        if (newGeneralSessionTool is { } sessionTool && generalTools.IndexOf(sessionTool) is var sessionIndex && sessionIndex >= 0)
        {
            newGeneralSessionTool = CreateNewGeneralSessionTool();
            generalTools[sessionIndex] = newGeneralSessionTool;
        }

        if (refreshGeneralChatsTool is { } chatsTool && generalTools.IndexOf(chatsTool) is var chatsIndex && chatsIndex >= 0)
        {
            refreshGeneralChatsTool = CreateRefreshGeneralChatsTool();
            generalTools[chatsIndex] = refreshGeneralChatsTool;
        }
    }

    /// <summary>Opens a terminal in the active session, or says in the status bar why it cannot.</summary>
    private void OpenTerminal()
    {
        if (terminals.OpenTerminal() is null)
        {
            Status.SetWorkspaceProblem(new LocText(TerminalStrings.FailureNoSession));
        }
    }

    /// <summary>
    /// Follows the menu's item count into <see cref="TabGroupModel.KeepsVisibleWhenEmpty"/>: the bottom group stays in the
    /// layout while any finished sub-agent can still be opened from its title bar.
    /// </summary>
    private void WatchFinishedMenu(FinishedSubAgentsViewModel menu)
    {
        if (!ReferenceEquals(watchedFinishedMenu, menu))
        {
            if (watchedFinishedMenu is not null)
            {
                watchedFinishedMenu.Items.CollectionChanged -= OnFinishedItemsChanged;
            }

            watchedFinishedMenu = menu;
            menu.Items.CollectionChanged += OnFinishedItemsChanged;
        }

        UpdateBottomGroupContent(menu);
    }

    private void OnFinishedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (watchedFinishedMenu is { } menu)
        {
            UpdateBottomGroupContent(menu);
        }
    }

    private void UpdateBottomGroupContent(FinishedSubAgentsViewModel menu) =>
        Workbench.BottomTabs.KeepsVisibleWhenEmpty = menu.Items.Count > 0;

    /// <summary>
    /// The bottom group's own restore button minimizes it; the session keeps that state so it is not popped up again.
    /// </summary>
    private void OnWorkbenchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkbenchLayout.IsBottomMinimized) || activeSession is not { } session)
        {
            // The restored layout announces its own state before any session is attached; that announcement is what the
            // first attach adopts, so it must not count as the session having chosen.
            return;
        }

        bottomMinimizeAdopted = true;
        if (session.SubAgentsCollapsed != Workbench.IsBottomMinimized)
        {
            session.SubAgentsCollapsed = Workbench.IsBottomMinimized;
        }
    }

    private void OnTabClosed(object? sender, TabModel tab)
    {
        if (tab.Content is FileDocumentViewModel file)
        {
            file.PropertyChanged -= OnFileDocumentChanged;
            DetachEditorNavigation(file);
            file.DisposeReview();
            _ = file.ReleaseDocumentAsync();
        }

        if (tab.Kind == TabKind.Session && tab.Content is SessionWorkspaceViewModel session)
        {
            session.TabTitleChanged -= OnSessionTitleChanged;
            openTabs.Remove(session.SessionId);

            if (!session.IsRunning)
            {
                ReleaseClosedSession(session);
            }
        }

        ScheduleLayoutSave();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => ScheduleLayoutSave();

    private void OnActiveTabChanged(object? sender, TabModel tab)
    {
        ScheduleLayoutSave();

        // The tree mirrors what is on screen: a sub-agent tab marks its row, anything else clears the mark.
        SetTreesActiveAgent(tab.Content is SubAgentViewModel agent ? agent.AgentId : null);

        if (tab.Content is not SessionWorkspaceViewModel session)
        {
            return;
        }

        if (!ReferenceEquals(session, activeSession))
        {
            ActiveSession = session;
        }
    }

    // ---------------------------------------------------------------- panels and documents

    private void ApplyPanelContext()
    {
        var session = activeSession;
        if (session is null)
        {
            Changes.Attach(PanelContext.Empty);
            Files.Attach(PanelContext.Empty);
            Todos.Attach(null);
            Reviews.Attach(PanelContext.Empty);
            Specs.Attach(PanelContext.Empty);
            GitSync.Attach(PanelContext.Empty);
            SyncFinishedMenu(null);
            PruneSubAgentTabs(null, []);
            return;
        }

        var projectRoot = session.Info.ProjectId is { } projectId && projectRoots.TryGetValue(projectId, out var root)
            ? root
            : null;
        var context = new PanelContext(session.SessionId, session.WorkDir, projectRoot);
        Changes.Attach(context);
        Files.Attach(context);
        Todos.Attach(session.SessionId);
        Reviews.Attach(context);
        Specs.Attach(context);
        GitSync.Attach(context);
        SyncSubAgentRegion();
    }

    /// <summary>
    /// The files panel's git row feeds the composer's strip: the branch in the title row and the ahead/behind markers. The
    /// CI marker went away with the Git panel — a pull request is a conversation topic now, not a status readout.
    /// </summary>
    private void OnGitSyncPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GitSyncViewModel.BranchLabel):
                OnPropertyChanged(nameof(ActiveBranchText));
                OnPropertyChanged(nameof(HasActiveBranch));
                PushGitText();
                break;
            case nameof(GitSyncViewModel.AheadBehindText):
                Status.SetAheadBehind(GitSync.AheadBehindText);
                PushGitText();
                break;
        }
    }

    /// <summary>
    /// The description the 打开 PR dialog starts with: exactly what the agent's pr_create defaults to (the plan summary plus
    /// the generated plan), rendered from this session's plan. Nothing is sent until the dialog is confirmed.
    /// </summary>
    private async Task<string?> DescribePullRequestAsync()
    {
        if (activeSession is not { } session)
        {
            return null;
        }

        try
        {
            // Three store reads: awaited, so the UI thread is free while they run.
            var todos = composition.Todos;
            var plan = await todos.GetPlanSummaryAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            var steps = await todos.ListAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            var groups = await todos.ListGroupsAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            var body = Forge.Workspace.PrBodyRenderer.Render(plan, steps, groups);
            return body.Length > 0 ? body : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Ship.ZLogWarning($"could not render the plan for the pull request body: {exception.Message}");
            return null;
        }
    }

    /// <summary>The 提交目标 row's merge dialog, same split: the view owns the window, the row gets the answer.</summary>
    private Task<Git.PullRequestMergeChoice?> ComposePullRequestMergeAsync(Git.PullRequestMergeRequest request) =>
        dialogs is null ? Task.FromResult<Git.PullRequestMergeChoice?>(null) : dialogs.ComposePullRequestMergeAsync(request, CancellationToken.None);

    /// <summary>
    /// The row's own confirmations (a merge commit, deleting the remote branch) go through the same shell dialog as every other
    /// question, so nothing destructive happens without one modal.
    /// </summary>
    private Task<bool> ConfirmGitRowActionAsync(string title, string message) =>
        dialogs is null ? Task.FromResult(false) : dialogs.ConfirmAsync(title, message, ShellStrings.Ok, ShellStrings.Cancel);

    /// <summary>The 提交目标 row's "打开 PR…": the dialog lives in the view, the row only gets the answer.</summary>
    private Task<Git.PullRequestDraft?> ComposePullRequestAsync(Git.PullRequestDraft draft) =>
        dialogs is null ? Task.FromResult<Git.PullRequestDraft?>(null) : dialogs.ComposePullRequestAsync(draft, CancellationToken.None);

    private void OpenFile(string path, bool newTab) => _ = OpenFileAsync(path, newTab, CancellationToken.None);

    /// <summary>
    /// Opens or reuses a file tab. The two overrides are independent on purpose: a caller that knows which workspace owns the
    /// file (a stored diagnostic, a definition target) can pass it without also claiming a review owner, and a caller that
    /// knows the review owner does not have to know the workspace.
    /// </summary>
    private async Task OpenFileAsync(
        string path, bool newTab, CancellationToken ct, string? workspaceOverride = null,
        Review.ReviewOwner? reviewOwnerOverride = null, bool hasReviewOwnerOverride = false,
        bool hasWorkspaceOverride = false)
    {
        var resolved = ResolveFilePath(path);
        if (resolved is null)
        {
            return;
        }

        var id = newTab ? WorkbenchIds.PinnedFileTab(resolved) : WorkbenchIds.PreviewTab;
        var workspace = hasWorkspaceOverride ? workspaceOverride : activeSession?.WorkDir;
        if (FindFileTab(resolved) is { Content: FileDocumentViewModel same } sameTab)
        {
            if (!same.IsDirty && !same.IsSaving)
            {
                await same.LoadAsync(resolved, ct).ConfigureAwait(true);
                sameTab.Title = same.TabTitle;
            }

            Workbench.ActivateTab(sameTab);
            return;
        }

        if (Workbench.FindTab(id) is { Content: FileDocumentViewModel open } tab)
        {
            if (open.IsDirty && !await ConfirmDiscardManyAsync(() => [open]).ConfigureAwait(true))
            {
                return;
            }

            open.Workspace = workspace;
            ConfigureReview(open, reviewOwnerOverride, hasReviewOwnerOverride);
            await open.LoadAsync(resolved, ct, discardEdits: true).ConfigureAwait(true);
            tab.Title = open.TabTitle;
            Workbench.ActivateTab(tab);
            return;
        }

        var document = FileDocumentViewModel.Create(composition.Editor?.Grammars, composition.Editor?.Sessions);
        document.Workspace = workspace;
        ConfigureReview(document, reviewOwnerOverride, hasReviewOwnerOverride);
        var opened = false;
        try
        {
            await document.LoadAsync(resolved, ct).ConfigureAwait(true);
            if (Workbench.FindTab(id) is not null || FindFileTab(resolved) is not null)
            {
                return;
            }

            Workbench.OpenTab(Workbench.PreviewTabs, id, TabKind.FilePreview, document, document.TabTitle);
            document.PropertyChanged += OnFileDocumentChanged;
            AttachEditorNavigation(document);
            opened = true;
        }
        finally
        {
            if (!opened)
            {
                document.DisposeReview();
                await document.ReleaseDocumentAsync().ConfigureAwait(true);
            }
        }
    }

    private void ConfigureReview(FileDocumentViewModel document, Review.ReviewOwner? ownerOverride = null, bool hasOverride = false)
    {
        var session = activeSession;
        Review.ReviewOwner? owner = hasOverride
            ? ownerOverride
            : session is { WorkDir.Length: > 0 }
                ? new Review.ReviewOwner(session.WorkDir, session.SessionId)
                : null;
        document.ConfigureReview(CreateReviewServices(), owner);
    }

    /// <summary>File previews and the changes view share one review store, delivery path and panel refresh.</summary>
    private Review.ReviewServices CreateReviewServices() =>
        new(
            reviews,
            (sessionId, text, withdrawn) => sessions.TryGetValue(sessionId, out var target) && target.TryQueueReview(text, withdrawn),
            () => _ = RefreshReviewsAsync());

    private async Task RefreshReviewsAsync()
    {
        try
        {
            await Reviews.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            AppLogs.Review.ZLogWarning($"panel refresh failed: {exception.Message}");
        }
    }

    private TabModel? FindFileTab(string resolved)
    {
        var full = Path.GetFullPath(resolved);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var tab in Workbench.PreviewTabs.Tabs)
        {
            if (tab.Content is FileDocumentViewModel file && string.Equals(file.Path, full, comparison))
            {
                return tab;
            }
        }

        return null;
    }

    private void OnFileDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileDocumentViewModel.TabTitle) || sender is not FileDocumentViewModel file)
        {
            return;
        }

        foreach (var tab in Workbench.PreviewTabs.Tabs)
        {
            if (ReferenceEquals(tab.Content, file))
            {
                tab.Title = file.TabTitle;
            }
        }
    }

    private async Task<bool> ConfirmDiscardAsync(FileDocumentViewModel file, bool archive = false)
    {
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(new LocText(InspectorStrings.FilesUnsaved));
            return false;
        }

        var name = Path.GetFileName(file.Path);
        return await dialogs.ConfirmAsync(
                Loc.Instance[archive ? InspectorStrings.FilesArchiveTitle : InspectorStrings.FilesDiscardTitle],
                archive ? InspectorStrings.FileArchiveMessage(name) : InspectorStrings.FileDiscardMessage(name),
                Loc.Instance[archive ? InspectorStrings.FilesArchiveConfirm : InspectorStrings.FilesDiscardConfirm],
                ShellStrings.Cancel)
            .ConfigureAwait(true);
    }

    private async Task<bool> ConfirmDiscardManyAsync(Func<List<FileDocumentViewModel>> collect, bool archive = false)
    {
        for (var round = 0; round < 2; round++)
        {
            var snapshot = new List<(FileDocumentViewModel File, string Text)>();
            foreach (var file in collect())
            {
                if (file.IsSaving)
                {
                    Status.SetWorkspaceProblem(new LocText(InspectorStrings.FilesBusy));
                    return false;
                }

                if (file.IsDirty)
                {
                    snapshot.Add((file, file.EditText));
                }
            }

            if (snapshot.Count == 0)
            {
                return true;
            }

            foreach (var (file, _) in snapshot)
            {
                if (!await ConfirmDiscardAsync(file, archive).ConfigureAwait(true))
                {
                    return false;
                }
            }

            if (SnapshotStillCurrent(snapshot, collect()))
            {
                return true;
            }
        }

        Status.SetWorkspaceProblem(new LocText(InspectorStrings.FilesUnsaved));
        return false;
    }

    private static bool SnapshotStillCurrent(List<(FileDocumentViewModel File, string Text)> snapshot, List<FileDocumentViewModel> now)
    {
        foreach (var file in now)
        {
            if (!file.IsDirty)
            {
                continue;
            }

            var found = false;
            foreach (var (known, text) in snapshot)
            {
                if (ReferenceEquals(known, file))
                {
                    found = string.Equals(text, file.EditText, StringComparison.Ordinal);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private List<FileDocumentViewModel> DirtyFiles(string? underRoot)
    {
        var result = new List<FileDocumentViewModel>();
        string? prefix = null;
        if (!string.IsNullOrEmpty(underRoot))
        {
            prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(underRoot)) + Path.DirectorySeparatorChar;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var tab in Workbench.PreviewTabs.Tabs)
        {
            if (tab.Content is FileDocumentViewModel { IsDirty: true } file
                && (prefix is null || file.Path.StartsWith(prefix, comparison)))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private async Task<bool> ConfirmCloseTabAsync(TabModel tab)
    {
        return tab.Content is not FileDocumentViewModel { IsDirty: true } file
            || await ConfirmDiscardManyAsync(() => [file]).ConfigureAwait(true);
    }

    private Task<bool> ConfirmArchiveAsync(string sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || string.IsNullOrEmpty(session.WorkDir))
        {
            return Task.FromResult(true);
        }

        var root = session.WorkDir;
        return ConfirmDiscardManyAsync(() => DirtyFiles(root), archive: true);
    }
    public bool HasUnsavedFiles
    {
        get
        {
            foreach (var tab in Workbench.PreviewTabs.Tabs)
            {
                if (tab.Content is FileDocumentViewModel { IsDirty: true })
                {
                    return true;
                }
            }

            return false;
        }
    }

    public Task<bool> ConfirmCloseWindowAsync() => ConfirmDiscardManyAsync(() => DirtyFiles(null));

    /// <summary>
    /// File-panel rows carry workspace-relative paths, while spec-tree rows and restored snapshots are already absolute.
    /// A relative path is resolved against the active session's working directory so the document never depends on the
    /// process working directory. Without an active session there is nothing to resolve against: the open is skipped with
    /// a status-bar hint instead of throwing.
    /// </summary>
    private string? ResolveFilePath(string path)
    {
        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        if (activeSession is null || activeSession.WorkDir.Length == 0)
        {
            Status.SetWorkspaceProblem(new LocText(ShellStrings.NoOpenSessionKey));
            return null;
        }

        return Path.GetFullPath(Path.Combine(activeSession.WorkDir, path));
    }

    private void ShowChangesAsync(string? path) => _ = ShowChangesCoreAsync(path);

    /// <summary>Opens the fixed changes tab of the preview group and optionally jumps to one file's diff.</summary>
    private async Task ShowChangesCoreAsync(string? path)
    {
        ShowChangesTab(activate: true);
        if (path is null)
        {
            await Changes.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        await Changes.ShowFileAsync(path, CancellationToken.None).ConfigureAwait(true);
    }

    private void ShowChangesTab(bool activate)
    {
        Workbench.OpenTab(
            Workbench.PreviewTabs,
            WorkbenchIds.ChangesTab,
            TabKind.Changes,
            Changes,
            ShellStrings.ChangesTitle,
            activate: activate,
            closable: false);
    }

    // ---------------------------------------------------------------- commands

    private async Task AddProjectAsync(CancellationToken ct)
    {
        if (dialogs is null)
        {
            return;
        }

        var folder = await dialogs.PickFolderAsync(ShellStrings.PickRepositoryTitle).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var isRepository = await Task.Run(() => composition.Git.IsRepositoryAsync(folder, ct), ct).ConfigureAwait(true);
        if (!isRepository)
        {
            var confirmed = await dialogs.ConfirmAsync(
                    ShellStrings.NotRepositoryTitle,
                    ShellStrings.NotRepositoryMessage(folder),
                    ShellStrings.GitInit,
                    ShellStrings.Cancel)
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            var init = await Task.Run(() => composition.Git.RunAsync(folder, ["init"], ct), ct).ConfigureAwait(true);
            if (!init.Success)
            {
                Status.SetWorkspaceProblem(ShellStrings.GitInitFailed(init.StandardError.Trim()));
                return;
            }
        }

        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var registry = composition.Projects;
        await BackgroundRead.RunAsync(token => registry.AddAsync(folder, name, ProjectKind.Repository, token), ct)
            .ConfigureAwait(true);
        RefreshProjectCacheAsync();
        await RefreshTreesAsync(ct).ConfigureAwait(true);
    }

    private async Task<bool> ConfirmAndCreateAsync(Func<CancellationToken, Task<SessionInfo>> factory)
    {
        if (dialogs is null)
        {
            return false;
        }

        var confirmed = await dialogs.ConfirmAsync(ShellStrings.NewSessionTitle, ShellStrings.NewSessionQuestion, ShellStrings.Create, ShellStrings.Cancel)
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }

        try
        {
            var session = await factory(CancellationToken.None).ConfigureAwait(true);
            await OpenSessionAsync(session, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (SessionCreateOccupiedException occupied)
        {
            await ShowOccupiedAsync(occupied.Occupied, HandoverIntent.NewSession(occupied.Create), CancellationToken.None)
                .ConfigureAwait(true);
            return false;
        }
        catch (WorktreeOccupiedException occupied)
        {
            await ShowOccupiedAsync(occupied, null, CancellationToken.None).ConfigureAwait(true);
            return false;
        }
        catch (Exception exception) when (exception is GitException or InvalidOperationException or IOException or ArgumentException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(exception.Message));
            return false;
        }
    }

    private async Task<string?> PromptTextAsync(string title, string initial) =>
        dialogs is null ? null : await dialogs.PromptTextAsync(title, initial).ConfigureAwait(true);

    /// <summary>Opens the new-branch dialog; a shell without dialogs (a headless host) creates nothing.</summary>
    private async Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) =>
        dialogs is null ? null : await dialogs.PickNewBranchAsync(request, ct).ConfigureAwait(true);

    /// <summary>
    /// Approval cards waiting for one session. The queue belongs to the UI thread, so this is only ever read from a view model
    /// that lives there too; a plugin approval card carries no session and counts for none.
    /// </summary>
    private static int CountPendingApprovals(string sessionId)
    {
        if (sessionId.Length == 0 || ApprovalServices.Current is not { } approvals)
        {
            return 0;
        }

        var pending = 0;
        foreach (var card in approvals.Queue.Items)
        {
            if (string.Equals(card.SessionId, sessionId, StringComparison.Ordinal))
            {
                pending++;
            }
        }

        return pending;
    }

    /// <summary>
    /// Opens the settings page on one section: the click target of the strip's four counters. The window is created on
    /// demand and reused; selecting the section before showing it means the window opens on the right page.
    /// </summary>
    private void OpenSettingsSection(string sectionId)
    {
        Settings.SelectSection(sectionId);
        var window = ShowSettingsWindow();
        window.Activate();
    }

    /// <summary>The gear button: closes the window when it is open, otherwise opens (or re-focuses) it.</summary>
    private void ToggleSettings()
    {
        if (settingsWindow is { } open && open.IsVisible)
        {
            open.Close();
            return;
        }

        ShowSettingsWindow();
    }

    /// <summary>
    /// Creates the settings window unless it already exists, then shows it. Both entry points go through here so there is
    /// only ever one, and closing it flips <see cref="SettingsViewModel.IsOpen"/> back in one place.
    /// </summary>
    private SettingsWindow ShowSettingsWindow()
    {
        if (settingsWindow is { } existing)
        {
            if (!existing.IsVisible)
            {
                if (SettingsOwner?.Invoke() is { } existingOwner)
                {
                    existing.Show(existingOwner);
                }
                else
                {
                    existing.Show();
                }
            }

            return existing;
        }

        var window = new SettingsWindow(Settings);
        settingsWindow = window;
        window.Closed += OnSettingsWindowClosed;
        var owner = SettingsOwner?.Invoke();
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        return window;
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (settingsWindow is { } window)
        {
            window.Closed -= OnSettingsWindowClosed;
        }

        settingsWindow = null;
        Settings.IsOpen = false;
    }

    /// <summary>Cards come and go on their own schedule, so the active strip re-reads its approval count on every change.</summary>
    private void OnApprovalQueueChanged() => activeSession?.Instruments.RefreshCounters();

    /// <summary>Gives one session's strip the project it belongs to, from the cache the registry read fills.</summary>
    private void ApplyProject(SessionWorkspaceViewModel session)
    {
        var projectId = session.Info.ProjectId;
        var name = projectId is { } named && projectNames.TryGetValue(named, out var foundName) ? foundName : null;
        var root = projectId is { } rooted && projectRoots.TryGetValue(rooted, out var foundRoot) ? foundRoot : null;
        session.Instruments.SetProject(name, root);
    }

    /// <summary>Hands the active session's strip the Git panel's live branch, ahead/behind and CI marker.</summary>
    private void PushGitText() =>
        activeSession?.Instruments.SetGit(GitSync.AheadBehindText);

    private static Task RevealAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return Task.CompletedTask;
    }

    private void OnCommandFailed(Exception exception) => Status.SetWorkspaceProblem(LocText.Literal(exception.Message));

    private void OnClockTick(object? sender, EventArgs e)
    {
        foreach (var session in sessions.Values)
        {
            session.Tick();
        }

        foreach (var session in sessions.Values)
        {
            SetTreeRunning(session.SessionId, session.IsRunning);
        }

        TickAutomationBanners();
    }

    private void OnHostStatusChanged(HostStatus status, LocText? detail) =>
        Dispatcher.UIThread.Post(() =>
        {
            Status.SetHost(status, detail);
            Status.SetHostProcess(composition.Host?.HostProcessId);
            if (status == HostStatus.Running)
            {
                RefreshInventory();
            }
        });

    private void OnHostSurfaceChanged() => Dispatcher.UIThread.Post(RefreshInventory);

    /// <summary>
    /// Only the texts this view model computes have to be re-raised: the workbench titles belong to
    /// <see cref="WorkbenchLayout"/>, the status bar to <see cref="StatusBarViewModel"/> and the settings page to
    /// <see cref="SettingsViewModel"/>, each of which listens on its own.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(ActiveSessionTitle));
        OnPropertyChanged(nameof(ProviderNotice));
        RefreshOpenChangesTool();
        RefreshProjectTools();

        // The bottom group's "new terminal" tool carries its tooltip as a plain string too; rebuild it and re-sync the row.
        newTerminalButton = CreateNewTerminalButton();
        SyncFinishedMenu(activeSession);
        OnPropertyChanged(nameof(TerminalButtonText));
    }

    /// <summary>
    /// The open-changes header tool carries its tooltip as a plain string on the model, so a language switch replaces
    /// that one item in place; the collection is a view model the panel binds to, not a control built here.
    /// </summary>
    private void RefreshOpenChangesTool()
    {
        if (openChangesTool is not { } existing)
        {
            return;
        }

        var tools = Workbench.FilesPanel.HeaderTools;
        var index = tools.IndexOf(existing);
        if (index < 0)
        {
            return;
        }

        openChangesTool = new PanelToolButton(
            OpenChangesToolId,
            ShellStrings.OpenChangesTooltip,
            "IconChangesDocument",
            ShowChangesCommand);
        tools[index] = openChangesTool;
    }

    private void RefreshInventory()
    {
        if (composition.Host is not { } host)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                var inventory = await host.ReadInventoryAsync(CancellationToken.None).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    Status.SetInventory(inventory);
                    composition.Inventory = inventory;
                });
            });
    }

    private void ReloadSettings()
    {
        var provider = composition.ReloadProvider();
        OnPropertyChanged(nameof(ProviderNotice));
        OnPropertyChanged(nameof(ShowProviderNotice));
        Status.ApplyProvider(provider);
        Settings.Rebuild();
        _ = Status.RefreshApprovalsAsync();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        closing = true;
        if (settingsWindow is { } window)
        {
            window.Closed -= OnSettingsWindowClosed;
            window.Close();
            settingsWindow = null;
        }

        clock.Stop();
        layoutSaveTimer.Stop();
        CloseChecks();

        // Terminals are ended before the final layout capture, so no terminal tab can end up in a snapshot, and before the
        // composition root tears the extension host down.
        ShutdownTerminals();
        SaveLayoutOnShutdown();

        disposed = true;

        if (composition.Host is { } host)
        {
            host.StatusChanged -= OnHostStatusChanged;
            host.SurfaceChanged -= OnHostSurfaceChanged;
        }

        if (ApprovalServices.Current is { } approvalServices)
        {
            approvalServices.Queue.Changed -= OnApprovalQueueChanged;
        }

        DetachAutomations();
        foreach (var session in sessions.Values)
        {
            UnhookAutomations(session);
            session.Dispose();
        }

        sessions.Clear();
        openTabs.Clear();

        if (watchedFinishedMenu is not null)
        {
            watchedFinishedMenu.Items.CollectionChanged -= OnFinishedItemsChanged;
            watchedFinishedMenu = null;
        }

        // Every runtime is released before the composition root tears its stores down.
        composition.Runtimes.Dispose();

        Workbench.TabClosed -= OnTabClosed;
        Workbench.ContentCloseGuard = null;
        Workbench.ActiveTabChanged -= OnActiveTabChanged;
        Workbench.LayoutChanged -= OnLayoutChanged;
        Workbench.PropertyChanged -= OnWorkbenchPropertyChanged;
        GitSync.PropertyChanged -= OnGitSyncPropertyChanged;
        GitSync.Dispose();

        // The long-lived owners subscribe to the string table themselves; they drop it with the window.
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Status.Dispose();
        Settings.Dispose();
        Projects.Dispose();
        GeneralChats.Dispose();
        Files.Dispose();
        Changes.Dispose();
        Todos.Dispose();
        Reviews.Dispose();
        Specs.Dispose();
        ClosePlan();
        PropertyChanged -= OnPlanActiveSessionChanged;
        Problems.Dispose();
        problemsDiagnostics?.Dispose();
        Workbench.Dispose();

        // The final layout capture and the disposals above can still raise a layout change, which restarts the save
        // timer. Stop() only disables a DispatcherTimer: the Dispatcher drops it from its list on its next pass, so a
        // dispatcher that is not pumped any more (a closed headless session) would keep the entry, its Tick handler,
        // this view model and through the dialogs owner the whole window alive. Unhooking Tick ends that reference.
        clock.Stop();
        layoutSaveTimer.Stop();
        clock.Tick -= OnClockTick;
        layoutSaveTimer.Tick -= OnLayoutSaveTick;
    }

    /// <summary>
    /// Ends every terminal with a bounded wait. The extension host is stopped right after this, so the wait is only about
    /// letting the graceful <c>terminal.stop</c> finish; a host that does not answer cannot hold the window open.
    /// </summary>
    private void ShutdownTerminals()
    {
        try
        {
            if (!terminals.DisposeAsync().AsTask().Wait(TerminalShutdownBudget))
            {
                AppLogs.Terminal.ZLogWarning($"shutdown timed out");
            }
        }
        catch (AggregateException exception)
        {
            AppLogs.Terminal.ZLogError(exception, $"shutdown failed");
        }
    }

    /// <summary>
    /// Told by the window before it closes: from here on a floating window keeps its place in the layout snapshot, so the
    /// final capture still sees the tab that was floating rather than one the layout already docked back.
    /// </summary>
    public void PrepareForShutdown() => Workbench.IsShuttingDown = true;

    /// <summary>Number of tabs currently open; used by the status bar.</summary>
    public int OpenTabCount => openTabs.Count;

    public string LayoutSummary =>
        (activeSession is null ? ShellStrings.NoOpenSession : activeSession.WorkDir) + " · " +
        openTabs.Count.ToString(CultureInfo.InvariantCulture) + " tabs";

    public IReadOnlyCollection<string> OpenTabIds => openTabs;
}
