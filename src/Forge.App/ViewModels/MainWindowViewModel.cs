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
    /// cancelled and nothing is created. A host that cannot show it (a recording double) answers null, so
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

    /// <summary>
    /// An information message with a single OK button. The host builds it itself: a notice is not a question, so it must not
    /// borrow the confirm dialog and end up with two buttons carrying the same label.
    /// </summary>
    Task NotifyAsync(string title, string message);

    /// <summary>
    /// A question with several buttons; the last option is the cancel button. Returns the chosen index, -1 when the
    /// dialog was closed. The default offers the first and the last option only.
    /// </summary>
    async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options) =>
        await ConfirmAsync(title, message, options[0], options[^1]).ConfigureAwait(true) ? 0 : -1;

    /// <summary>
    /// The add-project dialog (wave W9): a clone or a brand-new repository, described and run by
    /// <paramref name="model"/>. Answers the created directory, or null when the user cancelled and nothing was created. A
    /// host that cannot show it (a recording double) answers null, so no project appears that nobody asked
    /// for.
    /// </summary>
    Task<string?> RunProjectCreationAsync(ProjectCreationViewModel model, CancellationToken ct) =>
        Task.FromResult<string?>(null);

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

    /// <summary>Automation id (and header tool id) of the project panel's refresh button.</summary>
    private const string RefreshProjectsToolId = "refresh-projects";

    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>Upper bound of the shutdown wait for the terminals; the coordinator itself stops waiting after four.</summary>
    private static readonly TimeSpan TerminalShutdownBudget = TimeSpan.FromSeconds(5);

    private readonly AppComposition composition;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private readonly LayoutStateLoader layoutState;

    /// <summary>Where each session's chip selection is kept between runs; one store for the whole window.</summary>
    private readonly SessionComposerStore composerSelections;
    private readonly Dictionary<string, SessionWorkspaceViewModel> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> handingOver = new(StringComparer.Ordinal);
    private readonly HashSet<string> openTabs = new(StringComparer.Ordinal);

    /// <summary>
    /// Sub-agent tabs the shell itself closed while syncing the roster. Their close is bookkeeping, not the user ending a
    /// topic, so <see cref="OnTabClosed"/> must not retire the child out of the roster because of it.
    /// </summary>
    private readonly HashSet<SubAgentViewModel> prunedSubAgentTabs = [];
    private readonly FinishedSubAgentsViewModel emptyFinishedMenu = new();
    private readonly Dictionary<string, string> projectRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> projectNames = new(StringComparer.Ordinal);

    private readonly SessionInstrumentSources instruments;
    private readonly DispatcherTimer clock;
    private readonly DispatcherTimer layoutSaveTimer;
    private readonly TerminalWorkbench terminals;
    private readonly QuestionWorkbench? questionPanels;
    private readonly WorkspaceDiagnostics? problemsDiagnostics;
    private PanelToolButton newTerminalButton;
    private ReviewStore reviews;

    /// <summary>
    /// The one review wiring every preview and the changes view deliver through; built on first use so a tab switch can
    /// re-resolve a document's owner without allocating a new one (the controller compares it by reference).
    /// </summary>
    private Review.ReviewServices? reviewServices;
    private PanelToolButton? openChangesTool;
    private PanelToolButton? addProjectTool;
    private PanelToolButton? refreshProjectsTool;
    private PanelToolButton? newGeneralSessionTool;
    private SessionWorkspaceViewModel? activeSession;
    private FinishedSubAgentsViewModel? watchedFinishedMenu;

    /// <summary>The session whose menu <see cref="watchedFinishedMenu"/> is; its live roster keeps the group's title bar alive.</summary>
    private SessionWorkspaceViewModel? watchedMenuSession;
    private IDialogService? dialogs;

    /// <summary>Set once the restored layout's bottom-group state has been handed to the first attached session.</summary>
    private bool bottomMinimizeAdopted;

    /// <summary>The session whose wish the bottom group currently serves; a change of it ends a hand-opened empty body.</summary>
    private SessionWorkspaceViewModel? bottomPointedAt;
    private bool disposed;
    private SettingsWindow? settingsWindow;
    private bool closing;

    public MainWindowViewModel(AppComposition composition)
    {
        this.composition = composition;
        // The session hosts read this when a message_session delivery happens; installing it before any runtime exists means
        // the very first host already has the seam.
        InitializeSessionMessages();
        layoutState = new LayoutStateLoader(composition.UiState);
        composerSelections = new SessionComposerStore(composition.UiState);
        reviews = composition.Workspace?.Reviews ?? new ReviewStore(composition.Paths.ReviewsDirectory);

        Status = new StatusBarViewModel(composition);
        Settings = new SettingsViewModel(composition);
        instruments = new SessionInstrumentSources(composition);
        composition.ActiveSessionTools = () => activeSession?.ToolNames() ?? [];
        composition.ActiveSessionToolCatalog = () => activeSession?.DescribeTools() ?? [];
        composition.ActiveSessionWorkspace = () => activeSession?.WorkDir;
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
        Projects.SessionPromoted = OnSessionPromoted;
        Projects.ArchiveGuard = ConfirmArchiveAsync;
        Projects.HasVsCode = instruments.HasVsCode;
        Projects.OpenInVsCode = instruments.OpenInVsCode;
        Projects.NewAgentSession = OpenAgentSessionAsync;
        // The tree menu's kinds come from the same store the session hosts read, models included, so a kind row can name the
        // model its session will run on.
        Projects.AgentKinds = workspacePath =>
            AgentCatalogue.Resolve(composition.PromptTemplates.Resolve(workspacePath), composition.AgentDefaults);
        Projects.OpenAgentSession = OpenAgentSessionByIdAsync;
        Projects.IsAgentSessionUnread = IsAgentSessionUnread;
        Projects.SessionRenamed = OnSessionRenamed;
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
            SessionPromoted = OnSessionPromoted,
            ArchiveGuard = ConfirmArchiveAsync,
            HasVsCode = instruments.HasVsCode,
            OpenInVsCode = instruments.OpenInVsCode,
            OpenAgentSession = OpenAgentSessionByIdAsync,
            IsAgentSessionUnread = IsAgentSessionUnread,
            SessionRenamed = OnSessionRenamed,
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
            gitSync: GitSync,
            reveal: RevealAsync,
            trash: (path, ct) => composition.Trash.MoveToTrashAsync(path, ct));
        Services.EditorFileOpener.Handler = path => OpenFile(path, newTab: false);
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
        if (ApprovalServices.Current is { } questionServices)
        {
            questionPanels = new QuestionWorkbench(Workbench, questionServices.Queue, () => RevealBottomGroup(activeSession));
        }

        newTerminalButton = CreateNewTerminalButton();

        GitSync.PropertyChanged += OnGitSyncPropertyChanged;
        openChangesTool = new PanelToolButton(
            OpenChangesToolId,
            ShellStrings.OpenChangesTooltip,
            "IconChangesDocument",
            ShowChangesCommand);
        Workbench.FilesPanel.HeaderTools.Add(openChangesTool);
        // Each left-column panel keeps its own tools in its title bar: the panel chrome owns the bar, so a toolbar inside the
        // body would read as a second, broken title row. General chats add a chat, projects add a project and refresh the tree.
        addProjectTool = CreateAddProjectTool();
        refreshProjectsTool = CreateRefreshProjectsTool();
        newGeneralSessionTool = CreateNewGeneralSessionTool();
        Workbench.ProjectPanel.HeaderTools.Add(addProjectTool);
        Workbench.ProjectPanel.HeaderTools.Add(refreshProjectsTool);
        if (Workbench.GeneralPanel is { } generalPanel)
        {
            generalPanel.HeaderTools.Add(newGeneralSessionTool);
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

        // The resource inspector's panel and the row of the strip's resources menu that opens it (wave W8). Created before
        // the window binds anything, because the sessions built below are handed OpenInspector as a callback.
        InitializeInspector();

        // The plan panel's view model and the command that opens it (wave W7). Created before the window binds, so the
        // overlay in MainWindow.axaml has its data context from the first frame.
        InitializePlan();
        InitializePrompts();

        // The palette's view model and its three opening gestures. Created here, before the window binds, so the overlay
        // in MainWindow.axaml has its data context from the first frame.
        InitializePalette();

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
                // A tab restored at start-up replays its history here, on the first time it is shown, rather than during
                // restore (SessionWorkspaceViewModel.EnsureHistoryAsync never throws and runs once per tab).
                if (value is { } shown)
                {
                    _ = shown.EnsureHistoryAsync(CancellationToken.None);
                }

                CloseChecks();
                SetTreesActiveSession(activeSession?.SessionId);
                OnPropertyChanged(nameof(HasActiveSession));
                OnPropertyChanged(nameof(ActiveSessionTitle));
                ApplyPanelContext();
                terminals.OnActiveSessionChanged(activeSession?.SessionId, activeSession?.WorkDir);
                questionPanels?.OnActiveSessionChanged(activeSession?.SessionId);
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
            await (InitializationStep ?? (() => composition.Initialization))().ConfigureAwait(true);
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
    private async Task RefreshTreesAsync(CancellationToken ct, bool reuseStaleGitReads = false, bool refreshConflicts = true)
    {
        await GeneralChats.RefreshAsync(ct, reuseStaleGitReads).ConfigureAwait(true);
        await Projects.RefreshAsync(ct, reuseStaleGitReads).ConfigureAwait(true);
        if (refreshConflicts)
        {
            await RefreshConflictSnapshotAsync(ct).ConfigureAwait(true);
        }
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
    /// <para>The session always comes first: with its tab already open it is brought forward without touching the store, and
    /// with the tab closed (or the view not built yet) it is opened again - a click that only lit the agent's row while the
    /// main region stayed on another session left the tree marking a row the user could not see.</para>
    /// </summary>
    private async Task OpenSubAgentTabAsync(string sessionId, string agentId, CancellationToken ct)
    {
        if (sessions.ContainsKey(sessionId) && Workbench.FindTab(WorkbenchIds.SessionTab(sessionId)) is { } tab)
        {
            Workbench.ActivateTab(tab);
        }
        else
        {
            await OpenSessionByIdAsync(sessionId, ct).ConfigureAwait(true);
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

    /// <summary>
    /// The project-list read behind the tree's id→root cache; settable so it can be made to fail the way a store
    /// that cannot answer does, and pin the announcement of that failure. Null reads through the composition.
    /// </summary>
    internal Func<CancellationToken, Task<IReadOnlyList<ProjectInfo>>>? ProjectListRead { get; set; }

    /// <summary>
    /// The initialization step the shell awaits on startup; settable so a fault can be injected the way the database
    /// catch in <see cref="InitializeAsync"/> does not handle, exercising the shell's outer startup-failed fallback
    /// (MainWindow.InitializeAsync). Null awaits the composition's own initialization.
    /// </summary>
    internal Func<Task>? InitializationStep { get; set; }

    private void RefreshProjectCacheAsync() =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var projects = await (ProjectListRead ?? composition.Projects.ListAsync)(CancellationToken.None)
                        .ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        projectRoots.Clear();
                        projectNames.Clear();
                        foreach (var project in projects)
                        {
                            projectRoots[project.Id] = project.RootPath;
                            projectNames[project.Id] = project.Name;
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

        // Every remembered tab first, in one UI pass: the rows are read concurrently and the tabs are then built without an
        // await between them, so the strip appears at once. Only the stored active session replays its history (its
        // activation does that); the rest defer it to their first activation.
        var order = new List<string>(tabs.Sessions.Count);
        if (tabs.ActiveSession is { } activeFirst && tabs.Sessions.Contains(activeFirst))
        {
            order.Add(activeFirst);
        }

        foreach (var id in tabs.Sessions)
        {
            if (!string.Equals(id, tabs.ActiveSession, StringComparison.Ordinal))
            {
                order.Add(id);
            }
        }

        await RestoreSessionTabsAsync(order, tabs.ActiveSession).ConfigureAwait(true);

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

        // The stored active session was restored (and activated) with the batch above; the preview and the floating windows
        // then take the foreground in the same order they were saved in.
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

    internal Task OpenSessionByIdAsync(string sessionId, CancellationToken ct) =>
        OpenSessionByIdAsync(sessionId, activate: true, ct);

    /// <summary>
    /// Opens the session by the role the store holds for it. <paramref name="activate"/> false brings it on screen without
    /// taking the focus: a role that moved behind a background tab is repaired in place, not brought forward.
    /// </summary>
    internal Task OpenSessionByIdAsync(string sessionId, bool activate, CancellationToken ct) =>
        OpenSessionByIdCoreAsync(sessionId, activate, ct, deferHistory: false, index: null);

    private async Task OpenSessionByIdCoreAsync(string sessionId, bool activate, CancellationToken ct, bool deferHistory, int? index)
    {
        var sessions = composition.Sessions;
        var info = await BackgroundRead.RunAsync(token => sessions.GetSessionAsync(sessionId, token), ct)
            .ConfigureAwait(true);
        if (info is null)
        {
            return;
        }

        if (info.ParentSessionId is not null)
        {
            // A child session the user opened under a root (an agent session) comes back as its own tab; a model-spawned
            // sub-agent has no tab of its own and is shown inside its root's bottom group instead.
            await OpenAgentSessionByIdAsync(info.Id, activate, ct).ConfigureAwait(true);
            return;
        }

        // Opening never changes a session's status (6.3): a session that is not Active opens as a read-only view, and
        // only "continue" (ActivateAsync, which honours the one-active-session-per-workspace rule) makes it Active again.
        await OpenSessionAsync(info, activate, ct, deferHistory, index).ConfigureAwait(true);
    }

    private async Task OpenSessionAsync(SessionInfo info, CancellationToken ct, bool deferHistory = false, int? index = null) =>
        await OpenSessionAsync(info, activate: true, ct, deferHistory, index).ConfigureAwait(true);

    private async Task OpenSessionAsync(SessionInfo info, bool activate, CancellationToken ct, bool deferHistory = false, int? index = null)
    {
        ComposerCarry? carried = null;
        if (sessions.TryGetValue(info.Id, out var existing))
        {
            // A view built for another role (a demoted root's root view, a promoted session's agent view) is stale from the
            // moment the store moved the row: it runs the wrong depth, the wrong tool surface and the wrong turn gate, so it
            // never goes back on screen as it is. A turn in flight is the one exception - it ends on the instance it started
            // with, and the row the gate checks sends the tab read-only at its next use.
            var action = existing.IsRunning
                ? SessionRoleAction.Keep
                : SessionRoleRules.ActionFor(info, existing.IsAgentSession, existing.IsReadOnlyView, existing.AgentSessionRoot?.Id);
            if (action == SessionRoleAction.Keep)
            {
                ShowSessionTab(existing, activate);
                openTabs.Add(info.Id);
                if (activate)
                {
                    ActiveSession = existing;
                }

                ScheduleLayoutSave();
                return;
            }

            if (action == SessionRoleAction.ReadOnly)
            {
                // The row left Active behind the tab's back: the tab becomes the read-only history view of the row it is now.
                existing.EnterReadOnly(info);
                ShowSessionTab(existing, activate);
                openTabs.Add(info.Id);
                if (activate)
                {
                    ActiveSession = existing;
                }

                ScheduleLayoutSave();
                return;
            }

            if (!TryRetireView(existing, out carried))
            {
                ShowSessionTab(existing, activate);
                openTabs.Add(info.Id);
                ScheduleLayoutSave();
                return;
            }
        }

        var readOnly = info.Status != SessionStatus.Active;
        var session = BuildSession(info, readOnly, carried);

        // A remembered read-only tab keeps its history in the store until it is first shown (SessionWorkspaceViewModel
        // .EnsureHistoryAsync). A tab that can run turns never defers: a live turn would append rows under the replay.
        var deferred = deferHistory && readOnly;

        if (deferred)
        {
            // The tab exists and is titled from its stored row; the transcript stays empty until it is first shown.
            ShowSessionTab(session, activate: false, index);
            openTabs.Add(info.Id);
            if (activeSession is null)
            {
                ActiveSession = session;
            }

            ScheduleLayoutSave();
            return;
        }

        await session.LoadHistoryAsync(ct).ConfigureAwait(true);
        ShowSessionTab(session, activate, index);
        openTabs.Add(info.Id);
        if (activate || activeSession is null)
        {
            ActiveSession = session;
        }

        ScheduleLayoutSave();
    }

    /// <summary>
    /// Takes an open view out of its tab and out of every table this window keeps, and hands back the draft and queue it
    /// held so a view built for the row can take them over. A view is retired when the role, the root or the row's status
    /// moved under it: its runtime, its turn gate and its tool surface were all chosen for the answer that no longer holds.
    /// <para>False when a turn is still running in a root view: a closed root tab stops nothing in flight and is released at
    /// its turn's end, so it is left where that release can find it (the tab it showed is then already showing the new
    /// role, and the old view finishes out of sight).</para>
    /// </summary>
    private bool TryRetireView(SessionWorkspaceViewModel view, out ComposerCarry? carried)
    {
        ArgumentNullException.ThrowIfNull(view);
        carried = null;
        if (view.IsRunning && !view.IsAgentSession)
        {
            // A running root view keeps its tab: closing it is what lets its turn end release it (ReleaseClosedSession reads
            // `openTabs`), so it is not taken out of the tables now. An agent view can move on - its turn ends in the
            // background exactly as a closed agent tab's does (agentViewsClosing).
            return false;
        }

        carried = view.TakeComposerState();
        var wasActive = ReferenceEquals(activeSession, view);
        if (view.IsAgentSession && agentViews.TryGetValue(view, out var agentTab))
        {
            // The tab stays; the shell's agent bookkeeping, the grants naming it and (when nothing else of that root needs
            // it) the root's runtime all go with the view, exactly as a closed tab takes them.
            DetachAgentSessionTab(agentTab);
            if (wasActive)
            {
                ActiveSession = null;
            }

            return true;
        }

        sessions.Remove(view.SessionId);
        openTabs.Remove(view.SessionId);
        view.BecameReadOnly -= OnSessionBecameReadOnly;
        view.TurnCompleted -= OnTurnCompleted;
        view.SubAgentsChanged -= OnSubAgentsChanged;
        view.AgentTreeChanged -= OnAgentTreeChanged;
        view.TabTitleChanged -= OnSessionTitleChanged;
        UnhookAutomations(view);
        view.Dispose();
        if (wasActive)
        {
            ActiveSession = null;
        }
        if (!HasAgentSessionsUsingRuntime(view.SessionId))
        {
            // A root tab runs on its session's own runtime; the role it takes next either brings a fresh host (promoted)
            // or runs on another root's (demoted).
            composition.Runtimes.Release(view.SessionId);
        }

        return true;
    }

    /// <summary>
    /// The view model of one session tab plus the handlers the shell owns, registered where every opener needs them. Reading
    /// the history is the caller's business: restore builds every tab in one UI pass and only lets the one on screen replay it.
    /// </summary>
    private SessionWorkspaceViewModel BuildSession(SessionInfo info, bool readOnly, ComposerCarry? carried)
    {
        var catalog = composition.Catalog;
        SessionWorkspaceViewModel? created = null;
        var session = new SessionWorkspaceViewModel(
            info,
            readOnly ? null : composition.Runtimes.Get(info),
            composition.Checkpoints,
            composition.Sessions,
            composition.Providers,
            OnSubAgentToolRequested,
            instrumentSources: instruments,
            preferences: composition.Preferences,
            openInspector: OpenInspector,
            openSession: () => ActivateSessionTab(info.Id),
            sessionMentions: () => created is null ? [] : SessionMentionTargetsFor(created),
            sessionReceipt: DeliverReceipt,
            sessionComposer: composerSelections)
        {
            ChildQuery = readOnly
                ? (parent, token) => catalog.QueryAsync(new SessionQuery { ParentSessionId = parent }, token)
                : null,
        };
        created = session;
        // The constructor's first fill runs before `created` is assigned and can only answer empty: ask again now that the
        // view can name itself, so a freshly opened (or role-rebuilt) tab starts on the @ picker its role gives it.
        session.RefreshSessionMentions();
        if (!readOnly)
        {
            session.TurnGate = token => CheckTurnAsync(session, token);
        }

        session.BecameReadOnly += OnSessionBecameReadOnly;
        session.TurnCompleted += OnTurnCompleted;
        if (!readOnly)
        {
            HookAutomations(session);
        }

        session.SubAgentsChanged += OnSubAgentsChanged;
        session.SubAgentRegionRevealRequested += OnSubAgentRegionReveal;
        session.AgentTreeChanged += OnAgentTreeChanged;
        sessions[info.Id] = session;
        if (carried is { } state)
        {
            session.RestoreComposerState(state);
        }

        return session;
    }

    /// <summary>
    /// Restores the remembered root sessions as one batch: every stored row is read first (concurrently, off the dispatcher) and
    /// then every tab is built in a single UI pass with nothing awaited between them, so the strip appears at once instead of
    /// one tab per read. Only the session that ends up on screen replays its history (its activation does it); the rest defer
    /// that to their first activation. Agent-session tabs need their root's row too and keep the single-tab path.
    /// </summary>
    private async Task RestoreSessionTabsAsync(IReadOnlyList<string> order, string? activeId)
    {
        var store = composition.Sessions;
        var rows = await Task.WhenAll(order.Select(id => ReadSessionRowAsync(store, id))).ConfigureAwait(true);

        var lastRestored = (string?)null;
        var agentTabs = (List<string>?)null;
        for (var i = 0; i < order.Count; i++)
        {
            if (rows[i] is not { } info)
            {
                continue;
            }

            if (info.ParentSessionId is not null)
            {
                (agentTabs ??= []).Add(info.Id);
                continue;
            }

            if (sessions.TryGetValue(info.Id, out var existing))
            {
                // A tab filed for this session may have been built for the other role (the layout remembered it, or a
                // session opened during start-up): the row decides, and a view for the role it no longer has is rebuilt.
                if (SessionRoleRules.ActionFor(info, existing.IsAgentSession, existing.IsReadOnlyView, existing.AgentSessionRoot?.Id)
                    == SessionRoleAction.Keep)
                {
                    ShowSessionTab(existing, activate: false);
                    openTabs.Add(info.Id);
                    lastRestored = info.Id;
                    continue;
                }

                TryRetireView(existing, out _);
            }

            var readOnly = info.Status != SessionStatus.Active;
            var session = BuildSession(info, readOnly, carried: null);
            ShowSessionTab(session, activate: false, index: i);
            openTabs.Add(info.Id);
            lastRestored = info.Id;
        }

        ScheduleLayoutSave();

        if (agentTabs is not null)
        {
            foreach (var id in agentTabs)
            {
                try
                {
                    await OpenAgentSessionByIdAsync(id, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
                {
                    AppLogs.Layout.ZLogWarning($"skipped agent session {id}: {exception.Message}");
                }
            }
        }

        if (activeId is { } active && sessions.TryGetValue(active, out var activeSession))
        {
            ActivateSessionTab(activeSession.SessionId);
            ActiveSession = activeSession;
        }
        else if (lastRestored is { } last && sessions.TryGetValue(last, out var fallback))
        {
            // The stored active session is gone (archived, deleted, or its tab failed to open): the last tab restored is what
            // the window used to end up on, so it stays the one on screen.
            ActivateSessionTab(fallback.SessionId);
            ActiveSession = fallback;
        }
    }

    /// <summary>One session row for the batch above; a store failure skips that tab instead of failing the whole restore.</summary>
    private static async Task<SessionInfo?> ReadSessionRowAsync(ISessionStore store, string sessionId)
    {
        try
        {
            return await BackgroundRead
                .RunAsync(token => store.GetSessionAsync(sessionId, token), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Layout.ZLogWarning($"skipped session {sessionId}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// The turn-start check (6.3), read off the dispatcher: the store must still have the session as Active (a handover
    /// or a restore elsewhere may have replaced it), and its workspace must be neither in an operation (a merge back /
    /// sync running in it: this also covers a tab opened after the operation began) nor in a merge conflict (the snapshot
    /// the tree refresh keeps; no git runs here). A store failure lets the turn start: the check guards against a stale tab,
    /// it is not a second source of truth.
    /// </summary>
    private Task<TurnRejection> CheckTurnAsync(SessionWorkspaceViewModel session, CancellationToken ct) =>
        CheckTurnAsync(session.SessionId, ct);

    /// <summary>
    /// The same check by session id: an agent-session tab gates its turns on the row of its root (the child's own row says
    /// nothing about the root having been taken over or archived).
    /// </summary>
    private async Task<TurnRejection> CheckTurnAsync(string id, CancellationToken ct)
    {
        var store = composition.Sessions;
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
        // The root left Active: every authorization of its tree dies with that (forge-core "session messaging"). Revoked before the
        // runtime is released, while the host that holds the table is still there.
        RevokeSessionMessagesOf(session);
        composition.Runtimes.Release(session.SessionId);
        _ = RefreshTreesAfterLifecycleAsync();
    }

    /// <summary>
    /// Why an existing directory cannot be used again (6.3), or null: it is a repository worktree that git marks prunable
    /// or does not list (a turn there would run git against a broken or an enclosing repository). A folder that is gone
    /// blocks nothing: restore rebuilds it first. A worktree list that cannot be read is unknown and blocks nothing: it is
    /// neither healthy nor stale, and the turn's own git calls report what is wrong. Runs off the dispatcher.
    /// </summary>
    internal Task<string?> RestoreBlockAsync(SessionInfo info, CancellationToken ct)
    {
        var git = composition.Git;
        var projects = composition.Projects;
        return Task.Run(
            async () =>
            {
                var directory = info.WorktreePath ?? info.WorkspacePath;
                if (!Directory.Exists(directory))
                {
                    return null;
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
    internal async Task TakeOverAsync(
        SessionInfo holder,
        HandoverIntent intent,
        CancellationToken ct,
        HandoverDialogStyle style = HandoverDialogStyle.TakeOver)
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
            await RunHandoverDialogAsync(holder, intent, ct, style).ConfigureAwait(true);
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
        HandoverIntent intent,
        CancellationToken ct,
        HandoverDialogStyle style)
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
            },
            style);
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

        // A takeover into a session that did not exist (or that was already the branch's root) does not change that
        // session's role: its first turn is a plain cold start, not a rewritten prefix.
        var promotedChangedRole = intent.ResumeSessionId is { } resumeId && await IsAgentSessionAsync(resumeId, ct).ConfigureAwait(true);

        // The old session handed the workspace over: its row is an archived agent session of the taker now, so its tab takes
        // that role (read-only) and its still-active agent sessions move under the taker - the same handover a promotion
        // runs, so no path leaves a tab on the role it had before.
        await HandOverBranchAsync(holder.Id, taker.Id, keepDemotedTab: true, activateId: taker.Id, promotedChangedRole, ct)
            .ConfigureAwait(true);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        if (done.Warning is { } warning)
        {
            await dialogs.NotifyAsync(HandoverStrings.Title, warning).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Whether the stored row is an agent session - what the taker of a resumed handover was before it took the branch. A read
    /// that fails answers false: an unknown previous role must not become a claim about a cache that was never rewritten.
    /// </summary>
    private async Task<bool> IsAgentSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var row = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(sessionId, token), ct)
                .ConfigureAwait(true);
            return row?.ParentSessionId is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"the previous role of '{sessionId}' could not be read: {exception.Message}");
            return false;
        }
    }

    /// <summary>Adds the reasons of <paramref name="extra"/> that <paramref name="reasons"/> does not name yet.</summary>
    private static void Merge(List<string> reasons, IReadOnlyList<string> extra)
    {
        foreach (var reason in extra)
        {
            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }
    }

    /// <summary>
    /// Why the session is not idle (section 2, "the busy reasons are merged into the root"): the busy sources of its open
    /// tab first, then those of the agent sessions running under it - they run turns and write tools in its worktree whether
    /// or not their tabs, or its own, are open. The tree reads this one list for both the disabled entries and the flyout
    /// text, so the two never disagree; a reason shared with an agent session appears once. In memory only.
    /// </summary>
    private List<string> SessionBusyReasons(string sessionId)
    {
        var reasons = new List<string>(5);
        if (sessions.TryGetValue(sessionId, out var view))
        {
            reasons.AddRange(view.BusyReasons());
        }

        Merge(reasons, AgentSessionBusyReasons(sessionId));
        return reasons;
    }

    /// <summary>
    /// Why <paramref name="holder"/> cannot be taken over now (section 2, idle): the busy sources of its open tab, the busy
    /// reasons of the agent sessions running under it (they run turns and write tools in the holder's worktree, and they do so
    /// whether or not the holder's own tab is open), a handover already running for it, and the workspace-level states (an
    /// operation under way, a merge conflict). The holder's own reasons come first; a reason shared with its agent sessions
    /// appears once.
    /// </summary>
    private List<string> HandoverBusyReasons(SessionInfo holder, SessionWorkspaceViewModel? holderView)
    {
        var reasons = new List<string>(holderView?.BusyReasons() ?? []);
        Merge(reasons, AgentSessionBusyReasons(holder.Id));

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
    /// Wired to <see cref="RemoveForkWorkspaceAsync"/>; settable so it can be substituted.
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
        WirePairing(tree);
        WireMenu(tree);
        RemoveForkWorkspace ??= RemoveForkWorkspaceAsync;
    }

    /// <summary>
    /// The busy flags of a session's open tab, a handover running for it, and the agent sessions under it (their turns, queue
    /// and interjections live in the same worktree) - the same reasons the tree names when an entry is disabled; in memory only.
    /// </summary>
    private bool IsSessionBusy(string sessionId) =>
        handingOver.Contains(sessionId) || SessionBusyReasons(sessionId).Count > 0;

    /// <summary>
    /// The parallel fork (6.5) of <paramref name="source"/>: reads the live HEAD, changes and sparse config off the UI
    /// thread (detached / unborn / not a repository refuse with a notice), then runs the fork dialog. A busy source only
    /// changes are only warned about - nothing is committed and nothing is carried. A successful fork creates the branch and
    /// its worktree only, so the trees refresh and the status bar names the new workspace; the source session goes on
    /// untouched.
    /// </summary>
    internal async Task ForkWorkspaceAsync(ForkSource source, CancellationToken ct)
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

        ForkAttempt? done = null;
        var model = new ForkDialogModel(
            preparation.Branch!,
            preparation.StartCommit!,
            preparation.Uncommitted.Count,
            preparation.Sparse is not null,
            ForkFlow.DefaultBranchName(composition.Worktrees.Manager.Options.BranchPrefix),
            async (choice, token) =>
            {
                var attempt = await Task.Run(() => flow.ExecuteAsync(source, choice, token), CancellationToken.None)
                    .ConfigureAwait(true);
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

        if (done is not { Succeeded: true })
        {
            return;
        }

        // The fork is the branch and its checkout: no session was created there, so the tree is all there is to refresh.
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        if (done.Workspace is { } workspace)
        {
            Status.SetWorkspaceProblem(LocText.Literal(ForkStrings.Created(workspace)));
        }
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
    /// The idle check of the merge target (section 2): the busy sources of a session open in that workspace, the busy
    /// reasons of the agent sessions working in it (their tabs, and their roots' tabs, may all be closed while their turns go
    /// on), and the workspace's own states. When busy, lists the reasons and returns true; nothing is queued or forced. With
    /// no session of that workspace on screen the dialog names the target itself - a path, not a session title.
    /// </summary>
    private async Task<bool> ShowTargetBusyAsync(string title, string target)
    {
        var key = OccupancyKey.Compute(null, target);
        var live = sessions.Values.FirstOrDefault(session =>
            !session.IsReadOnlyView
            && OccupancyKey.Compute(null, session.Info.WorktreePath ?? session.Info.WorkspacePath) == key);
        var reasons = live is not null ? HandoverBusyReasons(live.Info, live) : new List<string>(5);
        // A session with no tab on screen is not in the open table, but its agent sessions keep running turns in that
        // worktree: their roots answer the same busy question a handover asks.
        Merge(reasons, WorkspaceAgentSessionBusyReasons(key));

        var states = composition.WorkspaceStates;
        if (states.IsOperating(target) && !reasons.Contains(HandoverStrings.BusyWorkspaceOperating))
        {
            reasons.Add(HandoverStrings.BusyWorkspaceOperating);
        }

        if (states.IsConflicted(target) && !reasons.Contains(HandoverStrings.BusyMergeConflict))
        {
            reasons.Add(HandoverStrings.BusyMergeConflict);
        }

        if (reasons.Count == 0)
        {
            return false;
        }

        await dialogs!.NotifyAsync(title, ForkStrings.Busy(live?.Info.Title ?? target, reasons)).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Why the agent sessions working in the workspace keyed <paramref name="key"/> are busy (section 2, "the busy reasons
    /// are merged into the root"): every root that has an agent-session view there - one with a tab on screen
    /// (<see cref="agentViews"/>) or one whose tab closed while a turn ran (<see cref="agentViewsClosing"/>) - answers with
    /// the reasons a handover of that root would give, whether or not the root's own tab is open. Each root and each reason
    /// once.
    /// </summary>
    private List<string> WorkspaceAgentSessionBusyReasons(string key)
    {
        var reasons = new List<string>(5);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in agentViews.Keys.Concat(agentViewsClosing))
        {
            if (session.AgentSessionRoot is not { } root
                || OccupancyKey.Compute(null, HandoverFlow.DirectoryOf(root)) != key
                || !roots.Add(root.Id))
            {
                continue;
            }

            sessions.TryGetValue(root.Id, out var view);
            Merge(reasons, HandoverBusyReasons(root, view));
        }

        return reasons;
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

    /// <param name="gitChanged">
    /// The flow moved a branch, a checkout, a worktree or a merge, so the menus' cached git reads are stale now. A flow
    /// that only changed session rows (a restore into an existing directory, an archive, a session removal) passes false:
    /// the rebuild then serves the cached reads even past their lifetime so the rows move at once, and a fresh rebuild
    /// follows in the background to pick up whatever changed outside Forge. The conflict snapshot is workspace git state
    /// too, so only a changed checkout refreshes it here; the next turn end or manual refresh reads it anyway.
    /// </param>
    private async Task RefreshTreesAfterLifecycleAsync(bool gitChanged = true)
    {
        if (gitChanged)
        {
            Projects.InvalidateGitState();
            GeneralChats.InvalidateGitState();
        }

        try
        {
            await RefreshTreesAsync(CancellationToken.None, reuseStaleGitReads: !gitChanged, refreshConflicts: gitChanged).ConfigureAwait(true);
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
            var problem = LocText.Literal(RemovalStrings.RemoveRetryFailed(string.Join("; ", failures.Distinct(StringComparer.Ordinal))));
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
    private void ShowSessionTab(SessionWorkspaceViewModel session, bool activate = true, int? index = null)
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
            if (activate)
            {
                Workbench.ActivateTab(existing);
            }

            return;
        }

        Workbench.OpenTab(
            Workbench.MainTabs, id, TabKind.Session, session, session.TabTitle, activate: activate, index: index);
        session.TabTitleChanged += OnSessionTitleChanged;
    }

    private void ActivateSessionTab(string sessionId)
    {
        if (Workbench.FindTab(WorkbenchIds.SessionTab(sessionId)) is { } tab)
        {
            Workbench.ActivateTab(tab);
            return;
        }

        // An agent session's tab keeps the id it was opened under (the draft's), so the stored session id finds it here.
        ActivateAgentSessionTab(sessionId);
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

        // A derived placeholder or the model's own set_title lands here as the store accepted it: the tree row, the open
        // agent sessions of this root and the @ picker's names follow, exactly as they do for a name a person typed.
        Retitle(session.SessionId, session.Info.Title);
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
        if (HasAgentSessionsUsingRuntime(session.SessionId))
        {
            return;
        }

        // The root's runtime is what its agent sessions run in: a closed root tab releases nothing while one of them still needs
        // it, or the child would lose its host mid-turn. The last one to finish releases it (ReleaseRootRuntimeIfUnused).
        composition.Runtimes.Release(session.SessionId);
    }

    /// <summary>
    /// Whether an open agent-session tab <em>runs</em> under this root. A read-only history tab of one of its agent sessions
    /// keeps no runtime: it runs nothing, and its root's runtime may go when that root's own tab closes.
    /// </summary>
    private bool HasOpenAgentSessions(string rootSessionId)
    {
        foreach (var session in agentViews.Keys)
        {
            if (!session.IsReadOnlyView
                && string.Equals(session.AgentSessionRoot?.Id, rootSessionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An archived session leaves the tree together with its tab: the tab is closed first, so no dead tab survives the
    /// view model it shows, and a later reopen finds no duplicate <c>session:</c> tab. Archiving a root takes its agent
    /// sessions with it: their tabs close too, and the root's runtime is released once they are gone.
    /// </summary>
    private void OnSessionArchived(string sessionId)
    {
        // A target that was archived takes its grants with it (forge-core "session messaging": a grant lives until the target session ends).
        OnMessageTargetClosed(sessionId);
        terminals.OnSessionArchived(sessionId);
        CloseAgentSessionTabs(sessionId);
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

    /// <summary>
    /// An archive with a successor, or an explicit "promote to main session", handed the workspace to the promoted
    /// session: the one routine every role change runs, so no path leaves a tab on the role it had before.
    /// </summary>
    private void OnSessionPromoted(string rootId, SessionInfo promoted) =>
        _ = HandOverAsync(rootId, promoted.Id, keepDemotedTab: false, activateId: promoted.Id, promotedChangedRole: true);

    /// <summary>
    /// Fire-and-forget wrapper: a handover that fails only leaves the tab on its old role, and the tree row still opens the
    /// right one - it must never take the window down.
    /// </summary>
    private async Task HandOverAsync(
        string demotedRootId,
        string promotedId,
        bool keepDemotedTab,
        string? activateId,
        bool promotedChangedRole)
    {
        try
        {
            await HandOverBranchAsync(demotedRootId, promotedId, keepDemotedTab, activateId, promotedChangedRole, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the handover of '{demotedRootId}' to '{promotedId}' failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The store handed the branch to <paramref name="promotedId"/>: the old root left Active as an archived agent session
    /// of it, and the new root owns the workspace. Every open tab of the branch takes the role, root and runtime the store
    /// now holds - the promoted session becomes the branch's root (its agent view was built for Depth 1 and with it lost the
    /// root's tool surface), the old root's other agent tabs move under the new root in place, and the old root's own tab is
    /// closed or kept as the read-only view of the row it is now.
    /// </summary>
    /// <param name="activateId">The session the caller wants on screen; the other side of the handover is repaired without
    /// taking the focus.</param>
    /// <param name="promotedChangedRole">Whether the promoted session existed before this action as the other role. A brand
    /// new one (a takeover into a fresh session) or one that was already the branch's root did not change role, so its first
    /// turn is an ordinary cold start and the cache toast would misattribute the cost.</param>
    private async Task HandOverBranchAsync(
        string demotedRootId,
        string promotedId,
        bool keepDemotedTab,
        string? activateId,
        bool promotedChangedRole,
        CancellationToken ct)
    {
        if (string.Equals(demotedRootId, promotedId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{demotedRootId}' cannot hand the branch over to itself.", nameof(promotedId));
        }

        // The row after the transaction, never the caller's snapshot: the snapshot it holds was read before the store moved
        // the parent link, and this method's whole job is the answer that link carries.
        SessionInfo? promoted;
        try
        {
            promoted = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(promotedId, token), ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"the handover of '{demotedRootId}' to '{promotedId}' failed: {exception.Message}");
            return;
        }

        if (promoted is null || promoted.ParentSessionId is not null)
        {
            // The store does not hold it as this branch's root: nothing moved, and opening it must not invent a role.
            return;
        }

        var runtime = composition.Runtimes.Get(promoted);
        var rebuildDemotedTab = false;
        ComposerCarry? carried = null;
        ComposerCarry? demotedCarried = null;
        foreach (var view in agentViews.Keys.ToArray())
        {
            if (!string.Equals(view.AgentSessionRoot?.Id, demotedRootId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(view.SessionId, promoted.Id, StringComparison.Ordinal))
            {
                // The promoted session's own tab was built for the role it no longer has: it is retired (its draft carried
                // into the root view below) rather than re-homed.
                TryRetireView(view, out carried);
                continue;
            }

            // An archived or removed child stays a history row and its tab closes with the old root; a tab with a turn in
            // flight keeps its runtime until that turn ends and is closed with the old root too.
            if (view.Info.Status != SessionStatus.Active || view.IsRunning)
            {
                continue;
            }

            view.RehomeTo(promoted, runtime);
            view.TurnGate = token => CheckTurnAsync(promoted.Id, token);
        }

        if (keepDemotedTab)
        {
            // The old root keeps its tab as the read-only view of the row it is now (an archived agent session of the new
            // root): its grants die with the role, its runtime goes, and the view is rebuilt below.
            sessions.TryGetValue(demotedRootId, out var demoted);
            if (demoted is not null)
            {
                RevokeSessionMessagesOf(demoted);
            }

            OnMessageTargetClosed(demotedRootId);
            terminals.OnSessionArchived(demotedRootId);
            CloseAgentSessionTabs(demotedRootId);
            openTabs.Remove(demotedRootId);

            // A turn still running in that view finishes on the host it started on: its tab and runtime stay until it ends,
            // and the role it kept is repaired the next time the tab is opened.
            if (demoted is null || TryRetireView(demoted, out demotedCarried))
            {
                composition.Runtimes.Release(demotedRootId);
                rebuildDemotedTab = demoted is not null;
            }
        }
        else
        {
            OnSessionArchived(demotedRootId);
        }

        await OpenSessionByIdAsync(promoted.Id, activateId == promoted.Id, ct).ConfigureAwait(true);
        if (carried is { } state && sessions.TryGetValue(promoted.Id, out var opened))
        {
            opened.RestoreComposerState(state);
        }

        if (rebuildDemotedTab)
        {
            await OpenSessionByIdAsync(demotedRootId, activateId == demotedRootId, ct).ConfigureAwait(true);
            if (demotedCarried is { } kept && sessions.TryGetValue(demotedRootId, out var demotedView))
            {
                // The old root's draft survives the demotion: the read-only view holds it, and a later "continue" carries it
                // on into the writable view.
                demotedView.RestoreComposerState(kept);
            }
        }

        if (promotedChangedRole)
        {
            NotifyRoleChanged(promoted.Id, promoted.Title, becameRoot: true);
        }
    }

    /// <summary>
    /// A session's role changed and its tool surface moved with it: one toast says what that costs - the prefix the
    /// provider caches (system prompt + tool definitions) is written from scratch once, while the conversation and every
    /// other tab keep theirs. Nothing is required of the user, so the level is <see cref="NotificationLevel.Info"/>; the key
    /// is the session, so a promote-demote-promote round counts its repeats into one card instead of stacking three.
    /// </summary>
    private void NotifyRoleChanged(string sessionId, string title, bool becameRoot) =>
        composition.Notifications.Notify(
            NotificationLevel.Info,
            SessionStrings.RoleChangedToastTitle,
            SessionStrings.RoleChangedToastBody(title, becameRoot),
            "session-role:" + sessionId);

    /// <summary>
    /// The store moved agent sessions under <paramref name="rootId"/> (a restore that joined a branch, a demotion's
    /// family): every open tab whose row now hangs under it follows - a live one in place, the same re-home a promotion
    /// does and no replay, because only who owns the workspace moved; a read-only one takes the role its row holds now.
    /// </summary>
    private async Task RehomeAgentTabsAsync(string rootId, CancellationToken ct)
    {
        if (agentViews.Count == 0)
        {
            return;
        }

        SessionInfo? root;
        try
        {
            root = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(rootId, token), ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"the agent tabs of '{rootId}' were not re-homed: {exception.Message}");
            return;
        }

        if (root is null || root.Status != SessionStatus.Active)
        {
            return;
        }

        SessionRuntime? runtime = null;
        foreach (var view in agentViews.Keys.ToArray())
        {
            if (view.IsRunning)
            {
                continue;
            }

            SessionInfo? row;
            try
            {
                row = await BackgroundRead.RunAsync(token => composition.Sessions.GetSessionAsync(view.SessionId, token), ct)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                continue;
            }

            if (row is null || !string.Equals(row.ParentSessionId, root.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.Status != SessionStatus.Active)
            {
                // Its own row is history now (an archived child that followed the family): the tab is the read-only view of it.
                if (!view.IsReadOnlyView)
                {
                    view.EnterReadOnly(row);
                }

                continue;
            }

            if (view.IsReadOnlyView)
            {
                // A history tab of a row that is Active again (its old root was archived): re-homing it would move a read-only
                // view onto a live root and leave it unable to run, so the guarded open replaces it with the live view.
                await OpenAgentSessionByIdAsync(view.SessionId, activate: false, ct).ConfigureAwait(true);
                continue;
            }

            // The role held; only the root it runs on moved, so the tab keeps its transcript and its run.
            if (string.Equals(view.AgentSessionRoot?.Id, root.Id, StringComparison.Ordinal))
            {
                continue;
            }

            runtime ??= composition.Runtimes.Get(root);
            view.RehomeTo(root, runtime);
            view.TurnGate = token => CheckTurnAsync(root.Id, token);
        }
    }

    /// <summary>Closes every open agent-session tab of an archived root, so no tab outlives the runtime it runs in.</summary>
    private void CloseAgentSessionTabs(string rootSessionId)
    {
        foreach (var (session, tab) in agentViews.ToArray())
        {
            if (string.Equals(session.AgentSessionRoot?.Id, rootSessionId, StringComparison.Ordinal))
            {
                Workbench.CloseTab(tab);
            }
        }
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
    /// The composer's chip asked for the region. The session's wish is recorded first, then - only when that session is
    /// the one on screen, since a floated view's chip is not the group's to move - the body opens the way the title bar's
    /// own restore opens it, the empty state included.
    /// </summary>
    private void OnSubAgentRegionReveal(SessionWorkspaceViewModel session)
    {
        session.SubAgentsCollapsed = false;
        if (ReferenceEquals(activeSession, session))
        {
            Workbench.RevealBottomBody();
        }
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
    private void OpenSubAgentTab(SubAgentViewModel agent, bool activate = true)
    {
        Workbench.OpenTab(
            Workbench.BottomTabs,
            WorkbenchIds.SubAgentTab(agent.AgentId),
            TabKind.SubAgent,
            agent,
            agent.TabTitle,
            activate);
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
        if (!ReferenceEquals(bottomPointedAt, session))
        {
            // The group is being pointed at a (possibly another) session: its wish takes over, and a hand-opened empty
            // body from the session before is a moment that does not travel along.
            bottomPointedAt = session;
            Workbench.DropBottomHandOpen();
        }

        SyncFinishedMenu(session);
        if (session is null)
        {
            PruneSubAgentTabs(null, []);
            SyncTreeActiveAgent();
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
            // Docking a child never activates it: the roster holds finished children too now, and syncing on every
            // member change would otherwise drag the user's selection onto the last one in the list. A tab that just
            // started announces itself through OpenSubAgentTabAsync instead.
            OpenSubAgentTab(agent, activate: false);
        }

        // The chip's accent reads the real thing, not the wish: a session whose sub-agents have all left the roster docks
        // no tab, so the group folds to its title bar (its finished menu stays) and the accent rests until a tab lands
        // again - while the click stays live either way. A child that finished a moment ago is still on the roster, so
        // its tab is still there.
        session.SubAgentRegionOpen = Workbench.IsBottomBodyOpen;
        SyncTreeActiveAgent();
    }

    /// <summary>
    /// Re-reads the tree's current sub-agent row from the bottom group once the tab set has settled. Pruning closes tabs, and
    /// closing the active one activates a neighbour - which re-announces that neighbour as "on screen" while it still belongs
    /// to the session that just left; an emptied group reports nothing at all, because a null active tab raises no change.
    /// Taking the group's real state last is what keeps the tree free of a row the user cannot see.
    /// </summary>
    private void SyncTreeActiveAgent() =>
        SetTreesActiveAgent((Workbench.BottomTabs.ActiveTab?.Content as SubAgentViewModel)?.AgentId);

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
                // A close the shell decided on says nothing about the topic: only the user's own close retires a child.
                prunedSubAgentTabs.Add(agent);
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

        WatchFinishedMenu(session, menu);
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
    /// Follows the watched menu and its session into <see cref="TabGroupModel.KeepsVisibleWhenEmpty"/>: the bottom group
    /// stays in the layout while the session has any sub-agent activity at all, so its title bar - with the restore button
    /// that opens even an empty body - never leaves with it.
    /// </summary>
    private void WatchFinishedMenu(SessionWorkspaceViewModel? session, FinishedSubAgentsViewModel menu)
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

        watchedMenuSession = session;
        UpdateBottomGroupContent();
    }

    private void OnFinishedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateBottomGroupContent();

    private void UpdateBottomGroupContent() =>
        Workbench.BottomTabs.KeepsVisibleWhenEmpty = KeepsBottomGroupVisible(watchedFinishedMenu, watchedMenuSession);

    /// <summary>
    /// Whether the bottom group must stay in the layout with no tab docked: a finished sub-agent the title bar can still
    /// open, or a live-roster child whose tab the user closed or floated while it runs - its tab comes back when the run
    /// ends, and until then the group folds to its title bar instead of taking the restore button with it. A session
    /// without any sub-agent activity gives the group nothing to carry, so it leaves the layout as before.
    /// </summary>
    internal static bool KeepsBottomGroupVisible(FinishedSubAgentsViewModel? menu, SessionWorkspaceViewModel? session) =>
        menu is { Items.Count: > 0 } || session is { HasSubAgents: true };

    /// <summary>
    /// The bottom group's own restore button minimizes it; the session keeps that state so it is not popped up again. Its
    /// body coming and going (a tab docks, the last one leaves) is mirrored too, for the composer's chip: the accent follows
    /// whether the body is on screen - the click itself is always live, so it needs no gate.
    /// </summary>
    private void OnWorkbenchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchLayout.IsBottomBodyOpen))
        {
            if (activeSession is { } open)
            {
                open.SubAgentRegionOpen = Workbench.IsBottomBodyOpen;
            }

            return;
        }

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
            DetachAgentSessionTab(tab);
            session.TabTitleChanged -= OnSessionTitleChanged;
            openTabs.Remove(session.SessionId);

            if (!session.IsRunning)
            {
                ReleaseClosedSession(session);
            }
        }

        if (tab.Kind == TabKind.SubAgent && tab.Content is SubAgentViewModel agent)
        {
            // Closing a child's own tab is the user ending that topic: it leaves the roster now instead of when its reuse
            // window closes, and its report moves to the finished menu. Core still holds the instance, so the scheduler may
            // hand it the next task of the same kind - that pulls it back out of the menu and re-docks the tab.
            // Remove() consumes the marker of a bookkeeping close; a read-only tab is already retired.
            if (!prunedSubAgentTabs.Remove(agent) && !agent.IsReadOnly && sessions.TryGetValue(agent.SessionId, out var owner))
            {
                owner.RetireSubAgent(agent);
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

        // The tab that is on screen has nothing unread: the mark it may carry goes now - an agent session's ended turn and a
        // receipt another session delivered to a root both raise the same one - and its tree row with it.
        var wasUnread = session.HasAgentUnread;
        session.MarkAgentSeen();
        if (wasUnread)
        {
            RefreshTreeAgents();
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
            // Same tail as the non-empty path: the finished menu, the sub-agent tabs and the tree's current row go together.
            SyncSubAgentRegion();
            ReapplyReviewOwners();
            return;
        }

        var projectRoot = session.Info.ProjectId is { } projectId && projectRoots.TryGetValue(projectId, out var root)
            ? root
            : null;

        // The panels follow the tab: an agent-session tab hands them the child session's own id (never the root's), so the
        // review comments, the turn list and the changed-file tree are that session's alone. Only the root changes git, so
        // the git row of a child's tab keeps its reads and hides its writes.
        var context = new PanelContext(session.SessionId, session.WorkDir, projectRoot) { GitWrites = !session.IsAgentSession };
        Changes.Attach(context);
        Files.Attach(context);
        Todos.Attach(session.SessionId);
        Reviews.Attach(context);
        Specs.Attach(context);
        GitSync.Attach(context);
        ReapplyReviewOwners();
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
                break;
            case nameof(GitSyncViewModel.AheadBehindText):
                Status.SetAheadBehind(GitSync.AheadBehindText);
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
        document.FindOpenDocument = path => FindFileTab(path)?.Content as FileDocumentViewModel;
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
        // The opener either pins an owner (a stored diagnostic, a definition target) or leaves the document to the tab on
        // screen: a preview has no owner of its own, so every tab switch re-resolves it (ApplyPanelContext).
        document.PinReviewOwner(hasOverride ? ownerOverride : null);
        document.ConfigureReview(CreateReviewServices(), hasOverride ? ownerOverride : ActiveReviewOwner());
    }

    /// <summary>
    /// The session a preview's review comments belong to: the one on screen, same as the review panel and the diff view, so
    /// an opinion written in A's preview is A's and is gone after switching to B (forge-app "Agent 会话的界面与接线").
    /// </summary>
    private Review.ReviewOwner? ActiveReviewOwner()
    {
        // A draft agent-session tab has no session row yet: a comment stored under its make-believe id could never be found
        // again once the row exists, so the preview offers no comments there (the status line says why).
        if (activeSession is not { WorkDir.Length: > 0 } session
            || (session.IsAgentSession && session.AgentChildSessionId is null))
        {
            return null;
        }

        return new Review.ReviewOwner(session.WorkDir, session.SessionId);
    }

    /// <summary>
    /// Hands every open preview the owner of the tab on screen again. Without this, a document keeps the owner it was opened
    /// under, and a comment written in one agent session's preview would be filed - and shown - under another session.
    /// </summary>
    private void ReapplyReviewOwners()
    {
        foreach (var tab in Workbench.PreviewTabs.Tabs)
        {
            if (tab.Content is not FileDocumentViewModel document)
            {
                continue;
            }

            var owner = document.PinnedReviewOwner ?? ActiveReviewOwner();
            if (document.ReviewOwner != owner)
            {
                document.ConfigureReview(CreateReviewServices(), owner);
            }
        }
    }

    /// <summary>File previews and the changes view share one review store, delivery path and panel refresh.</summary>
    private Review.ReviewServices CreateReviewServices() =>
        reviewServices ??= new(
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
        // The one unconditional refusal: a live sub-agent's tab is the window onto a run, so no user gesture closes it
        // (the strip hides its close box; this stands in for the middle click and whatever else reaches the guard).
        if (TabModel.BlocksUserClose(tab))
        {
            return false;
        }

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

    /// <summary>
    /// The add-project button: an existing folder, a clone or a new repository. The flow itself lives in
    /// <c>MainWindowViewModel.Projects.cs</c> (wave W9), next to the creation dialog and the registration every branch shares;
    /// this method only keeps the name the command and the panel button were wired to.
    /// </summary>
    private Task AddProjectAsync(CancellationToken ct) => AddProjectFlowAsync(ct);

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
            // "新根会话…" on a branch that already has a session: its own dialog asks the one question (hand over a summary or
            // start blank) and takes the branch over - uncommitted changes stay in the workspace by construction. The holder's
            // row is what the handover hands over, so it is read first; an unreadable row falls back to the occupied notice.
            var holder = await BackgroundRead
                .RunAsync(
                    token => composition.Sessions.GetSessionAsync(occupied.Occupied.HolderSessionId, token),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (holder is null)
            {
                await ShowOccupiedAsync(occupied.Occupied, null, CancellationToken.None).ConfigureAwait(true);
                return false;
            }

            // The branch is read live, never taken from the tree's last plan: an outside checkout may have moved it since, and
            // the new session row records whichever branch this workspace is on now.
            var branch = await LiveBranchAsync(
                    occupied.Create.WorktreePath ?? occupied.Create.WorkspacePath,
                    occupied.Create.Branch)
                .ConfigureAwait(true);
            await TakeOverAsync(
                    holder,
                    HandoverIntent.NewSession(occupied.Create with { Branch = branch }),
                    CancellationToken.None,
                    HandoverDialogStyle.NewRootSession)
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
    /// Opens the settings page on one section: the click target of the app's "open settings" links. The window is created on
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
        CloseOnboarding();
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

        // The overlay reads every open session's live work; it goes before the sessions it reads do.
        CloseInspector();

        DetachAutomations();
        foreach (var session in sessions.Values)
        {
            UnhookAutomations(session);
            session.Dispose();
        }

        // An agent-session view whose row its first message never created is in no session map: it still holds a composer
        // and a language subscription, so it goes with the rest.
        foreach (var session in agentViews.Keys)
        {
            session.Dispose();
        }

        sessions.Clear();
        openTabs.Clear();

        if (watchedFinishedMenu is not null)
        {
            watchedFinishedMenu.Items.CollectionChanged -= OnFinishedItemsChanged;
            watchedFinishedMenu = null;
        }

        watchedMenuSession = null;

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
        PropertyChanged -= OnPaletteActiveSessionChanged;
        paletteViewModel.PropertyChanged -= OnPaletteOpenChanged;
        paletteViewModel.Dispose();
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
        questionPanels?.Dispose();
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
