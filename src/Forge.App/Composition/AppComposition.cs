using Forge.Abstractions;
using Forge.App.Agents;
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
    private bool disposed;

    private AppComposition(
        AppPaths paths,
        HttpClient httpClient,
        string approvalsDatabasePath,
        ExtensionHostSupervisor? host,
        string? hostProblem,
        WorkspaceServices? workspace,
        string? workspaceProblem,
        McpServices? mcp,
        string? mcpProblem,
        EditorServices? editor,
        string? editorProblem,
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
        Task initialization)
    {
        Paths = paths;
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
        Initialization = initialization;
    }

    public AppPaths Paths { get; }

    public ProviderSettings Provider { get; private set; }

    public string? HostProblem { get; }

    public string? WorkspaceProblem { get; }

    public string? McpProblem { get; }

    public string? EditorProblem { get; }

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

    public string WorkspaceDirectory => Paths.WorkspaceDirectory;

    public static AppComposition Create()
    {
        var paths = AppPaths.Create();

        // 1. The approval database is owned by the extension host; the shell only reads its record count.
        var approvalsDatabasePath = Path.Combine(paths.HostDataDirectory, ApprovalStore.FileName);

        // 2. Extension host client (AOT side of the JIT boundary; the client owns the child process).
        ExtensionHostSupervisor? host = null;
        string? hostProblem = null;
        var layout = JitHostLayout.Locate(out var locateProblem);
        if (layout is null)
        {
            hostProblem = locateProblem;
        }
        else
        {
            host = ExtensionHostSupervisor.Create(layout, paths);
        }

        // 3. Workspace.
        WorkspaceServices? workspace = null;
        string? workspaceProblem = null;
        try
        {
            workspace = WorkspaceServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            workspaceProblem = "工作区服务不可用：" + exception.Message;
        }

        // 4. MCP.
        McpServices? mcp = null;
        string? mcpProblem = null;
        try
        {
            mcp = McpServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            mcpProblem = "MCP 服务不可用：" + exception.Message;
        }

        // 5. Editor.
        EditorServices? editor = null;
        string? editorProblem = null;
        try
        {
            editor = EditorServices.Create(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            editorProblem = "编辑器服务不可用：" + exception.Message;
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

        // 8. The per-session agent runtimes.
        var runtimeServices = new AgentRuntimeServices(
            BuildProviderFactory(provider, httpClient),
            host?.Client,
            sessions,
            sessions,
            todos,
            provider.Provider?.ModelSettings ?? new ModelSettings(string.Empty));
        var runtimes = new AgentRuntimeManager(runtimeServices);

        var initialization = InitializeWorkbenchAsync(projects, worktrees, paths);

        return new AppComposition(
            paths,
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
            initialization);
    }

    /// <summary>Re-reads <c>settings.json</c>; the running runtime keeps its provider until the next launch.</summary>
    public ProviderSettings ReloadProvider() => Provider = ProviderSettings.Load(Paths.SettingsPath);

    private static async Task InitializeWorkbenchAsync(
        SqliteProjectRegistry projects,
        WorktreeService worktrees,
        AppPaths paths)
    {
        var general = await worktrees.EnsureGeneralWorkspaceAsync(paths.GeneralWorkspaceDirectory, CancellationToken.None)
            .ConfigureAwait(false);
        await projects.AddAsync(general, WorktreeService.GeneralFolderName, ProjectKind.General, CancellationToken.None)
            .ConfigureAwait(false);
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

        runtimes.Dispose();
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
}
