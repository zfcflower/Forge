using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Terminal;
using Forge.App.Workbench;
using Forge.HostClient;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>The shell's modal interactions, implemented by the view so the view models stay free of window types.</summary>
internal interface IDialogService
{
    Task<string?> PickFolderAsync(string title);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);

    Task<string?> PromptTextAsync(string title, string initial);
}

/// <summary>
/// Root view model: owns the workbench layout, the open sessions, the panels and the persistence of the layout and the
/// open tabs. Every background task is started here; the view only renders.
/// </summary>
internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string OpenChangesToolId = "files-open-changes";

    /// <summary>Automation id (and header tool id) of the bottom group's "new terminal" button.</summary>
    private const string NewTerminalToolId = "terminal-new";

    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>Upper bound of the shutdown wait for the terminals; the coordinator itself stops waiting after four.</summary>
    private static readonly TimeSpan TerminalShutdownBudget = TimeSpan.FromSeconds(5);

    private readonly AppComposition composition;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private readonly LayoutStateLoader layoutState;
    private readonly Dictionary<string, SessionWorkspaceViewModel> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> openTabs = new(StringComparer.Ordinal);
    private readonly FinishedSubAgentsViewModel emptyFinishedMenu = new();
    private readonly Dictionary<string, string> projectRoots = new(StringComparer.Ordinal);
    private readonly DispatcherTimer clock;
    private readonly DispatcherTimer layoutSaveTimer;
    private readonly TerminalWorkbench terminals;
    private PanelToolButton newTerminalButton;
    private ReviewStore reviews;
    private PanelToolButton? openChangesTool;
    private SessionWorkspaceViewModel? activeSession;
    private FinishedSubAgentsViewModel? watchedFinishedMenu;
    private IDialogService? dialogs;
    private bool disposed;

    public MainWindowViewModel(AppComposition composition)
    {
        this.composition = composition;
        layoutState = new LayoutStateLoader(composition.UiState);
        reviews = composition.Workspace?.Reviews ?? new ReviewStore(composition.Paths.ReviewsDirectory);

        Status = new StatusBarViewModel(composition);
        Settings = new SettingsViewModel(composition);
        Projects = new ProjectTreeViewModel(
            composition.Projects,
            composition.Catalog,
            composition.Worktrees,
            OpenSessionByIdAsync,
            ConfirmAndCreateAsync,
            PromptTextAsync,
            RevealAsync,
            AddProjectAsync)
        {
            RunningSessions = () => new HashSet<string>(
                sessions.Values.Where(static session => session.IsRunning).Select(static session => session.SessionId),
                StringComparer.Ordinal),
        };
        Projects.SessionArchived = OnSessionArchived;
        Projects.AgentTrees = sessionId =>
            sessions.TryGetValue(sessionId, out var session) ? session.AgentSnapshots() : [];
        Files = new FilesPanelViewModel(
            composition.Git,
            path => OpenFile(path, newTab: false),
            path => OpenFile(path, newTab: true),
            path => ShowChangesAsync(path));
        Todos = new TodoListViewModel(composition.Todos);
        Reviews = new ReviewPanelViewModel(reviews, path => ShowChangesAsync(path));
        Specs = new SpecTreeViewModel(root => new SpecGraph(root), path => OpenFile(path, newTab: false));
        Changes = new ChangesViewModel(composition.Git, composition.TurnCheckpoints);
        ShowChangesCommand = new RelayCommand(() => ShowChangesAsync(null));
        NewTerminalCommand = new RelayCommand(OpenTerminal);
        Workbench = new WorkbenchLayout(Projects, Files, Todos, Reviews, Specs);
        terminals = new TerminalWorkbench(Workbench, composition.Preferences, composition.Host?.Client);
        newTerminalButton = CreateNewTerminalButton();
        openChangesTool = new PanelToolButton(
            OpenChangesToolId,
            ShellStrings.OpenChangesTooltip,
            "IconRestore",
            ShowChangesCommand);
        Workbench.FilesPanel.HeaderTools.Add(openChangesTool);
        Workbench.BottomTabs.HeaderTools.Add(emptyFinishedMenu);
        Workbench.BottomTabs.HeaderTools.Add(newTerminalButton);
        Workbench.TabClosed += OnTabClosed;
        Workbench.ActiveTabChanged += OnActiveTabChanged;
        Workbench.LayoutChanged += OnLayoutChanged;
        Workbench.PropertyChanged += OnWorkbenchPropertyChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        AddProjectCommand = new AsyncRelayCommand(AddProjectAsync, () => !disposed, OnCommandFailed);
        ToggleSettingsCommand = new RelayCommand(() => Settings.IsOpen = !Settings.IsOpen);
        ReloadSettingsCommand = new RelayCommand(ReloadSettings);
        RevealGeneralCommand = new AsyncRelayCommand(
            ct => RevealAsync(composition.Paths.GeneralWorkspaceDirectory),
            () => true,
            OnCommandFailed);

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

    public ProjectTreeViewModel Projects { get; }

    public FilesPanelViewModel Files { get; }

    public TodoListViewModel Todos { get; }

    public ReviewPanelViewModel Reviews { get; }

    public SpecTreeViewModel Specs { get; }

    public ChangesViewModel Changes { get; }

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

    public string ProviderNotice => composition.Provider.StatusText.Resolve();

    public bool ShowProviderNotice => !composition.Provider.IsConfigured;

    public string GeneralWorkspaceText => composition.Paths.GeneralWorkspaceDirectory;

    public SessionWorkspaceViewModel? ActiveSession
    {
        get => activeSession;
        private set
        {
            if (Set(ref activeSession, value))
            {
                OnPropertyChanged(nameof(HasActiveSession));
                OnPropertyChanged(nameof(ActiveSessionTitle));
                ApplyPanelContext();
                terminals.OnActiveSessionChanged(activeSession?.SessionId, activeSession?.WorkDir);
            }
        }
    }

    public bool HasActiveSession => activeSession is not null;

    public string ActiveSessionTitle => activeSession?.Title ?? ShellStrings.NoOpenSession;

    public void AttachDialogs(IDialogService dialogService) => dialogs = dialogService;

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
            return;
        }

        RefreshProjectCacheAsync();
        await Projects.InitializeAsync(composition.Paths.GeneralWorkspaceDirectory, CancellationToken.None).ConfigureAwait(true);
        await RestoreAsync().ConfigureAwait(true);
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
                        foreach (var project in projects)
                        {
                            projectRoots[project.Id] = project.RootPath;
                        }
                    });
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    Dispatcher.UIThread.Post(() => Status.SetWorkspaceProblem(ShellStrings.ProjectsProblem(exception.Message)));
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
                Trace.WriteLine("[layout] restore failed, using the default layout: " + exception.Message);
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
                Trace.WriteLine("[layout] skipped session " + id + ": " + exception.Message);
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
            Trace.WriteLine("[layout] floating restore failed: " + exception.Message);
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
                Trace.WriteLine("[layout] final save timed out");
            }
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            Trace.WriteLine("[layout] final save failed: " + exception.Message);
        }
    }

    private void OnLayoutSaveTick(object? sender, EventArgs e)
    {
        layoutSaveTimer.Stop();
        _ = SaveLayoutAsync(CancellationToken.None);
    }

    private void ScheduleLayoutSave()
    {
        if (disposed)
        {
            return;
        }

        layoutSaveTimer.Stop();
        layoutSaveTimer.Start();
    }

    // ---------------------------------------------------------------- sessions

    private async Task OpenSessionByIdAsync(string sessionId, CancellationToken ct)
    {
        var sessions = composition.Sessions;
        var info = await BackgroundRead.RunAsync(token => sessions.GetSessionAsync(sessionId, token), ct)
            .ConfigureAwait(true);
        if (info is null || info.ParentSessionId is not null)
        {
            return;
        }

        if (info.Status == SessionStatus.Archived)
        {
            var catalog = composition.Catalog;
            await BackgroundRead.RunAsync(token => catalog.SetStatusAsync(sessionId, SessionStatus.Active, token), ct)
                .ConfigureAwait(true);
            info = info with { Status = SessionStatus.Active };
        }

        await OpenSessionAsync(info, ct).ConfigureAwait(true);
    }

    private async Task OpenSessionAsync(SessionInfo info, CancellationToken ct)
    {
        if (sessions.TryGetValue(info.Id, out var existing))
        {
            ShowSessionTab(existing);
            openTabs.Add(info.Id);
            ActiveSession = existing;
            ScheduleLayoutSave();
            return;
        }

        var runtime = composition.Runtimes.Get(info);
        var session = new SessionWorkspaceViewModel(
            info,
            runtime,
            composition.Checkpoints,
            composition.Sessions,
            composition.Provider,
            OnSubAgentToolRequested);
        session.TurnCompleted += OnTurnCompleted;
        session.SubAgentsChanged += OnSubAgentsChanged;
        session.AgentTreeChanged += OnAgentTreeChanged;
        sessions[info.Id] = session;
        await session.LoadHistoryAsync(ct).ConfigureAwait(true);
        ShowSessionTab(session);
        openTabs.Add(info.Id);
        ActiveSession = session;
        ScheduleLayoutSave();
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
        Projects.MarkRunning(session.SessionId, false);
        if (!openTabs.Contains(session.SessionId))
        {
            ReleaseClosedSession(session);
        }

        _ = RefreshAfterTurnAsync(session);
    }

    /// <summary>A tab closed while its turn ran keeps running; once the turn ends nothing references it any more.</summary>
    private void ReleaseClosedSession(SessionWorkspaceViewModel session)
    {
        if (session.IsRunning || !sessions.Remove(session.SessionId))
        {
            return;
        }

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
                await Changes.LoadAsync(CancellationToken.None).ConfigureAwait(true);
                await Reviews.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await Projects.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Trace.WriteLine("[turn] refresh failed: " + exception.Message);
        }
    }

    private void OnSubAgentsChanged()
    {
        SyncSubAgentRegion();
        Projects.RefreshAgentTrees();
    }

    private void OnAgentTreeChanged() => Dispatcher.UIThread.Post(() => Projects.RefreshAgentTrees());

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
    /// collapsed group keeps its tabs: it is then the strip on Main's bottom edge that lists them.
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

        Workbench.IsBottomCollapsed = session.SubAgentsCollapsed;

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

    /// <summary>Opens a terminal in the active session, or says in the status bar why it cannot.</summary>
    private void OpenTerminal()
    {
        if (terminals.OpenTerminal() is null)
        {
            Status.SetWorkspaceProblem(Loc.Instance[TerminalStrings.FailureNoSession]);
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

    /// <summary>The bottom group's pin button collapses it; the session keeps that state so it is not popped up again.</summary>
    private void OnWorkbenchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkbenchLayout.IsBottomCollapsed))
        {
            return;
        }

        if (activeSession is { } session && session.SubAgentsCollapsed != Workbench.IsBottomCollapsed)
        {
            session.SubAgentsCollapsed = Workbench.IsBottomCollapsed;
        }
    }

    private void OnTabClosed(object? sender, TabModel tab)
    {
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
            Files.Attach(PanelContext.Empty);
            Todos.Attach(null);
            Reviews.Attach(PanelContext.Empty);
            Specs.Attach(PanelContext.Empty);
            Changes.Attach(PanelContext.Empty);
            SyncFinishedMenu(null);
            PruneSubAgentTabs(null, []);
            return;
        }

        var projectRoot = session.Info.ProjectId is { } projectId && projectRoots.TryGetValue(projectId, out var root)
            ? root
            : null;
        var context = new PanelContext(session.SessionId, session.WorkDir, projectRoot);
        Files.Attach(context);
        Todos.Attach(session.SessionId);
        Reviews.Attach(context);
        Specs.Attach(context);
        Changes.Attach(context);
        SyncSubAgentRegion();
    }

    private void OpenFile(string path, bool newTab) => _ = OpenFileAsync(path, newTab, CancellationToken.None);

    private async Task OpenFileAsync(string path, bool newTab, CancellationToken ct)
    {
        var resolved = ResolveFilePath(path);
        if (resolved is null)
        {
            return;
        }

        var id = newTab ? WorkbenchIds.PinnedFileTab(resolved) : WorkbenchIds.PreviewTab;
        if (Workbench.FindTab(id) is { Content: FileDocumentViewModel open } tab)
        {
            await open.LoadAsync(resolved, ct).ConfigureAwait(true);
            tab.Title = open.TabTitle;
            Workbench.ActivateTab(tab);
            return;
        }

        var document = FileDocumentViewModel.Create();
        await document.LoadAsync(resolved, ct).ConfigureAwait(true);
        Workbench.OpenTab(Workbench.PreviewTabs, id, TabKind.FilePreview, document, document.TabTitle);
    }

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
        await Projects.RefreshAsync(ct).ConfigureAwait(true);
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
        catch (Exception exception) when (exception is GitException or InvalidOperationException or IOException or ArgumentException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(exception.Message));
            return false;
        }
    }

    private async Task<string?> PromptTextAsync(string title, string initial) =>
        dialogs is null ? null : await dialogs.PromptTextAsync(title, initial).ConfigureAwait(true);

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
            Projects.MarkRunning(session.SessionId, session.IsRunning);
        }
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
            "IconRestore",
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
                Dispatcher.UIThread.Post(() => Status.SetInventory(inventory));
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

        clock.Stop();
        layoutSaveTimer.Stop();

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

        foreach (var session in sessions.Values)
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

        // Every runtime is released before the composition root tears its stores down.
        composition.Runtimes.Dispose();

        Workbench.TabClosed -= OnTabClosed;
        Workbench.ActiveTabChanged -= OnActiveTabChanged;
        Workbench.LayoutChanged -= OnLayoutChanged;
        Workbench.PropertyChanged -= OnWorkbenchPropertyChanged;

        // The long-lived owners subscribe to the string table themselves; they drop it with the window.
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Status.Dispose();
        Settings.Dispose();
        Projects.Dispose();
        Files.Dispose();
        Changes.Dispose();
        Todos.Dispose();
        Reviews.Dispose();
        Specs.Dispose();
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
                Trace.WriteLine("[terminal] shutdown timed out");
            }
        }
        catch (AggregateException exception)
        {
            Trace.WriteLine("[terminal] shutdown failed: " + exception.Message);
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
