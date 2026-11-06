using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Appearance;
using Forge.App.Approval;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Approval;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Forge.HostClient;
using Forge.Workspace;

namespace Forge.App.Composition;

/// <summary>
/// The manual composition root: it constructs every service in dependency order, exposes them to the view models and
/// tears them down in the exact reverse order. There is no DI container - the graph is a dozen objects deep and a
/// container would only hide the order that the shutdown path depends on.
/// </summary>
internal sealed class AppComposition : IAsyncDisposable
{
    private readonly HttpClient httpClient;
    private readonly WorkspaceServices? workspace;
    private readonly McpServices? mcp;
    private readonly EditorServices? editor;
    private readonly ExtensionHostSupervisor? host;
    private readonly SqliteSessionStore sessions;
    private readonly SqliteProjectRegistry projects;
    private readonly SqliteTodoStore todos;
    private readonly SqliteUiStateStore uiState;
    private readonly SqliteTurnCheckpointStore turnCheckpoints;
    private readonly AgentRuntimeManager runtimes;
    private readonly PreferencesStore preferences;
    private readonly SettingsSections settings;
    private readonly AppearanceFeature appearance;
    private bool disposed;

    private AppComposition(
        AppPaths paths,
        PreferencesStore preferences,
        SettingsSections settings,
        AppearanceFeature appearance,
        HttpClient httpClient,
        string approvalsDatabasePath,
        ExtensionHostSupervisor? host,
        LocText hostProblem,
        WorkspaceServices? workspace,
        LocText workspaceProblem,
        McpServices? mcp,
        LocText mcpProblem,
        EditorServices? editor,
        LocText editorProblem,
        ProviderSettings provider,
        GitCli git,
        SqliteSessionStore sessions,
        SqliteProjectRegistry projects,
        SqliteTodoStore todos,
        SqliteUiStateStore uiState,
        SqliteTurnCheckpointStore turnCheckpoints,
        TurnCheckpointService checkpoints,
        WorktreeService worktrees,
        AgentRuntimeManager runtimes,
        GitSettingsSectionViewModel gitSettings,
        Task initialization)
    {
        Paths = paths;
        this.preferences = preferences;
        this.settings = settings;
        this.appearance = appearance;
        this.httpClient = httpClient;
        ApprovalsDatabasePath = approvalsDatabasePath;
        this.host = host;
        HostProblem = hostProblem;
        this.workspace = workspace;
        WorkspaceProblem = workspaceProblem;
        this.mcp = mcp;
        McpProblem = mcpProblem;
        this.editor = editor;
        EditorProblem = editorProblem;
        Provider = provider;
        Git = git;
        ApprovalsCounter = new ApprovalDatabaseCounter(approvalsDatabasePath);
        this.sessions = sessions;
        this.projects = projects;
        this.todos = todos;
        this.uiState = uiState;
        this.turnCheckpoints = turnCheckpoints;
        Checkpoints = checkpoints;
        Worktrees = worktrees;
        this.runtimes = runtimes;
        GitSettings = gitSettings;
        GitPanelSettings = GitPreferences.PanelSettings(preferences.Current);
        Initialization = initialization;
        preferences.Current.PropertyChanged += OnPreferencesChanged;
    }

    public AppPaths Paths { get; }

    /// <summary>The live user preferences; every setter applies immediately and the store persists after a short debounce.</summary>
    public AppPreferences Preferences => preferences.Current;

    /// <summary>Why the last read of the <c>preferences</c> section fell back to defaults, or null.</summary>
    public string? PreferencesProblem => preferences.LoadProblem;

    /// <summary>
    /// The settings-page sections. A feature appends its own here in <see cref="Create"/> (id and order come from
    /// <see cref="SettingsSectionIds"/>) plus one app-level DataTemplate, and never edits the settings page.
    /// </summary>
    public SettingsSections Settings => settings;

    public ProviderSettings Provider { get; private set; }

    /// <summary>Why the extension host is not running (it could not be located), or empty. Resolved when it is shown.</summary>
    public LocText HostProblem { get; }

    public LocText WorkspaceProblem { get; }

    public LocText McpProblem { get; }

    public LocText EditorProblem { get; }

    /// <summary>Null when the JIT side was not found; the status bar then says so instead of the app failing to start.</summary>
    public ExtensionHostSupervisor? Host => host;

    public WorkspaceServices? Workspace => workspace;

    public McpServices? Mcp => mcp;

    public EditorServices? Editor => editor;

    /// <summary>The approval database the extension host owns; the App only reads its record count.</summary>
    public string ApprovalsDatabasePath { get; }

    /// <summary>
    /// The status bar's approval record count, read over a read-only connection. Counting is the only thing the App
    /// reads the approval database for; every decision comes from <c>plugins.status</c>.
    /// </summary>
    public IApprovalCounter ApprovalsCounter { get; }

    public GitCli Git { get; }

    /// <summary>The <c>git</c> settings section's view model; it edits <see cref="Preferences"/> live.</summary>
    public GitSettingsSectionViewModel GitSettings { get; }

    /// <summary>What the inspector's Git tab reads on every operation (pull strategy and network timeout).</summary>
    public GitPanelSettings GitPanelSettings { get; }

    public ISessionStore Sessions => sessions;

    public ISessionCatalog Catalog => sessions;

    public IProjectRegistry Projects => projects;

    public ITodoStore Todos => todos;

    public IUiStateStore UiState => uiState;

    public ITurnCheckpointStore TurnCheckpoints => turnCheckpoints;

    public TurnCheckpointService Checkpoints { get; }

    public WorktreeService Worktrees { get; }

    public AgentRuntimeManager Runtimes => runtimes;

    /// <summary>Registers the general workspace; awaited before the window shows any session state.</summary>
    public Task Initialization { get; }

    /// <summary>
    /// The approval queue, the remembered rules and the extension authoring adapter; set by <see cref="Create"/>. The queue
    /// overlay and the <c>extensions</c> settings section read it from here.
    /// </summary>
    public ApprovalServices? Approvals { get; private set; }

    public string WorkspaceDirectory => Paths.WorkspaceDirectory;

    public static AppComposition Create()
    {
        // 0. Preferences own the `preferences` section of the same settings.json the provider reads. Constructed
        //    before everything else so the debounced write is the last thing flushed on shutdown. The settings registry
        //    is created here too: a feature adds its section below, once the service it edits exists. The live string
        //    table is touched first of all: every table registers from its own [ModuleInitializer], and this is the
        //    explicit order point before any view model asks for a text.
        Loc.EnsureRegistered();

        var paths = AppPaths.Create();
        var preferences = new PreferencesStore(paths.SettingsPath);
        var settings = new SettingsSections();
        var appearance = AppearanceFeature.Create(preferences.Current, settings);

        // The language section edits AppPreferences.Language only: OnPreferencesChanged pushes the value into Loc and
        // PreferencesStore persists it, so it needs nothing but the preferences.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Language,
            LanguageStrings.SectionTitle,
            SettingsSectionIds.LanguageOrder,
            new LanguageSettingsViewModel(preferences.Current)));

        // 0b. Git: the settings section edits the live preferences, so it exists as soon as they do; the section is
        //     registered here (id and order from SettingsSectionIds) like every other feature's.
        var gitSettings = new GitSettingsSectionViewModel(preferences.Current);
        settings.Add(new SettingsSection(SettingsSectionIds.Git, GitStrings.SettingsSectionTitle, SettingsSectionIds.GitOrder, gitSettings));

        // 1. The approval database is owned by the extension host; the shell only reads its record count.
        var approvalsDatabasePath = Path.Combine(paths.HostDataDirectory, ApprovalStore.FileName);

        // 2. Extension host client (AOT side of the JIT boundary; the client owns the child process).
        ExtensionHostSupervisor? host = null;
        var toolRouter = new SessionToolCallRouter();
        var hostProblem = default(LocText);
        var layout = JitHostLayout.Locate(out var locateProblem);
        if (layout is null)
        {
            hostProblem = locateProblem;
        }
        else
        {
            host = ExtensionHostSupervisor.Create(layout, paths, toolRouter);
        }

        // 2b. Approvals: the remembered rules, the queue and the host adapter that turns a plugin draft into an approval
        //     card. Created before the agent runtimes because every session handler and the extension tools come from it,
        //     and before the window so the queue overlay finds it.
        var approvals = ApprovalServices.Create(paths, host, hostProblem);

        // 3. Workspace.
        WorkspaceServices? workspace = null;
        var workspaceProblem = default(LocText);
        try
        {
            workspace = WorkspaceServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            workspaceProblem = ShellStrings.WorkspaceServiceProblem(exception.Message);
        }

        // 4. MCP.
        McpServices? mcp = null;
        var mcpProblem = default(LocText);
        try
        {
            mcp = McpServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            mcpProblem = ShellStrings.McpServiceProblem(exception.Message);
        }

        // 5. Editor.
        EditorServices? editor = null;
        var editorProblem = default(LocText);
        try
        {
            editor = EditorServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            editorProblem = ShellStrings.EditorServiceProblem(exception.Message);
        }

        // 6. Provider settings.
        var provider = ProviderSettings.Load(paths.SettingsPath);
        var httpClient = ProviderHttpClient.Create();

        // 7. Workbench storage: one forge.db under the data root for sessions, projects, todos, UI state and checkpoints.
        //    Every store runs the StorageSchema migration on its first use, so there is no separate initialize step.
        var git = workspace?.Git ?? new GitCli();
        var sessions = new SqliteSessionStore(paths.UserDataDirectory);
        var projects = new SqliteProjectRegistry(paths.UserDataDirectory);
        var todos = new SqliteTodoStore(paths.UserDataDirectory);
        var uiState = new SqliteUiStateStore(paths.UserDataDirectory);
        var turnCheckpoints = new SqliteTurnCheckpointStore(paths.UserDataDirectory);
        var checkpoints = new TurnCheckpointService(git, turnCheckpoints);
        var worktrees = new WorktreeService(git);

        // 8. The per-session agent runtimes. The git tools are built once for the whole app: they are stateless, take the
        //    session's worktree from each call's ToolContext, and read the live git preferences on every call.
        var gitTools = new GitTools(git, GitToolPreferences.ToolSettings(preferences.Current), new GitReviewLedger());
        var runtimeServices = new AgentRuntimeServices(
            BuildProviderFactory(provider, httpClient),
            host?.Client,
            sessions,
            sessions,
            todos,
            provider.Provider?.ModelSettings ?? new ModelSettings(string.Empty),
            Git: gitTools);
        runtimeServices.Approvals = approvals;
        runtimeServices.ToolRouter = toolRouter;
        var runtimes = new AgentRuntimeManager(runtimeServices);

        var initialization = InitializeWorkbenchAsync(projects, worktrees, paths, preferences, appearance);

        // The extensions settings section: installed extensions, staged drafts and the remembered approval rules.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Extensions,
            ExtensionsStrings.SettingsSectionTitle,
            SettingsSectionIds.ExtensionsOrder,
            approvals.SettingsSection));

        var composition = new AppComposition(
            paths,
            preferences,
            settings,
            appearance,
            httpClient,
            approvalsDatabasePath,
            host,
            hostProblem,
            workspace,
            workspaceProblem,
            mcp,
            mcpProblem,
            editor,
            editorProblem,
            provider,
            git,
            sessions,
            projects,
            todos,
            uiState,
            turnCheckpoints,
            checkpoints,
            worktrees,
            runtimes,
            gitSettings,
            initialization);
        composition.Approvals = approvals;
        return composition;
    }

    /// <summary>Re-reads <c>settings.json</c>; the running runtime keeps its provider until the next launch.</summary>
    public ProviderSettings ReloadProvider() => Provider = ProviderSettings.Load(Paths.SettingsPath);

    /// <summary>
    /// Startup work the window waits for: the preferences are read on the pool and then applied here, and the general
    /// workspace is registered. The first await has no <c>ConfigureAwait(false)</c> on purpose - the caller is the UI
    /// thread, and the language is pushed into <see cref="Loc"/> (which notifies bound text) before anything is bound.
    /// </summary>
    private static async Task InitializeWorkbenchAsync(
        SqliteProjectRegistry projects,
        WorktreeService worktrees,
        AppPaths paths,
        PreferencesStore preferences,
        AppearanceFeature appearance)
    {
        await preferences.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        ApplyLanguage(preferences.Current.Language);
        appearance.ApplyLoaded();

        var general = await worktrees.EnsureGeneralWorkspaceAsync(paths.GeneralWorkspaceDirectory, CancellationToken.None)
            .ConfigureAwait(false);
        await projects.AddAsync(general, WorktreeService.GeneralFolderName, ProjectKind.General, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="Loc"/> raises <c>Item[]</c> and <see cref="Loc.LanguageChanged"/> to whatever is bound, so the language
    /// is only ever set on the UI thread. The pool thread of a host without a dispatcher loop posts and never runs it,
    /// which is the one case where nothing is bound anyway.
    /// </summary>
    private static void ApplyLanguage(UiLanguage language)
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
        {
            Loc.Instance.Language = language;
            return;
        }

        dispatcher.Post(() => Loc.Instance.Language = language);
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AppPreferences.Language), StringComparison.Ordinal))
        {
            ApplyLanguage(preferences.Current.Language);
        }
    }

    private static Func<ModelSettings, IChatProvider> BuildProviderFactory(ProviderSettings settings, HttpClient httpClient)
    {
        if (settings.Provider is not { } snapshot)
        {
            return static _ => UnconfiguredChatProvider.Instance;
        }

        return _ => snapshot.CreateProvider(httpClient);
    }

    /// <summary>
    /// Shuts the graph down in reverse construction order. The host client kills and waits for the child process tree
    /// here, and the shell never held a writable approval connection that could outlive it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        appearance.Dispose();

        // A section's view model may subscribe to the process-wide Loc; Loc.Instance outlives this graph, so an
        // undisposed section would stay rooted (with the preferences it holds) after the composition is gone.
        foreach (var section in settings.Items)
        {
            (section.ViewModel as IDisposable)?.Dispose();
        }

        runtimes.Dispose();

        // After the runtimes: every session handler is released first, then the remaining cards are withdrawn as denials
        // and a pending rules write is flushed on the pool.
        if (Approvals is { } approvals)
        {
            await approvals.DisposeAsync().ConfigureAwait(false);
        }

        await turnCheckpoints.DisposeAsync().ConfigureAwait(false);
        await uiState.DisposeAsync().ConfigureAwait(false);
        await todos.DisposeAsync().ConfigureAwait(false);
        await projects.DisposeAsync().ConfigureAwait(false);
        await sessions.DisposeAsync().ConfigureAwait(false);

        if (editor is not null)
        {
            await editor.DisposeAsync().ConfigureAwait(false);
        }

        if (mcp is not null)
        {
            await mcp.DisposeAsync().ConfigureAwait(false);
        }

        workspace?.Dispose();

        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        httpClient.Dispose();

        preferences.Current.PropertyChanged -= OnPreferencesChanged;
        await preferences.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Last-resort teardown from <see cref="AppDomain.ProcessExit"/>, where nothing can be awaited. Bounded, and only the
    /// child process matters: an orphaned extension host would outlive the window.
    /// </summary>
    public void Abort()
    {
        if (host is null)
        {
            return;
        }

        try
        {
            host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The font feature's two pieces: the settings section (always) and the applier that writes the font tokens into the
    /// application resources (only when an <see cref="Application"/> exists and this is its thread - a composition built
    /// by a plain unit test has neither, and the static defaults in <c>Tokens.axaml</c> then stand).
    /// </summary>
    private sealed class AppearanceFeature : IDisposable
    {
        private readonly AppearanceSettingsViewModel section;
        private readonly AppearanceApplier? applier;
        private readonly Dispatcher? dispatcher;
        private bool disposed;

        private AppearanceFeature(AppearanceSettingsViewModel section, AppearanceApplier? applier, Dispatcher? dispatcher)
        {
            this.section = section;
            this.applier = applier;
            this.dispatcher = dispatcher;
        }

        public static AppearanceFeature Create(AppPreferences preferences, SettingsSections settings)
        {
            var section = new AppearanceSettingsViewModel(preferences, SystemFontCatalog.CreateDefault());
            settings.Add(new SettingsSection(SettingsSectionIds.Appearance, AppearanceStrings.SectionTitle, SettingsSectionIds.AppearanceOrder, section));

            // AvaloniaObject.CheckAccess, not Dispatcher.UIThread: reading the latter off the UI thread creates a dispatcher there.
            if (Application.Current is { } app && app.CheckAccess())
            {
                return new AppearanceFeature(section, new AppearanceApplier(preferences, app.Resources), app.Dispatcher);
            }

            return new AppearanceFeature(section, applier: null, dispatcher: null);
        }

        /// <summary>
        /// The load raises no PropertyChanged, so the loaded values are pushed once on the UI thread: the applier re-reads the
        /// fonts and the section re-syncs its editors (they were built from the defaults, before the load).
        /// </summary>
        public void ApplyLoaded()
        {
            if (dispatcher is null)
            {
                // No UI thread (a unit-test composition): nothing is bound, so the editors are synced right here.
                SyncAndApply();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                SyncAndApply();
                return;
            }

            dispatcher.Post(SyncAndApply);
        }

        private void SyncAndApply()
        {
            if (Volatile.Read(ref disposed))
            {
                return;
            }

            section.SyncAll();
            applier?.Apply();
        }

        public void Dispose()
        {
            Volatile.Write(ref disposed, true);
            applier?.Dispose();
            section.Dispose();
        }
    }
}
