using System.ComponentModel;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Appearance;
using Forge.App.Approval;
using Forge.App.Automations;
using Forge.App.Editing;
using Forge.App.Git;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Network;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Settings;
using Forge.App.Views;
using Forge.App.Services;
using Forge.Approval;
using Forge.App.Prompts;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Agents;
using Forge.Core.Net;
using Forge.Core.Prompts.Templates;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Forge.HostClient;
using Forge.IO;
using Forge.IO.Tools;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.Composition;

/// <summary>
/// The manual composition root: it constructs every service in dependency order, exposes them to the view models and
/// tears them down in the exact reverse order. There is no DI container - the graph is a dozen objects deep and a
/// container would only hide the order that the shutdown path depends on.
/// </summary>
internal sealed class AppComposition : IAsyncDisposable
{
    /// <summary>The platform settings the language subscription hangs on; null when the app has none (tests, headless).</summary>
    private readonly IPlatformSettings? platformSettings;

    /// <summary>The handler kept so <see cref="DisposeAsync"/> can detach it from the process-wide platform settings.</summary>
    private readonly EventHandler? platformLanguageChanged;

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
    private readonly SqliteTurnMetricsStore turnMetrics;
    private readonly TurnMetricsRecorder metricsRecorder;
    private readonly AgentRuntimeManager runtimes;
    private readonly PreferencesStore preferences;
    private readonly SettingsSections settings;
    private readonly AppearanceFeature appearance;
    private Forge.App.Web.WebServices? webServices;
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
        ProviderServices providers,
        GitCli git,
        SqliteSessionStore sessions,
        SqliteProjectRegistry projects,
        SqliteTodoStore todos,
        SqliteUiStateStore uiState,
        SqliteTurnCheckpointStore turnCheckpoints,
        SqliteTurnMetricsStore turnMetrics,
        TurnMetricsRecorder metricsRecorder,
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
        Providers = providers;
        Git = git;
        ApprovalsCounter = new ApprovalDatabaseCounter(approvalsDatabasePath);
        this.sessions = sessions;
        this.projects = projects;
        this.todos = todos;
        this.uiState = uiState;
        this.turnCheckpoints = turnCheckpoints;
        this.turnMetrics = turnMetrics;
        this.metricsRecorder = metricsRecorder;
        Checkpoints = checkpoints;
        Worktrees = worktrees;
        this.runtimes = runtimes;
        GitSettings = gitSettings;
        GitPanelSettings = GitPreferences.PanelSettings(preferences.Current);
        Initialization = initialization;
        preferences.Current.PropertyChanged += OnPreferencesChanged;

        // "Follow the system" stays live: the platform reports when the OS preferred language changes, and the language is
        // re-resolved from it then. A pinned language is simply applied again, which changes nothing.
        if (Application.Current?.PlatformSettings is { } platform)
        {
            platformSettings = platform;
            platformLanguageChanged = (_, _) => ApplyLanguage(preferences.Current.Language);
            platform.PreferredApplicationLanguageChanged += platformLanguageChanged;
        }
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

    /// <summary>
    /// The connected providers and their secrets. One instance for the process: the runtime factory reads its live catalogue
    /// on every turn, so a provider added in the settings window is usable by the next message without a restart.
    /// </summary>
    public ProviderServices Providers { get; }

    /// <summary>Why the extension host is not running (it could not be located), or empty. Resolved when it is shown.</summary>
    public LocText HostProblem { get; }

    public LocText WorkspaceProblem { get; }

    public LocText McpProblem { get; }

    public LocText EditorProblem { get; }

    /// <summary>
    /// The active session's tool names, set by the window when it opens one: the tools section's rows and the tools counter's
    /// denominator are the same set, and that set only exists per session.
    /// </summary>
    public Func<IReadOnlyList<string>> ActiveSessionTools { get; set; } = static () => [];

    /// <summary>
    /// The active session's tool catalog (switched-off tools included, with origin and group), set by the window; the Tools
    /// settings section lists it and offers a switch on each built-in. Empty when no session has run a turn yet.
    /// </summary>
    public Func<IReadOnlyList<Forge.Abstractions.ToolDescriptor>> ActiveSessionToolCatalog { get; set; } = static () => [];

    /// <summary>
    /// The working directory (worktree or scratch folder) of the session in front, or null. The prompts page's project
    /// scope writes to its <c>.forge</c>, the same directory that session's runtime and composer read.
    /// </summary>
    public Func<string?> ActiveSessionWorkspace { get; set; } = static () => null;

    /// <summary>
    /// The live view of one session, or null when it is not open. The window installs this when it is constructed (it owns
    /// the session view models); a step commit hands its automatic review request through it, so the hand-off lands in the
    /// session's own composer and never starts a second turn behind the user's back.
    /// </summary>
    public Func<string, IAgentMessageHost?> SessionMessageHost { get; set; } = static _ => null;

    /// <summary>
    /// The window's side of session-to-session delivery (forge-core "session messaging"): handed to every session host as
    /// <see cref="AgentHostOptions.SessionMessages"/>. The window installs itself here when it is constructed; null before
    /// that (and in a headless composition), which leaves a host's <c>message_session</c> answering that nothing is connected.
    /// </summary>
    public Forge.Core.Agents.ISessionMessageEndpoint? SessionMessagingEndpoint { get; set; }

    /// <summary>
    /// What the extension host last reported it registered (plugins, tools, commands), or null before it answered. The
    /// window status bar used to show this; it now has a line in the tools settings section.
    /// </summary>
    public PluginInventory? Inventory { get; set; }

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

    /// <summary>
    /// The per-turn metrics store (<c>turn_metrics</c>): the strip's turn history reads it, and the recorder writes it. A
    /// database from before the feature has no table and the store says so once through
    /// <see cref="ITurnMetricsStore.ExistsAsync"/>.
    /// </summary>
    public ITurnMetricsStore TurnMetrics => turnMetrics;

    /// <summary>
    /// Where oversized output is stored (tool results Core spills, and the full report a receipt's caps cut away). The receipt
    /// card's "open the full report" entry reads one artifact through it; set by <see cref="Create"/>.
    /// </summary>
    public IArtifactStore? Artifacts { get; private set; }

    /// <summary>
    /// The one turn-metrics writer of the process: a bounded queue and a background task, handed to every session so a turn
    /// end only ever does one struct channel write. Best effort by contract — see <see cref="TurnMetricsRecorder"/>.
    /// </summary>
    public TurnMetricsRecorder MetricsRecorder => metricsRecorder;

    public TurnCheckpointService Checkpoints { get; }

    public WorktreeService Worktrees { get; }

    public AgentRuntimeManager Runtimes => runtimes;

    /// <summary>Process-level workspace states (operation in progress, merge conflict) the turn-start check and the tree read.</summary>
    public WorkspaceStates WorkspaceStates { get; } = new();

    /// <summary>Registers the general workspace; awaited before the window shows any session state.</summary>
    public Task Initialization { get; }

    /// <summary>
    /// The approval queue, the remembered rules and the extension authoring adapter; set by <see cref="Create"/>. The queue
    /// overlay and the <c>extensions</c> settings section read it from here.
    /// </summary>
    public ApprovalServices? Approvals { get; private set; }

    /// <summary>The one network stack every outbound connection goes through; set by <see cref="Create"/>.</summary>
    public NetworkStack? Network { get; private set; }

    /// <summary>Which fields of the <c>network</c> section were invalid and replaced by defaults at startup, or null.</summary>
    public string? NetworkProblem { get; private set; }

    /// <summary>
    /// The one notification entry (toasts in the window, desktop notifications in the background). Created before the window
    /// so the notification overlay can resolve it from its own constructor; every feature that tells the user something
    /// goes through it.
    /// </summary>
    public NotificationCenter Notifications { get; private set; } = null!;

    /// <summary>The automatic-update checker (wave W10b stage A); built in <see cref="Create"/> after the UI-state store.</summary>
    public UpdateService Updates { get; private set; } = null!;

    /// <summary>
    /// The notification-permission guide: the runtime-settings module sets it, the first-run wizard only calls it. Null until
    /// registered, and on a platform with nothing to guide.
    /// </summary>
    public INotificationPermissionGuide? NotificationPermissionGuide { get; set; }

    /// <summary>
    /// Commits a finished TODO step into the session's worktree. The agent's <c>todo</c> tool reaches it through the
    /// runtime services; the TODO panel calls it when the user ticks an item or retries a failed commit.
    /// </summary>
    public IStepCommitter TodoStepCommitter { get; private set; } = null!;

    /// <summary>
    /// The handover sequence (lifecycle 6.2) over the real store, git, step committer, provider and review store; set by
    /// <see cref="Create"/>. Settable so the handover can run against substitutes.
    /// </summary>
    internal HandoverFlow Handover { get; set; } = null!;

    /// <summary>The parallel fork (lifecycle 6.5) over the real store, git, step committer and provider; set by <see cref="Create"/>.</summary>
    internal ForkFlow Fork { get; set; } = null!;

    /// <summary>Merge back / sync (lifecycle 6.6) over the real git; set by <see cref="Create"/>.</summary>
    internal MergeFlow Merge { get; set; } = null!;

    /// <summary>
    /// The platform trash (lifecycle 7.1): the only way a workspace directory is removed. Settable so a workspace can move into a
    /// temporary folder instead of the real recycle bin.
    /// </summary>
    internal ITrashService Trash { get; set; } = TrashService.CreateDefault();

    /// <summary>Remove / restore / purge (lifecycle 7.2-7.5) over the real store and git; set by <see cref="Create"/>.</summary>
    internal RemovalFlow Removal { get; set; } = null!;

    /// <summary>The purge file cleanup and its journal; the startup retries what an earlier purge left (7.5).</summary>
    internal PurgeCleanup PurgeCleanup { get; set; } = null!;

    /// <summary>
    /// The pull-request platform as the files panel's 提交目标 row sees it: read the branch's pull request and open one.
    /// The same instance backs the agent's <c>pr_*</c> tools; gh is resolved from the live preferences on every call.
    /// </summary>
    public IPrPlatform PullRequests { get; private set; } = null!;

    /// <summary>The automation engine, runners and watchers; attached to the main window when it opens.</summary>
    public AutomationService Automations { get; private set; } = null!;

    /// <summary>The window callbacks of the automations settings page (filled by the main window).</summary>
    public AutomationUiBridge AutomationUi { get; } = new();

    public string WorkspaceDirectory => Paths.WorkspaceDirectory;

    /// <summary>The prompt template store (commands, agents, slots); file IO, call it off the UI thread.</summary>
    public PromptTemplateService PromptTemplates { get; private set; } = new(AgentRuntimeServices.UserDirectory());

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

        // The editor options every open editor reads are a projection of the preferences, so they exist as soon as the
        // preferences do: the first file tab binds them, and the settings section below edits the same instance.
        EditorSettings.Current = new EditorSettings(preferences.Current);

        var settings = new SettingsSections();
        var appearance = AppearanceFeature.Create(preferences.Current, settings);

        // The editor section edits the live editor settings, which write through to the preferences and are persisted by
        // the store's debounce. The section's body is the settings object itself (App.axaml has its data template).
        settings.Add(new SettingsSection(
            SettingsSectionIds.Editor,
            EditorStrings.SectionTitle,
            SettingsSectionIds.EditorOrder,
            EditorSettings.Current,
            SettingsGroups.Interface,
            "IconEdit"));

        // The language section edits AppPreferences.Language only: OnPreferencesChanged pushes the value into Loc and
        // PreferencesStore persists it, so it needs nothing but the preferences.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Language,
            LanguageStrings.SectionTitle,
            SettingsSectionIds.LanguageOrder,
            new LanguageSettingsViewModel(preferences.Current),
            SettingsGroups.Interface,
            "IconLanguage"));

        // 0b. Git: the settings section edits the live preferences, so it exists as soon as they do; the section is
        //     registered here (id and order from SettingsSectionIds) like every other feature's.
        var gitSettings = new GitSettingsSectionViewModel(preferences.Current);
        settings.Add(new SettingsSection(
            SettingsSectionIds.Git,
            GitStrings.SettingsSectionTitle,
            SettingsSectionIds.GitOrder,
            gitSettings,
            SettingsGroups.Interface,
            "IconBranch"));
        // Wave W17b appends the review rows in the section's own view; SectionViewTemplate is matched before the app-level
        // templates, so the section keeps its place without an edit to the frozen App.axaml.
        SectionViewTemplate.Register<GitSettingsSectionViewModel>(static () => new GitSettingsSectionView());

        // 0c. Notifications: one entry for the whole shell. The switch is the live preference (read on every notification)
        //     and the desktop notifier is the platform's own tool, so nothing here needs a restart. Created before the
        //     window; the overlay in MainWindow resolves it from NotificationCenter.Current.
        var systemNotifier = new SystemNotifier();
        var notifications = new NotificationCenter(
            () => preferences.Current.Notifications,
            systemNotifier);
        settings.Add(new SettingsSection(
            SettingsSectionIds.Notifications,
            NotificationsStrings.SectionTitle,
            SettingsSectionIds.NotificationsOrder,
            new NotificationsSettingsViewModel(preferences.Current),
            SettingsGroups.System,
            "IconBell"));

        // 0d. Network: ONE stack for every outbound connection (providers, model catalog, web tools, MCP over HTTP). The
        //     `network` section is read before the first client exists, so the first connection already uses it; Apply on
        //     the settings page changes the resolver and proxy of every handler from its next connection on.
        var networkSettings = new NetworkSettingsStore(paths.SettingsPath);
        var network = new NetworkStack(networkSettings.Current);
        settings.Add(new SettingsSection(
            SettingsSectionIds.Network,
            NetworkStrings.SectionTitle,
            SettingsSectionIds.NetworkOrder,
            new NetworkSettingsSectionViewModel(
                networkSettings,
                network,
                (candidate, host, ct) => network.CreateResolver(candidate).ResolveAsync(host, ct).AsTask()),
            SettingsGroups.System,
            "IconGlobe"));

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
            host = ExtensionHostSupervisor.Create(layout, paths, toolRouter, notifications);
        }

        // 2b. Approvals: the remembered rules, the queue and the host adapter that turns a plugin draft into an approval
        //     card. Created before the agent runtimes because every session handler and the extension tools come from it,
        //     and before the window so the queue overlay finds it.
        //     The extensions-repository client goes through the one network stack and decompresses (repository zips are
        //     served gzip-encoded); it has no whole-response timeout because a package download is bounded by the user's
        //     cancel and the size limit, not by a timer. Ownership moves into this graph: the settings section disposes
        //     the repository service, which disposes the client.
        var repositoryHttp = new HttpClient(network.CreateHandler(new NetworkHandlerOptions
        {
            AutomaticDecompression = DecompressionMethods.All,
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        ApprovalServices approvals;
        try
        {
            approvals = ApprovalServices.Create(paths, host, hostProblem, notifications, preferences.Current, repositoryHttp);
        }
        catch
        {
            // Nothing took ownership: the section was never built, so the client and its handler are released here.
            repositoryHttp.Dispose();
            throw;
        }

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
        HttpClient? mcpHttp = null;
        try
        {
            // The MCP client mirrors the transport's own defaults (no cookies, 30 s connect, 5 min pooled lifetime, no
            // decompression, no whole-response timeout) but resolves and proxies through the network stack.
            mcpHttp = new HttpClient(network.CreateHandler(new NetworkHandlerOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            mcp = McpServices.Create(paths, preferences, notifications, mcpHttp);
            mcpHttp = null; // owned (and disposed) by the MCP services from here on
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            mcpProblem = ShellStrings.McpServiceProblem(exception.Message);
        }
        finally
        {
            // Create failed (with any exception): nobody owns the client, so its handler and pooled sockets go here.
            mcpHttp?.Dispose();
        }

        // The MCP services exist, so the approval cards can now describe the server a tool call belongs to.
        if (mcp is { } mcpServices)
        {
            approvals.McpToolInfo = mcpServices.DescribeForApproval;
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

        // 6. Providers: the stored definitions, the encrypted secret store behind them and the credential resolvers.
        var httpClient = ProviderHttpClient.Create(network);
        ProviderServices providers;
        try
        {
            providers = new ProviderServices(paths.SettingsPath, paths.UserDataDirectory, httpClient);
        }
        catch
        {
            // The composition owns this client only once it is built; a failure here must not leave it behind.
            httpClient.Dispose();
            throw;
        }

        // The catalog refresh is one service with three callers (the daily automation, the startup check, the settings
        // button); it needs the provider list it also refreshes, so it is built here rather than inside automations.
        var modelCatalogRefresh = new ModelCatalogRefreshService(providers, httpClient);
        // The per-agent defaults live in the same settings.json; one instance is shared by the settings page (writes) and
        // the session runtimes (reads), so a saved default reaches the next session without a reload.
        var agentDefaults = new AgentDefaultsStore(paths.SettingsPath);
        // The `compaction` section: edited next to the default context window, read by each session host when it is built.
        var compactionSettings = new CompactionSettingsStore(paths.SettingsPath);
        // One template service: the settings pages edit it, the runtimes, PR tool and automations resolve it per use.
        var promptTemplates = new PromptTemplateService(AgentRuntimeServices.UserDirectory());
        settings.Add(new SettingsSection(
            SettingsSectionIds.Provider,
            ProviderStrings.SectionTitleKey,
            SettingsSectionIds.ProviderOrder,
            // The catalog notifications come from pool threads (the background read, the daily refresh), so they are
            // posted to the UI thread exactly like the automations page's.
            new ProvidersSettingsViewModel(
                providers,
                agentDefaults,
                modelCatalogRefresh,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                promptTemplates,
                compactionSettings,
                preferences.Current),
            SettingsGroups.Agent,
            "IconPlug"));

        // --- web tools: settings section and tool factory ---
        var webServices = new Forge.App.Web.WebServices(
            new Forge.App.Web.WebSettingsStore(paths.SettingsPath),
            providers.Secrets,
            network,
            // DeepSeek's search API bills the same account as its model API, so a configured DeepSeek provider's key serves both.
            () => providers.Catalog.DeepSeekApiKey());
        settings.Add(new SettingsSection(
            Forge.App.Web.WebSettingsSectionViewModel.SectionId,
            WebStrings.SectionTitle,
            Forge.App.Web.WebSettingsSectionViewModel.SectionOrder,
            new Forge.App.Web.WebSettingsSectionViewModel(webServices),
            SettingsGroups.Agent,
            "IconSearch"));
        // --- end web tools ---

        // 7. Workbench storage: one forge.db under the data root for sessions, projects, todos, UI state and checkpoints.
        //    Every store runs the StorageSchema migration on its first use, so there is no separate initialize step.
        var git = workspace?.Git ?? new GitCli();
        var sessions = new SqliteSessionStore(paths.UserDataDirectory);
        var projects = new SqliteProjectRegistry(paths.UserDataDirectory);
        var todos = new SqliteTodoStore(paths.UserDataDirectory);
        var uiState = new SqliteUiStateStore(paths.UserDataDirectory);
        var turnCheckpoints = new SqliteTurnCheckpointStore(paths.UserDataDirectory);
        // The per-turn metrics table (turn_metrics) next to the checkpoints: its own store and its own writer, so a stuck
        // metrics write can never hold up the session store, and its own existence probe (an old database has no table, and
        // the whole feature then bypasses itself to a no-op).
        var turnMetrics = new SqliteTurnMetricsStore(paths.UserDataDirectory);
        var metricsRecorder = new TurnMetricsRecorder(turnMetrics);

        // The update checker (wave W10b): its own client through the one network stack, staged under <data>/updates, and
        // the UI-state store for the last-check time the automatic schedule reads. The service owns the client.
        // Pictures a reply points at by address go through the same stack (proxy, certificates); the loader bounds time and size.
        Forge.App.Services.ChatPictures.Http = new HttpClient(network.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        var updateHttp = new HttpClient(network.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var updates = new UpdateService(
            updateHttp,
            Path.Combine(paths.UserDataDirectory, "updates"),
            ReadInformationalVersion(),
            () => preferences.Current.UpdatesManifestUrl,
            () => preferences.Current.UpdatesChannel,
            () => preferences.Current.UpdatesCheckFrequency,
            uiState);
        updates.Start();

        // Artifacts (Core SPEC "循环"): oversized tool output lands in forge.db's artifacts table and <UserData>/artifacts,
        // and a permanent delete removes one session's share of both. Not disposed with the composition: disposing it
        // clears every SQLite pool of the process, and it holds nothing else.
        var artifacts = new FileArtifactStore(Path.Combine(paths.UserDataDirectory, "artifacts"), sessions.ConnectionString);
        var checkpoints = new TurnCheckpointService(git, turnCheckpoints, turnMetrics);
        var worktrees = new WorktreeService(git);

        // 8. The per-session agent runtimes. The git tools are built once for the whole app: they are stateless, take the
        //    session's worktree from each call's ToolContext, and read the live git preferences on every call.
        //    The review ledger is shared: git_diff records the reviewed HEAD that pr_create's self-review gate checks.
        var gitToolSettings = GitToolPreferences.ToolSettings(preferences.Current);
        var reviewLedger = new GitReviewLedger();
        var gitTools = new GitTools(git, gitToolSettings, reviewLedger);
        // The gh executable is a preference that is loaded after this graph is built and can change while the app runs, so
        // gh, the platform and the poller timing are all resolved from the live preferences on each use.
        var gh = new ConfiguredGh(() => preferences.Current.GitGhPath, git, gitToolSettings.NetworkTimeout);
        var prPlatform = new ConfiguredPrPlatform(gh);
        var prChecksPoller = new PrChecksPoller(prPlatform, TimeProvider.System);
        Func<PrChecksPollerOptions> checksOptions = () => GitToolPreferences.ChecksOptions(preferences.Current);
        IReadOnlyList<ITool> pullRequestTools =
        [
            .. new PrTools(
                git,
                prPlatform,
                gitToolSettings,
                reviewLedger,
                todos,
                (phase, directory) => promptTemplates.PrWorkflowOverride(phase, directory)).All,
            .. new ScreenshotTools(git, prPlatform, gitToolSettings).All,
            .. new PrCheckTools(prPlatform, prChecksPoller, checksOptions).All,
            .. new PrCommentTools(git, prPlatform, gitToolSettings, new PrReplyLedger(), workspace?.Reviews, gh.UserResolver).All,
        ];
        var runtimeServices = new AgentRuntimeServices(
            providers.CreateProvider,
            host?.Client,
            sessions,
            sessions,
            todos,
            providers.Catalog.DefaultModelSettings,
            Git: gitTools);
        // `promptCacheTtl` is per provider, so it is resolved through the catalog at host build rather than baked into one runtime
        // value; the loop then applies the "auto" policy (short until the session pauses, hour afterwards) on top of it.
        runtimeServices.PromptCacheTtl = providerId => providers.Catalog.Providers
            .FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
            ?.Definition.PromptCacheTtl;
        runtimeServices.Approvals = approvals;
        runtimeServices.PromptTemplates = promptTemplates;
        runtimeServices.AgentDefaults = agentId => agentDefaults.Resolve(agentId, providers.Catalog);
        runtimeServices.Providers = () => providers.Catalog;
        runtimeServices.IsModelHidden = model => preferences.Current.IsModelHidden(model);
        runtimeServices.SpawnModels = new SpawnModelResolver(() => providers.Catalog).Resolve;
        runtimeServices.ClaimsOfficialIdentity = () => providers.Catalog.ClaimsOfficialIdentity;
        runtimeServices.PullRequestTools = pullRequestTools;
        runtimeServices.Projects = projects;
        // Compaction thresholds follow each model's effective window: the same resolution chain the context gauge shows
        // (catalog snapshot → provider report → default context window → per-model override).
        runtimeServices.ContextWindow = model => providers.Catalog.MetadataFor(model.Model, model.ProviderId).ContextWindow;
        runtimeServices.MaxOutputTokens = model =>
            providers.Catalog.MetadataFor(model.Model, model.ProviderId).MaxOutputTokens
            ?? providers.Catalog.Find(model.ProviderId)?.Definition.MaxOutputTokens;
        // What the request is really sent with, which is the only figure worth reserving:
        // - Anthropic always carries max_tokens (its own 8192 default, or the thinking budget's room) — OutputCeiling.Wire
        //   is that same call, so the reserve cannot drift from the request.
        // - The compatible and Responses protocols write the field only when the model pins one, so the pin is the wire
        //   figure there. It must not go through the 0.2·W clamp: a pinned 100k on a 200k window would otherwise reserve
        //   40k and let the trigger line sit above the real prompt limit.
        // - With no pin those two send nothing, and the catalogue chain above stays the capability input for the 20% proxy.
        runtimeServices.WireMaxOutputTokens = model =>
            IsAnthropic(providers.Catalog, model) ? OutputCeiling.Wire(model) : model.MaxOutputTokens;
        // A model the catalog knows to take no image input gets a tool result's text, not its pictures; unknown stays
        // "may take images", the same rule the composer applies to a user attachment.
        runtimeServices.ModelInputs = model =>
            providers.Catalog.MetadataFor(model.Model, model.ProviderId).Capabilities?.Inputs;
        runtimeServices.SendToolImages = () => preferences.Current.ChatToolImagesToModel;
        // Read before every turn and re-applied to every live session on each save, so a change reaches a running turn at
        // its next model request.
        runtimeServices.CompactionChanges = compactionSettings;
        // The workspace file and shell tools. Built once and shared: they take the session's worktree from each call. The
        // options are read through a factory, so a preference change (ripgrep path, shell, bash timeout) applies to the
        // next call without rebuilding the tool surface.
        var fileTools = new FileTools(() => ToolPreferences.From(preferences.Current));
        runtimeServices.FileTools = fileTools.All;
        // Switched-off built-in tools: read before every turn, so a toggle applies from the next turn of every session.
        runtimeServices.DisabledTools = () => preferences.Current.ToolsDisabled;
        // Artifact spill and preview sizes: read when a session host is built (both live in AgentOptions), so a change
        // applies to the next host. The preview is also capped at half the spill budget, so raising one never makes the
        // preview larger than what stays inline.
        runtimeServices.ArtifactLimits = () =>
        {
            var spread = preferences.Current.ChatArtifactSpillChars;
            return (spread, Math.Min(preferences.Current.ChatArtifactPreviewChars, spread / 2));
        };
        // The two sub-agent dials of the `agents` section: the cap is applied to every host as it is built and again before
        // each turn (a raised cap wakes agents already queued), and switching spawning off takes spawn_agent out of the tools.
        runtimeServices.SubAgents = () => SubAgentLimits.From(preferences.Current);
        // The `tools` section's four run_code dials (wave W14): read on use, so a change reaches the next turn's
        // description and every later script without rebuilding a session host.
        runtimeServices.CodeMode = () => CodeModeSettings.From(preferences.Current);
        runtimeServices.WebTools = webServices.CreateTools; // web tools
        // Late-bound: the window installs SessionMessageHost on the composition it receives, and the composition is only
        // created further down. A step commit reads the hook at the moment it needs a session, long after that.
        AppComposition? created = null;
        var stepCommitter = new StepCommitter(
            git,
            todos,
            StepCommitter.WorktreeOf(sessions),
            () => preferences.Current.GitStepCommits,
            notifications,
            StepReviewSettings.Live(preferences.Current),
            new StepReviewMessenger(
                sessionId => created?.SessionMessageHost(sessionId),
                DispatcherUiThread.Instance));
        runtimeServices.StepCommitter = stepCommitter;
        // Core has no git: the review change set is built here over the app's git layer and handed to the host.
        runtimeServices.Reviews = new ReviewChangeSetSource(git);
        // The step-completion gate's git side, over the same worktree resolution the step committer uses: a session with
        // no worktree of its own (general scratch) and a sub-agent session are not gated, because they never commit a step.
        runtimeServices.StepChanges = new GitStepChangeProbe(git, StepCommitter.WorktreeOf(sessions));
        runtimeServices.StepReviewRequired = () => preferences.Current.StepReviewRequired;
        runtimeServices.ReviewToolEnabled = () => preferences.Current.ReviewToolEnabled;
        runtimeServices.ReviewFix = () => new Forge.Core.Tools.ReviewFixPolicy(preferences.Current.ReviewAutoFix, preferences.Current.ReviewAutoFixMaxRounds);
        // The default orchestration is a prompt snapshot taken when a host is built, like the prompt templates above.
        runtimeServices.OrchestrationEnabled = () => preferences.Current.OrchestrationEnabled;
        runtimeServices.ReviewLimits = () => GitToolPreferences.ReviewLimits(preferences.Current);
        // Each finished review is recorded into reviews.db (wave W17b), so a verdict outlives the turn that produced it. The
        // same store the review panels use; without a workspace services block the fallback is the same reviews directory
        // the purge path falls back to.
        var reviewStore = workspace?.Reviews ?? new ReviewStore(paths.ReviewsDirectory);
        runtimeServices.ReviewConclusions = new ReviewConclusionStore(reviewStore);
        runtimeServices.StepReviewStore = new StepReviewLedgerStore(reviewStore);
        runtimeServices.Mcp = mcp;
        runtimeServices.ToolRouter = toolRouter;
        runtimeServices.Artifacts = artifacts;
        // Late-bound like the step committer above: the window (which runs the root turns and owns the transcripts) is only
        // created after this composition, and the host reads the seam when a delivery actually happens.
        runtimeServices.SessionMessages = () => created?.SessionMessagingEndpoint;
        var runtimes = new AgentRuntimeManager(runtimeServices);

        var initialization = InitializeWorkbenchAsync(projects, worktrees, paths, preferences, appearance);

        // The extensions settings section: installed extensions, staged drafts and the remembered approval rules.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Extensions,
            ExtensionsStrings.SettingsSectionTitle,
            SettingsSectionIds.ExtensionsOrder,
            approvals.SettingsSection,
            SettingsGroups.System,
            "IconSparkle"));

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
            providers,
            git,
            sessions,
            projects,
            todos,
            uiState,
            turnCheckpoints,
            turnMetrics,
            metricsRecorder,
            checkpoints,
            worktrees,
            runtimes,
            gitSettings,
            initialization);
        composition.Approvals = approvals;
        composition.Artifacts = artifacts;
        composition.Network = network;
        composition.NetworkProblem = networkSettings.LoadProblem;
        composition.Updates = updates;
        composition.webServices = webServices;
        composition.PromptTemplates = promptTemplates;
        composition.TodoStepCommitter = stepCommitter;
        composition.PullRequests = prPlatform;
        var reviews = workspace?.Reviews;
        composition.Handover = new HandoverFlow(
            sessions,
            sessions,
            new HandoverGit(git, stepCommitter),
            (history, tree, ct) => composition.SummarizeHandoverAsync(history, tree, ct),
            reviews is null
                ? null
                : async (directory, from, to, ct) => await reviews.CopySessionAsync(directory, from, to, ct).ConfigureAwait(false));
        composition.Fork = new ForkFlow(sessions, new ForkGit(git, stepCommitter, worktrees));
        composition.Merge = new MergeFlow(new MergeGit(git));
        var purgeReviews = reviews ?? new ReviewStore(paths.ReviewsDirectory);
        var purgeComposer = new SessionComposerStore(uiState);
        composition.PurgeCleanup = new PurgeCleanup(
            new PurgeCleanupJournal(Path.Combine(paths.UserDataDirectory, "purge-pending.txt")),
            (sessionId, ct) => PurgeSessionFilesAsync(artifacts, purgeReviews, purgeComposer, sessionId, ct),
            async (sessionId, ct) => await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false) is not null);
        composition.Removal = new RemovalFlow(
            sessions,
            sessions,
            new RemovalGit(git, worktrees.Manager),
            (directory, ct) => composition.Trash.MoveToTrashAsync(directory, ct),
            composition.PurgeCleanup.CleanUpAsync);
        composition.Notifications = notifications;

        // Automations: the engine and its stores (automations.json beside settings.json, runs in forge.db), the built-in
        // runners and the CI watcher. The engine loop starts when the main window attaches (it delivers to sessions).
        var automations = AutomationComposition.Create(
            Path.GetDirectoryName(paths.SettingsPath) ?? paths.UserDataDirectory,
            paths.UserDataDirectory,
            projects,
            sessions,
            sessions,
            worktrees,
            providers.CreateProvider,
            () => providers.Catalog.DefaultModelSettings,
            prPlatform,
            git,
            notifications,
            modelCatalogRefresh,
            promptTemplates,
            idleArchive: () => IdleArchiveSettings.From(preferences.Current));
        composition.Automations = automations;
        settings.Add(AutomationComposition.Section(automations, projects, sessions, composition.AutomationUi));

        // The four instrument sections: the click target of each counter in the session strip. They read the composition
        // lazily (on the settings page opening), so they are registered here, still inside the composition root, once the
        // instance they read exists.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Approvals,
            InstrumentsStrings.ApprovalsSectionTitleKey,
            SettingsSectionIds.ApprovalsOrder,
            new ApprovalsSettingsSectionViewModel(composition),
            SettingsGroups.Agent,
            "IconShieldCheck"));
        settings.Add(new SettingsSection(
            SettingsSectionIds.Skills,
            InstrumentsStrings.SkillsSectionTitleKey,
            SettingsSectionIds.SkillsOrder,
            new SkillsSettingsSectionViewModel(composition),
            SettingsGroups.Agent,
            "IconBook"));
        // Prompt templates: the project scope is the front session's working directory, which is what sessions resolve
        // (<worktree>/.forge); without a session in front the page offers only the global scope.
        settings.Add(Forge.App.Settings.Prompts.PromptsSettingsSection.Create(
            composition.PromptTemplates,
            () => composition.ActiveSessionWorkspace() is { Length: > 0 } directory ? directory : null));
        settings.Add(new SettingsSection(
            SettingsSectionIds.Mcp,
            InstrumentsStrings.McpSectionTitleKey,
            SettingsSectionIds.McpOrder,
            new McpSettingsSectionViewModel(composition),
            SettingsGroups.Agent,
            "IconServer"));
        settings.Add(new SettingsSection(
            SettingsSectionIds.Tools,
            InstrumentsStrings.ToolsSectionTitleKey,
            SettingsSectionIds.ToolsOrder,
            new ToolsSettingsSectionViewModel(composition),
            SettingsGroups.Agent,
            "IconWrench"));

        FeatureComposition.RegisterAll(composition, settings);
        created = composition;
        return composition;
    }

    /// <summary>Re-reads <c>settings.json</c>; the running runtime keeps its provider until the next launch.</summary>
    /// <summary>Re-reads <c>settings.json</c>; running sessions keep the catalogue they captured until their next turn.</summary>
    public ProviderCatalog ReloadProvider() => Providers.Reload();

    /// <summary>The same reload off the UI thread, for the settings page's refresh button.</summary>
    public Task<ProviderCatalog> ReloadProviderAsync(CancellationToken cancellationToken = default) =>
        Providers.ReloadAsync(cancellationToken);

    /// <summary>
    /// What a session removal deletes besides the catalog rows (7.2): the session's artifacts (their <c>artifacts</c> rows
    /// and its <c>&lt;UserData&gt;/artifacts/&lt;id&gt;</c> folder), its review marks and comments, and its stored composer draft.
    /// Each part runs even when another failed, so one stuck file does not leave the rest behind; every failure is then
    /// reported in one exception, which <see cref="RemovalFlow"/> shows as a partial cleanup. Every part deletes by session
    /// id only and is idempotent.
    /// </summary>
    internal static async Task PurgeSessionFilesAsync(
        IArtifactStore artifacts,
        ReviewStore reviews,
        SessionComposerStore composer,
        string sessionId,
        CancellationToken ct)
    {
        List<Exception>? failures = null;
        try
        {
            await artifacts.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            (failures ??= []).Add(exception);
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            await reviews.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            (failures ??= []).Add(exception);
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            await composer.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new IOException(
                string.Join("; ", failures.Select(static f => f.Message)),
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }

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
        // The product's own skills are written out before any session host is built (the shell awaits this task before it
        // restores tabs), so the first root agent already sees them in its skill index. Off the UI thread: it is file IO.
        AgentRuntimeServices.ProductSkillsDirectory = await Task
            .Run(() => MaterializeBuiltInSkills(paths.UserDataDirectory))
            .ConfigureAwait(false);        // The loaded values reach the limiter here, for the same reason the appearance feature re-applies: ApplyLoaded raises no
        // change notification, so the ones wired in Register only see edits the user makes after this point.
        RateLimitComposition.Publish(preferences.Current);

        var general = await worktrees.EnsureGeneralWorkspaceAsync(paths.GeneralWorkspaceDirectory, CancellationToken.None)
            .ConfigureAwait(false);
        await projects.AddAsync(general, WorktreeService.GeneralFolderName, ProjectKind.General, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The version the update checker compares: the assembly <c>InformationalVersion</c>, which CI stamps with
    /// <c>-p:Version</c> (a tag builds <c>1.2.0</c>, any other CI build <c>0.0.0-ci.&lt;run&gt;</c>).
    /// </summary>
    private static string ReadInformationalVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// Whether this model is served over the Anthropic protocol — the one whose requests always carry <c>max_tokens</c>, so
    /// the only one with an output cap to reserve. Resolution is the provider factory's: the named provider, else the default.
    /// </summary>
    private static bool IsAnthropic(ProviderCatalog catalog, ModelSettings model) =>
        (catalog.Find(model.ProviderId) ?? catalog.Default)?.Definition.Protocol == ProviderProtocol.Anthropic;

    /// <summary>
    /// Writes the product's own skills where the skill catalog and the <c>skill</c> tool can reach them. A failure is not
    /// fatal: the app runs with the workspace's and the user's skills only, and the reason is traced.
    /// </summary>
    private static string? MaterializeBuiltInSkills(string userDataDirectory)
    {
        var directory = Skills.BuiltInSkills.Materialize(userDataDirectory);
        if (directory is null)
        {
            AppLogs.Prompts.ZLogWarning($"the built-in skills could not be written under {userDataDirectory}; only workspace and user skills are offered");
        }

        return directory;
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

    /// <summary>
    /// The handover summary over the provider configured right now (a reload in settings applies to the next handover), with
    /// that provider's default model. No provider configured is a summary failure the dialog reports.
    /// </summary>
    private Task<string> SummarizeHandoverAsync(IReadOnlyList<ChatMessage> history, string? tree, CancellationToken ct)
    {
        if (Providers.Catalog.Default is not { } provider)
        {
            throw new HandoverSummaryException(Providers.StatusText.Resolve());
        }

        return HandoverSummary.GenerateAsync(
            provider.CreateProvider(httpClient),
            provider.Definition.DefaultModelSettings,
            history,
            tree,
            ct,
            PromptTemplates.Resolve(workspace: null).FindSlot(PromptSlotIds.HandoverSummary));
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

        // Before the runtimes: the engine stops its loop and cancels in-flight runs (waiting-for-quota runs stay recorded).
        if (Automations is { } automations)
        {
            await automations.DisposeAsync().ConfigureAwait(false);
        }

        runtimes.Dispose();

        // After the runtimes: every session handler is released first, then the remaining cards are withdrawn as denials
        // and a pending rules write is flushed on the pool.
        if (Approvals is { } approvals)
        {
            await approvals.DisposeAsync().ConfigureAwait(false);
        }

        // The metrics writer first: it drains what it already holds (bounded) and only then does the store it writes through
        // go away. Reverse construction order otherwise.
        await metricsRecorder.DisposeAsync().ConfigureAwait(false);
        await turnMetrics.DisposeAsync().ConfigureAwait(false);
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
        webServices?.Dispose();
        Updates?.Dispose();
        Network?.Dispose();

        // Last of the services: a session may still have been finishing a turn while everything above was released.
        if (Notifications is { } notifications)
        {
            notifications.Dispose();
        }

        preferences.Current.PropertyChanged -= OnPreferencesChanged;

        if (platformSettings is { } platform && platformLanguageChanged is { } languageHandler)
        {
            platform.PreferredApplicationLanguageChanged -= languageHandler;
        }

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
    /// by a plain host has neither, and the static defaults in <c>Tokens.axaml</c> then stand).
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
            settings.Add(new SettingsSection(
                SettingsSectionIds.Appearance,
                AppearanceStrings.SectionTitle,
                SettingsSectionIds.AppearanceOrder,
                section,
                SettingsGroups.Interface,
                "IconColor"));

            // AvaloniaObject.CheckAccess, not Dispatcher.UIThread: reading the latter off the UI thread creates a dispatcher there.
            if (Application.Current is { } app && app.CheckAccess())
            {
                return new AppearanceFeature(section, new AppearanceApplier(preferences, app.Resources, app), app.Dispatcher);
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
                // No UI thread (a headless composition): nothing is bound, so the editors are synced right here.
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
