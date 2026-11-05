using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>
/// Main-program side of the Extension Host contract. It owns the host process and the IPC connection, keeps
/// atomic snapshots of what the host registered (tool proxies and the non-UI contributions) and relays
/// plugin→main-program tool calls through the caller's resolver and approval path so the client never bypasses
/// permission checks.
/// </summary>
public sealed partial class ExtensionHostClient : IExtensionContributionClient, ITerminalHost
{
    internal const string RunCodeIdentity = "$run_code";

    private const int MaxCallChain = 8;
    private const int MaxDiagnostics = 16;

    private const string NestedRunCodeRefusal =
        "run_code cannot be invoked from inside another extension invocation (a plugin callback or a script); call the underlying tools directly.";

    private const string EndedRunRefusal =
        "The execution that made this call already ended; the call was refused.";

    private static readonly Sdk.CodeExecutionOptions DefaultExecutionOptions = new();

    private readonly HostConnection connection;
    private readonly ExtensionHostClientOptions options;
    private readonly Func<string, ITool?>? toolResolver;
    private readonly Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Lock toolsGate = new();
    private readonly Lock contributionsGate = new();
    private readonly Lock statusGate = new();
    private readonly Lock peerGate = new();
    private readonly Lock runsGate = new();
    private readonly Lock diagnosticsGate = new();
    private readonly AsyncLocal<TrustedCall?> trustedCall = new();
    private readonly Dictionary<string, PluginTools> toolsByPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginContributions> contributionsByPlugin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionRun> runs = new(StringComparer.Ordinal);
    private readonly Queue<string> diagnostics = new();
    private IReadOnlyList<ITool> tools = [];
    private IReadOnlyDictionary<string, IReadOnlyList<ITool>> toolsByPluginView =
        new Dictionary<string, IReadOnlyList<ITool>>(StringComparer.Ordinal);
    private IReadOnlyList<ICommand> commands = [];
    private IReadOnlyList<WorkflowDefinition> workflows = [];
    private IReadOnlyList<AgentDefinition> agents = [];
    private IReadOnlyList<PluginPromptReference> promptContributors = [];
    private IpcPeer? peer;
    private NamedPipeClientTransport? transport;
    private HostProcess? hostProcess;
    private int? hostExitCode;
    private string? protocolProblem;
    private long nextCallId;
    private long nextGeneration;
    private long registeredGeneration;
    private long activeGeneration;
    private int status = (int)HostStatus.Stopped;
    private int stopping;
    private int disposed;

    /// <summary>Connects to (or starts) a host without a main-program tool resolver: <c>host.tool_call</c>
    /// requests are answered with an unknown-tool error.</summary>
    public ExtensionHostClient(HostConnection connection)
        : this(connection, null, null, null)
    {
    }

    public ExtensionHostClient(HostConnection connection, ExtensionHostClientOptions? options)
        : this(connection, null, null, options)
    {
    }

    /// <summary>Connects to (or starts) a host and lets plugins call main-program tools: <paramref name="toolResolver"/>
    /// finds the tool and <paramref name="approvalPath"/> applies the main program's policy and user approval.</summary>
    public ExtensionHostClient(
        HostConnection connection,
        Func<string, ITool?>? toolResolver,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath)
        : this(connection, toolResolver, approvalPath, null)
    {
    }

    public ExtensionHostClient(
        HostConnection connection,
        Func<string, ITool?>? toolResolver,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath,
        ExtensionHostClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        this.toolResolver = toolResolver;
        this.approvalPath = approvalPath;
        this.options = options ?? new ExtensionHostClientOptions();
    }

#if DEBUG
    /// <summary>Seam for tests: runs on the starting task after the pipe connected and before the Running transition is
    /// committed, so a test can close the connection inside that window.</summary>
    internal Func<Task>? AfterConnectedHook { get; set; }
#endif

    public HostStatus Status => (HostStatus)Volatile.Read(ref status);

    public IReadOnlyList<ITool> Tools => Volatile.Read(ref tools);

    /// <summary>The same proxies grouped by plugin id, as last reported by the host.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ITool>> ToolsByPlugin => Volatile.Read(ref toolsByPluginView);

    /// <summary>Plugin commands as last reported through <c>contributions.register</c>; each proxy runs the command
    /// in its plugin's worker after the main program's approval path allows it.</summary>
    public IReadOnlyList<ICommand> Commands => Volatile.Read(ref commands);

    /// <summary>Markdown skill workflows contributed by plugins (metadata only; the agent reads and follows them).</summary>
    public IReadOnlyList<WorkflowDefinition> Workflows => Volatile.Read(ref workflows);

    /// <summary>Agent definitions contributed by plugins, converted to the main-program contract.</summary>
    public IReadOnlyList<AgentDefinition> Agents => Volatile.Read(ref agents);

    /// <summary>Dynamic prompt contributors, addressed by plugin id and contributor id.</summary>
    public IReadOnlyList<PluginPromptReference> PromptContributors => Volatile.Read(ref promptContributors);

    /// <summary>The most recent protocol problems (a refused callback, a malformed registration, a dropped
    /// declaration), newest last. Malformed host data is dropped here instead of failing the event loop.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (diagnosticsGate)
            {
                return [.. diagnostics];
            }
        }
    }

    /// <summary>Id of the host process this client started; null when it connected to an existing host.</summary>
    public int? HostProcessId
    {
        get
        {
            lock (peerGate)
            {
                return hostProcess?.ProcessId;
            }
        }
    }

    /// <summary>Exit code of the host process this client started, once that process has left; null while it is
    /// running or when this client connected to an existing host instead of starting one.</summary>
    public int? HostExitCode
    {
        get
        {
            lock (peerGate)
            {
                return hostExitCode;
            }
        }
    }

    public event Action? ToolsChanged;

    public event Action? ContributionsChanged;

    public event Action<HostStatus, string?>? StatusChanged;

    /// <summary>Creates the AOT-safe <c>run_code</c> proxy. The description and schema are generated from
    /// <paramref name="tools"/>, the arguments are validated locally, and one <c>code.execute</c> request carries the
    /// script, its budget and the tool surface; compilation and execution stay in the Extension Host.</summary>
    public ITool CreateRunCodeTool(IToolRegistry tools, RunCodeProxyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var validated = (options ?? new RunCodeProxyOptions()).Validated();
        return new RunCodeProxyTool(this, tools, validated);
    }

    /// <summary>Asks the host for the plugin registry state; the main program never reads the approval database.
    /// An unavailable store is never an empty successful answer: a timeout propagates as
    /// <see cref="IpcTimeoutException"/> and an undecodable response as <see cref="InvalidDataException"/>, so the caller
    /// treats both as "unknown" rather than "no plugins".</summary>
    public async Task<IReadOnlyList<Sdk.PluginStatusInfo>> GetPluginStatusesAsync(CancellationToken ct)
    {
        var active = RequireRunning();
        IpcMessage response;
        try
        {
            response = await SendAsync(
                    active, Sdk.ExtensionProtocol.PluginsStatus, IpcJsonTypes.EmptyObject, RequestBudget, ct)
                .ConfigureAwait(false);
        }
        catch (IpcTimeoutException ex)
        {
            Diagnose($"plugins.status did not answer within its budget: {ex.Message}");
            throw;
        }

        if (!HostProtocolJson.TryReadPluginStatuses(response.Result, out var statuses, out var problem))
        {
            Diagnose("plugins.status returned unusable data: " + problem);
            throw new InvalidDataException(
                $"{Sdk.ExtensionProtocol.PluginsStatus} returned a response that cannot be decoded: {problem}");
        }

        return statuses;
    }

    /// <summary>Evaluates one prompt contributor inside its plugin's worker, bound to <paramref name="context"/>.
    /// Null means the contributor produced no text (or is no longer registered).</summary>
    public async Task<string?> GetPluginPromptAsync(
        PluginPromptReference contributor, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(contributor.PluginId) || string.IsNullOrWhiteSpace(contributor.ContributorId))
        {
            Diagnose("A prompt contributor with a blank plugin or contributor id was requested; it was ignored.");
            return null;
        }

        if (!IsRegisteredPrompt(contributor))
        {
            Diagnose(
                $"Prompt contributor '{contributor.ContributorId}' of plugin '{contributor.PluginId}' is not registered; it was ignored.");
            return null;
        }

        IpcPeer active;
        try
        {
            active = RequireRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IpcConnectionClosedException)
        {
            Diagnose($"Prompt contributor '{contributor.ContributorId}' could not be evaluated: {ex.Message}");
            return null;
        }

        if (!TryGetCallOrigin(context.AgentDepth, ct, out var origin, out var originProblem))
        {
            Diagnose(originProblem);
            return null;
        }

        var effectiveContext = EffectiveContext(origin, context);
        if (!IsFullyQualified(effectiveContext.WorkingDirectory))
        {
            Diagnose(
                $"Prompt contributor '{contributor.ContributorId}' of plugin '{contributor.PluginId}' was not evaluated: {WorkingDirectoryProblem(effectiveContext)}");
            return null;
        }

        if (TryFindCycle(origin.Ancestors, contributor.PluginId, out var cycleProblem))
        {
            Diagnose(cycleProblem);
            return null;
        }

        if (!BeginRun(
                contributor.PluginId,
                origin,
                null,
                context,
                isCode: false,
                DefaultExecutionOptions.MaxConcurrentToolCalls,
                DefaultExecutionOptions.MaxInnerResultChars,
                out var run))
        {
            Diagnose("The Extension Host connection changed before the prompt could be evaluated.");
            return null;
        }

        try
        {
            var request = new Sdk.PromptExecutionRequest(
                contributor.PluginId,
                contributor.ContributorId,
                WireContext(run!, origin),
                WireTimeoutMilliseconds);
            var payload = HostProtocolJson.SerializePromptExecute(request);
            IpcMessage response;
            try
            {
                response = await SendAsync(
                        active, Sdk.ExtensionProtocol.PluginPrompt, payload, RequestBudget, run.Token)
                    .ConfigureAwait(false);
            }
            catch (IpcRemoteException ex)
            {
                Diagnose(
                    $"Prompt contributor '{contributor.ContributorId}' of plugin '{contributor.PluginId}' failed: {ex.Message}");
                return null;
            }
            catch (IpcTimeoutException ex)
            {
                Diagnose(
                    $"Prompt contributor '{contributor.ContributorId}' of plugin '{contributor.PluginId}' did not answer: {ex.Message}");
                return null;
            }
            catch (IOException ex)
            {
                // Transport-only failure (IpcConnectionClosedException is an IOException): the caller gets the
                // documented soft null plus a diagnostic, while its own OperationCanceledException still propagates.
                Diagnose(
                    $"Prompt contributor '{contributor.ContributorId}' of plugin '{contributor.PluginId}' could not be evaluated: the connection failed: {ex.Message}");
                return null;
            }

            if (!HostProtocolJson.TryReadPromptOutput(response.Result, out var output, out var problem))
            {
                Diagnose("plugin.prompt returned unusable data: " + problem);
                return null;
            }

            return output.Text;
        }
        finally
        {
            EndRun(run);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var current = Status;
            if (current is not (HostStatus.Stopped or HostStatus.Failed))
            {
                throw new InvalidOperationException(
                    $"The Extension Host client is {current}; stop it before starting it again.");
            }

            var recovering = current == HostStatus.Failed;
            Interlocked.Exchange(ref stopping, 0);
            Volatile.Write(ref protocolProblem, null);
            var launchesHost = !string.IsNullOrWhiteSpace(connection.HostExecutablePath);
            SetStatus(HostStatus.Starting, launchesHost
                ? $"Starting '{connection.HostExecutablePath}'."
                : $"Connecting to the Extension Host on pipe '{connection.PipeName}'.");

            try
            {
                if (recovering)
                {
                    await TeardownAsync(sendHostStop: false, clearSnapshots: false).ConfigureAwait(false);
                }

                var pipeName = IpcPipe.ValidatePipeName(connection.PipeName);
                HostProcess? started = null;
                if (launchesHost)
                {
                    started = HostProcess.Start(connection, options);
                    lock (peerGate)
                    {
                        hostProcess = started;
                        hostExitCode = null;
                    }
                }
                else
                {
                    lock (peerGate)
                    {
                        hostExitCode = null;
                    }
                }

                var connected = await ConnectAsync(pipeName, started, ct).ConfigureAwait(false);
                lock (peerGate)
                {
                    peer = connected.Peer;
                    transport = connected.Transport;
                }

#if DEBUG
                if (AfterConnectedHook is { } hook)
                {
                    await hook().ConfigureAwait(false);
                }
#endif

                Interlocked.Exchange(ref registeredGeneration, connected.Generation);
                if (!TryCommitRunning(connected, out var closeFailure))
                {
                    throw new IpcConnectionClosedException(closeFailure);
                }

                if (recovering)
                {
                    DropSnapshotsOfEarlierConnections();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                SetStatus(HostStatus.Stopped, "Starting the Extension Host was canceled.");
                await TeardownAsync(sendHostStop: false, clearSnapshots: false).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                ReportStartFailure(DescribeFailure(ex));
                await TeardownAsync(sendHostStop: false, clearSnapshots: false).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task RefreshPluginsAsync(CancellationToken ct)
    {
        var active = RequireRunning();
        await SendAsync(active, Sdk.ExtensionProtocol.PluginsRefresh, IpcJsonTypes.EmptyObject, RequestBudget, ct)
            .ConfigureAwait(false);
    }

    public async Task ApprovePluginAsync(
        string pluginId, string version, string sourceHash, string dependencyHash, string sdkVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var active = RequireRunning();
        var payload = HostProtocolJson.SerializeApprove(
            pluginId, version, sourceHash, dependencyHash, sdkVersion);
        await SendAsync(active, Sdk.ExtensionProtocol.PluginApprove, payload, RequestBudget, ct)
            .ConfigureAwait(false);
    }

    public async Task RevokePluginAsync(string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var active = RequireRunning();
        var payload = HostProtocolJson.SerializeRevoke(pluginId, version);
        await SendAsync(active, Sdk.ExtensionProtocol.PluginRevoke, payload, RequestBudget, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
            }
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lifecycle.Dispose();
        }
    }

    /// <summary>Sends one <c>plugin.execute</c> request for an agent- or UI-initiated plugin tool call. A call made
    /// from inside a bound callback inherits that callback's trusted chain and depth instead of starting a new one.</summary>
    internal async ValueTask<ToolResult> InvokePluginToolAsync(
        string pluginId, string toolName, ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        var active = RequireRunning();
        if (!HostProtocolJson.TryParseArguments(call.ArgumentsJson, out var arguments))
        {
            return new ToolResult(
                $"The arguments of '{toolName}' are not valid JSON: {call.ArgumentsJson}", IsError: true);
        }

        if (!TryGetCallOrigin(context.AgentDepth, ct, out var origin, out var originProblem))
        {
            Diagnose(originProblem);
            return new ToolResult($"'{toolName}' of plugin '{pluginId}' was not run: {originProblem}", IsError: true);
        }

        var effectiveContext = EffectiveContext(origin, context);
        if (!IsFullyQualified(effectiveContext.WorkingDirectory))
        {
            var problem = WorkingDirectoryProblem(effectiveContext);
            Diagnose($"'{toolName}' of plugin '{pluginId}' was not run: {problem}");
            return new ToolResult($"'{toolName}' of plugin '{pluginId}' was not run: {problem}", IsError: true);
        }

        if (TryFindCycle(origin.Ancestors, pluginId, out var cycleProblem))
        {
            Diagnose(cycleProblem);
            return new ToolResult($"'{toolName}' of plugin '{pluginId}' was not run: {cycleProblem}", IsError: true);
        }

        if (!BeginRun(
                pluginId,
                origin,
                null,
                context,
                isCode: false,
                DefaultExecutionOptions.MaxConcurrentToolCalls,
                DefaultExecutionOptions.MaxInnerResultChars,
                out var run))
        {
            return new ToolResult(
                $"'{toolName}' of plugin '{pluginId}' was not run: the Extension Host connection changed.",
                IsError: true);
        }

        try
        {
            var toolContext = WireContext(run!, origin);
            var payload = HostProtocolJson.SerializePluginExecute(
                pluginId,
                toolName,
                arguments,
                run.Context.SessionId,
                run.Context.WorkingDirectory,
                run.Depth,
                toolContext,
                WireTimeoutMilliseconds);
            IpcMessage response;
            try
            {
                response = await SendAsync(
                        active,
                        Sdk.ExtensionProtocol.PluginExecute,
                        payload,
                        RequestBudget,
                        run.Token)
                    .ConfigureAwait(false);
            }
            catch (IpcRemoteException ex)
            {
                return new ToolResult(
                    $"The Extension Host could not run '{toolName}' of plugin '{pluginId}': {ex.Message}",
                    IsError: true);
            }

            if (!HostProtocolJson.TryReadToolOutput(response.Result, out var content, out var isError))
            {
                return new ToolResult(content, IsError: true);
            }

            return new ToolResult(content, IsError: isError);
        }
        finally
        {
            EndRun(run);
        }
    }

    /// <summary>Sends one <c>code.execute</c> request for the <c>run_code</c> proxy: the script, its budget, the
    /// run-bound context and the tool surface. Compilation, semantic checks and the process lease stay in the host.</summary>
    internal async ValueTask<ToolResult> InvokeCodeAsync(
        IToolRegistry registry,
        Sdk.CodeExecutionOptions execution,
        string code,
        IReadOnlyList<Sdk.ToolDeclaration> declarations,
        TimeSpan timeout,
        ToolContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(execution);
        ct.ThrowIfCancellationRequested();
        var active = RequireRunning();

        if (!TryGetCallOrigin(context.AgentDepth, ct, out var origin, out var originProblem))
        {
            Diagnose(originProblem);
            return new ToolResult($"The script was not run: {originProblem}", IsError: true);
        }

        if (origin.Ancestors.Count > 0)
        {
            Diagnose(NestedRunCodeRefusal);
            return new ToolResult(NestedRunCodeRefusal, IsError: true);
        }

        var effectiveContext = EffectiveContext(origin, context);
        if (!IsFullyQualified(effectiveContext.WorkingDirectory))
        {
            var problem = $"The script was not run: {WorkingDirectoryProblem(effectiveContext)}";
            Diagnose(problem);
            return new ToolResult(problem, IsError: true);
        }

        if (!BeginRun(
                RunCodeIdentity,
                origin,
                registry,
                context,
                isCode: true,
                execution.MaxConcurrentToolCalls,
                execution.MaxInnerResultChars,
                out var run))
        {
            return new ToolResult("The script was not run: the Extension Host connection changed.", IsError: true);
        }

        var milliseconds = (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
        try
        {
            var request = new Sdk.CodeExecutionRequest(
                code,
                WireContext(run!, origin),
                milliseconds,
                declarations,
                execution);
            var payload = HostProtocolJson.SerializeCodeExecute(request);
            IpcMessage response;
            try
            {
                response = await SendAsync(
                        active, Sdk.ExtensionProtocol.CodeExecute, payload, timeout, run.Token)
                    .ConfigureAwait(false);
            }
            catch (IpcRemoteException ex)
            {
                return new ToolResult($"The Extension Host could not run the script: {ex.Message}", IsError: true);
            }
            catch (IpcTimeoutException)
            {
                return new ToolResult(
                    $"The script was stopped after {Seconds(timeout)} s (run_code timeout). Narrow the work, raise timeout_seconds, or split it into several calls.",
                    IsError: true);
            }

            if (!HostProtocolJson.TryReadCodeExecutionResult(response.Result, out var result, out var problem))
            {
                Diagnose("code.execute returned unusable data: " + problem);
                return new ToolResult(problem, IsError: true);
            }

            var content = TextBounds.Truncate(result.Content, execution.MaxResultChars, "result");
            return new ToolResult(content, result.IsError);
        }
        finally
        {
            EndRun(run);
        }
    }

    /// <summary>Runs one contributed command in its plugin's worker after the main program's approval path allows a
    /// main-program-confirmed <see cref="ToolSideEffect.Execute"/> call.</summary>
    internal async ValueTask<CommandResult> InvokeCommandAsync(
        ExtensionHostCommand command, string arguments, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var name = command.Definition.Name;
        IpcPeer active;
        try
        {
            active = RequireRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IpcConnectionClosedException)
        {
            return new CommandResult(Output: ex.Message, IsError: true);
        }

        var toolContext = new ToolContext(context.SessionId ?? string.Empty, context.WorkingDirectory);
        if (!TryGetCallOrigin(toolContext.AgentDepth, ct, out var origin, out var originProblem))
        {
            Diagnose(originProblem);
            return new CommandResult(Output: $"The command '{name}' was not run: {originProblem}", IsError: true);
        }

        var effectiveContext = EffectiveContext(origin, toolContext);
        if (!IsFullyQualified(effectiveContext.WorkingDirectory))
        {
            var problem = $"The command '{name}' was not run: {WorkingDirectoryProblem(effectiveContext)}";
            Diagnose(problem);
            return new CommandResult(Output: problem, IsError: true);
        }

        var approval = approvalPath;
        if (approval is null)
        {
            return new CommandResult(
                Output: $"The command '{name}' was not run: the main program has no approval path for plugin commands.",
                IsError: true);
        }

        ApprovalDecision decision;
        try
        {
            decision = await approval(
                    new ApprovalRequest(
                        name,
                        HostProtocolJson.SerializeText(arguments),
                        ToolSideEffect.Execute,
                        effectiveContext.WorkingDirectory),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CommandResult(
                Output: $"The command '{name}' was not run: the approval path failed: {ex.Message}", IsError: true);
        }

        if (decision != ApprovalDecision.Allow)
        {
            return new CommandResult(Output: $"The main program denied command '{name}'.", IsError: true);
        }

        if (TryFindCycle(origin.Ancestors, command.PluginId, out var cycleProblem))
        {
            Diagnose(cycleProblem);
            return new CommandResult(Output: $"The command '{name}' was not run: {cycleProblem}", IsError: true);
        }

        if (!BeginRun(
                command.PluginId,
                origin,
                null,
                toolContext,
                isCode: false,
                DefaultExecutionOptions.MaxConcurrentToolCalls,
                DefaultExecutionOptions.MaxInnerResultChars,
                out var run))
        {
            return new CommandResult(
                Output: $"The command '{name}' was not run: the Extension Host connection changed.", IsError: true);
        }

        try
        {
            var request = new Sdk.CommandExecutionRequest(
                command.PluginId,
                name,
                arguments,
                WireContext(run!, origin),
                WireTimeoutMilliseconds);
            var payload = HostProtocolJson.SerializeCommandExecute(request);
            IpcMessage response;
            try
            {
                response = await SendAsync(
                        active, Sdk.ExtensionProtocol.PluginCommand, payload, RequestBudget, run.Token)
                    .ConfigureAwait(false);
            }
            catch (IpcRemoteException ex)
            {
                return new CommandResult(
                    Output: $"The Extension Host could not run command '{name}' of plugin '{command.PluginId}': {ex.Message}",
                    IsError: true);
            }
            catch (IpcTimeoutException ex)
            {
                return new CommandResult(
                    Output: $"Command '{name}' of plugin '{command.PluginId}' did not finish: {ex.Message}",
                    IsError: true);
            }

            if (!HostProtocolJson.TryReadCommandOutput(response.Result, out var output, out var problem))
            {
                Diagnose("plugin.command returned unusable data: " + problem);
                return new CommandResult(Output: problem, IsError: true);
            }

            return new CommandResult(output.Output, output.PromptToSend, output.IsError);
        }
        finally
        {
            EndRun(run);
        }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        try
        {
            await TeardownAsync(sendHostStop: true, clearSnapshots: true).ConfigureAwait(false);
            SetStatus(HostStatus.Stopped, null);
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 0);
        }
    }

    /// <summary>Drops the peer, transport and host process of the current connection generation. The peers' own
    /// close path faults pending requests with <see cref="IpcConnectionClosedException"/>.</summary>
    private async Task TeardownAsync(bool sendHostStop, bool clearSnapshots)
    {
        RetireActiveGeneration();
        InvalidateRuns();
        IpcPeer? active;
        lock (peerGate)
        {
            active = peer;
            peer = null;
        }

        var stopAcknowledged = false;
        if (active is not null)
        {
            if (sendHostStop && !active.IsClosed)
            {
                var bound = options.StopRequestTimeout > TimeSpan.Zero
                    ? options.StopRequestTimeout
                    : TimeSpan.FromSeconds(5);
                using var stopTimeout = new CancellationTokenSource(bound);
                try
                {
                    await active.SendRequestAsync(Sdk.ExtensionProtocol.HostStop, IpcJsonTypes.EmptyObject, stopTimeout.Token)
                        .ConfigureAwait(false);
                    stopAcknowledged = true;
                }
                catch (Exception ex) when (ex is IpcConnectionClosedException or IpcTimeoutException
                    or IpcRemoteException or IOException or ObjectDisposedException or OperationCanceledException
                    or InvalidOperationException)
                {
                }
            }

            await DisposeQuietlyAsync(active).ConfigureAwait(false);
        }

        NamedPipeClientTransport? pendingTransport;
        lock (peerGate)
        {
            pendingTransport = transport;
            transport = null;
        }

        if (pendingTransport is not null)
        {
            try
            {
                await pendingTransport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        HostProcess? pendingHost;
        lock (peerGate)
        {
            pendingHost = hostProcess;
            hostProcess = null;
        }

        if (pendingHost is not null)
        {
            try
            {
                await pendingHost.TerminateAsync(options.ProcessExitTimeout, waitForSelfExit: stopAcknowledged).ConfigureAwait(false);
            }
            finally
            {
                lock (peerGate)
                {
                    hostExitCode = pendingHost.ExitCode;
                }

                await pendingHost.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (clearSnapshots)
        {
            ClearTools();
            ClearContributions();
        }
    }

    private async Task<PeerBundle> ConnectAsync(string pipeName, HostProcess? started, CancellationToken ct)
    {
        var timeout = connection.StartTimeout > TimeSpan.Zero ? connection.StartTimeout : TimeSpan.FromSeconds(30);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        Exception? last = null;
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var candidate = CreatePeer(pipeName);

            // One attempt owns the connect it starts. The pipe layer gives up on a connect that has not finished within
            // IpcPipeOptions.ConnectTimeout but leaves it running, so an attempt that was already abandoned can still
            // reach the host later - and the host reads a connection that closes without a request as its main program
            // having left, which shuts it down. Cancelling the attempt under the same token before the transport is
            // disposed is what stops that connect instead of orphaning it.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await candidate.Peer.StartAsync(attempt.Token).ConfigureAwait(false);
                return candidate;
            }
            catch (Exception ex) when (ex is IpcTimeoutException or IOException or InvalidOperationException
                or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                last = ex;
                try
                {
                    await attempt.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }

                RetireActiveGeneration();
                await DisposeQuietlyAsync(candidate.Peer).ConfigureAwait(false);
                try
                {
                    await candidate.Transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeError) when (disposeError is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }

            if (started is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The Extension Host exited before accepting the connection on pipe '{connection.PipeName}' " +
                    $"({started.DescribeState()})." + OutputTail(started), last);
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                throw ConnectFailed(last, timeout, ct, oce);
            }

            delay = delay < TimeSpan.FromMilliseconds(250)
                ? TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2)
                : delay;
        }
    }

    private Exception ConnectFailed(Exception? last, TimeSpan timeout, CancellationToken ct, OperationCanceledException oce)
    {
        if (ct.IsCancellationRequested)
        {
            return new OperationCanceledException(
                "Connecting to the Extension Host was canceled.", last ?? oce, ct);
        }

        return new TimeoutException(
            $"The Extension Host did not accept a connection on pipe '{connection.PipeName}' within " +
            $"{timeout.TotalMilliseconds:F0} ms." + HostState() + OutputTail(null), last ?? oce);
    }

    /// <summary>Where the self-started host was when the connect budget ran out, so a timeout names a slow start
    /// (still running) separately from a host that died before it listened (exit code and output follow). A client
    /// that only connected to an already running host reports nothing.</summary>
    private string HostState()
    {
        HostProcess? started;
        lock (peerGate)
        {
            started = hostProcess;
        }

        return started is null
            ? string.Empty
            : Environment.NewLine + $"The Extension Host process {started.DescribeState()}.";
    }

    private PeerBundle CreatePeer(string pipeName)
    {
        var pipe = new NamedPipeClientTransport(pipeName, options.PipeOptions);
        var created = new IpcPeer(pipe, new IpcPeerOptions
        {
            // Deadlines are per call in this client (see SendAsync): a peer-wide default would cut a 600 s script
            // short, while leaving unrelated requests at the configured RequestTimeout.
            DefaultTimeout = Timeout.InfiniteTimeSpan,
        });
        var generation = Interlocked.Increment(ref nextGeneration);
        Volatile.Write(ref activeGeneration, generation);
        created.Closed += cause => OnPeerClosed(generation, cause);
        created.RequestReceived += (message, ct) => OnRequestAsync(created, message, ct);
        created.NotificationReceived += message => OnNotification(generation, message);
        created.VersionMismatch += OnVersionMismatch;
        return new PeerBundle(created, pipe, generation);
    }

    /// <summary>Stops the current connection generation from reporting: a close callback of a torn-down
    /// connection must not move the status of the connection that replaced it.</summary>
    private void RetireActiveGeneration()
    {
        Volatile.Write(ref activeGeneration, Interlocked.Increment(ref nextGeneration));
        TerminateTerminals(null);
    }

    private void OnPeerClosed(long generation, Exception? cause)
    {
        if (generation != Volatile.Read(ref activeGeneration))
        {
            return;
        }

        TerminateTerminals(cause);

        var intentional = Volatile.Read(ref stopping) != 0;
        var next = intentional ? HostStatus.Stopped : HostStatus.Failed;
        lock (statusGate)
        {
            var current = (HostStatus)status;
            if (current is HostStatus.Stopped or HostStatus.Failed)
            {
                return;
            }

            Volatile.Write(ref status, (int)next);
        }

        RaiseStatus(next, intentional ? null : DescribeClose(cause));
    }

    private void OnVersionMismatch(IpcVersionMismatchException mismatch) =>
        Volatile.Write(ref protocolProblem, mismatch.Message);

    private void OnNotification(long generation, IpcMessage message)
    {
        if (generation < Volatile.Read(ref registeredGeneration))
        {
            return;
        }

        if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ToolsRegister, StringComparison.Ordinal))
        {
            RegisterTools(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ToolsUnregister, StringComparison.Ordinal))
        {
            UnregisterTools(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ContributionsRegister, StringComparison.Ordinal))
        {
            RegisterContributions(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ContributionsUnregister, StringComparison.Ordinal))
        {
            UnregisterContributions(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, TerminalOps.Output, StringComparison.Ordinal))
        {
            OnTerminalOutput(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, TerminalOps.Exit, StringComparison.Ordinal))
        {
            OnTerminalExit(generation, message.Arguments);
        }
    }

    private void RegisterTools(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadToolsRegister(payload, out var pluginId, out var declarations))
        {
            Diagnose("tools.register carried no usable plugin id or tool list; it was ignored.");
            return;
        }

        if (string.Equals(pluginId, RunCodeIdentity, StringComparison.Ordinal))
        {
            Diagnose(
                $"tools.register used the reserved identity '{RunCodeIdentity}'; it was ignored so it cannot shadow run_code.");
            return;
        }

        if (declarations.Count == 0)
        {
            SetPluginTools(pluginId, null, generation);
            return;
        }

        var callable = new List<Sdk.ToolDeclaration>(declarations.Count);
        foreach (var declaration in declarations)
        {
            if (string.Equals(declaration.Name, RunCodeProxyTool.Name, StringComparison.Ordinal))
            {
                Diagnose(
                    $"Plugin '{pluginId}' registered a tool named '{RunCodeProxyTool.Name}'; it was ignored because run_code is a reserved main-program tool.");
                continue;
            }

            callable.Add(declaration);
        }

        if (callable.Count == 0)
        {
            SetPluginTools(pluginId, null, generation);
            return;
        }

        var proxies = new ExtensionHostTool[callable.Count];
        for (var index = 0; index < proxies.Length; index++)
        {
            proxies[index] = new ExtensionHostTool(this, pluginId, callable[index]);
        }

        SetPluginTools(pluginId, proxies, generation);
    }

    private void UnregisterTools(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadToolsUnregister(payload, out var pluginId))
        {
            Diagnose("tools.unregister carried no usable plugin id; it was ignored.");
            return;
        }

        SetPluginTools(pluginId, null, generation);
    }

    private void SetPluginTools(string pluginId, IReadOnlyList<ExtensionHostTool>? proxies, long generation)
    {
        var changed = false;
        lock (toolsGate)
        {
            toolsByPlugin.TryGetValue(pluginId, out var registered);
            if (registered.Tools is not null && registered.Generation > generation)
            {
                return;
            }

            if (proxies is null || proxies.Count == 0)
            {
                changed = toolsByPlugin.Remove(pluginId);
            }
            else if (registered.Tools is null || !SameDeclarations(registered.Tools, proxies))
            {
                toolsByPlugin[pluginId] = new PluginTools(generation, proxies);
                changed = true;
            }
            else
            {
                toolsByPlugin[pluginId] = registered with { Generation = generation };
            }

            if (!changed)
            {
                return;
            }

            RebuildToolSnapshots();
        }

        RaiseToolsChanged();
    }

    private void RegisterContributions(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadContributionsRegister(
                payload, out var pluginId, out var contributions, out var problem))
        {
            Diagnose("contributions.register ignored: " + problem);
            return;
        }

        if (string.Equals(pluginId, RunCodeIdentity, StringComparison.Ordinal))
        {
            Diagnose(
                $"contributions.register used the reserved identity '{RunCodeIdentity}'; it was ignored so it cannot shadow run_code.");
            return;
        }

        var validation = new List<string>();
        var validated = ContributionSnapshots.Validate(pluginId, contributions, validation);
        foreach (var message in validation)
        {
            Diagnose(message);
        }

        SetPluginContributions(pluginId, validated, generation);
    }

    private void UnregisterContributions(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadPluginId(payload, out var pluginId))
        {
            Diagnose("contributions.unregister carried no usable plugin id; it was ignored.");
            return;
        }

        SetPluginContributions(pluginId, null, generation);
    }

    private void SetPluginContributions(
        string pluginId, ValidatedContributions? contributions, long generation)
    {
        var changed = false;
        var messages = new List<string>();
        lock (contributionsGate)
        {
            contributionsByPlugin.TryGetValue(pluginId, out var registered);
            if (registered is not null && registered.Generation > generation)
            {
                return;
            }

            if (contributions is null || contributions.IsEmpty)
            {
                changed = contributionsByPlugin.Remove(pluginId);
            }
            else if (registered is null || !ContributionSnapshots.Same(registered.Contributions, contributions))
            {
                contributionsByPlugin[pluginId] = new PluginContributions(generation, contributions);
                changed = true;
            }
            else
            {
                contributionsByPlugin[pluginId] = registered with { Generation = generation };
            }

            if (changed)
            {
                RebuildContributionSnapshots(messages);
            }
        }

        foreach (var message in messages)
        {
            Diagnose(message);
        }

        if (changed)
        {
            RaiseContributionsChanged();
        }
    }

    /// <summary>Drops the entries an earlier connection registered; entries the current connection published - even
    /// ones that arrived before the Running transition - survive. A crash keeps the last known snapshot.</summary>
    private void DropSnapshotsOfEarlierConnections()
    {
        var current = Volatile.Read(ref registeredGeneration);
        var toolsChanged = false;
        lock (toolsGate)
        {
            List<string>? stale = null;
            foreach (var (pluginId, registered) in toolsByPlugin)
            {
                if (registered.Generation < current)
                {
                    (stale ??= []).Add(pluginId);
                }
            }

            if (stale is not null)
            {
                foreach (var pluginId in stale)
                {
                    toolsByPlugin.Remove(pluginId);
                }

                RebuildToolSnapshots();
                toolsChanged = true;
            }
        }

        if (toolsChanged)
        {
            RaiseToolsChanged();
        }

        var contributionsChanged = false;
        var messages = new List<string>();
        lock (contributionsGate)
        {
            List<string>? stale = null;
            foreach (var (pluginId, registered) in contributionsByPlugin)
            {
                if (registered.Generation < current)
                {
                    (stale ??= []).Add(pluginId);
                }
            }

            if (stale is not null)
            {
                foreach (var pluginId in stale)
                {
                    contributionsByPlugin.Remove(pluginId);
                }

                RebuildContributionSnapshots(messages);
                contributionsChanged = true;
            }
        }

        foreach (var message in messages)
        {
            Diagnose(message);
        }

        if (contributionsChanged)
        {
            RaiseContributionsChanged();
        }
    }

    private static bool SameDeclarations(
        IReadOnlyList<ExtensionHostTool> registered, IReadOnlyList<ExtensionHostTool> incoming)
    {
        if (registered.Count != incoming.Count)
        {
            return false;
        }

        for (var index = 0; index < registered.Count; index++)
        {
            if (!SameDeclaration(registered[index], incoming[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameDeclaration(ExtensionHostTool registered, ExtensionHostTool incoming) =>
        string.Equals(registered.PluginId, incoming.PluginId, StringComparison.Ordinal) &&
        registered.Definition == incoming.Definition &&
        registered.SideEffect == incoming.SideEffect;

    private void ClearTools()
    {
        lock (toolsGate)
        {
            if (toolsByPlugin.Count == 0 && tools.Count == 0)
            {
                return;
            }

            toolsByPlugin.Clear();
            RebuildToolSnapshots();
        }

        RaiseToolsChanged();
    }

    private void ClearContributions()
    {
        lock (contributionsGate)
        {
            if (contributionsByPlugin.Count == 0 &&
                commands.Count == 0 &&
                workflows.Count == 0 &&
                agents.Count == 0 &&
                promptContributors.Count == 0)
            {
                return;
            }

            contributionsByPlugin.Clear();
            RebuildContributionSnapshots([]);
        }

        RaiseContributionsChanged();
    }

    private void RebuildToolSnapshots()
    {
        if (toolsByPlugin.Count == 0)
        {
            tools = [];
            toolsByPluginView = new Dictionary<string, IReadOnlyList<ITool>>(StringComparer.Ordinal);
            return;
        }

        var view = new Dictionary<string, IReadOnlyList<ITool>>(toolsByPlugin.Count, StringComparer.Ordinal);
        var flat = new List<ITool>(toolsByPlugin.Count);
        foreach (var (pluginId, published) in toolsByPlugin)
        {
            var proxies = published.Tools;
            var copy = new ITool[proxies.Count];
            for (var index = 0; index < proxies.Count; index++)
            {
                copy[index] = proxies[index];
                flat.Add(proxies[index]);
            }

            view[pluginId] = copy;
        }

        tools = flat;
        toolsByPluginView = view;
    }

    /// <summary>Rebuilds the four contribution views under <see cref="contributionsGate"/>. Names are unique across
    /// plugins by ordinal plugin id (the first registration wins), so a lookup by name is unambiguous.</summary>
    private void RebuildContributionSnapshots(List<string> messages)
    {
        if (contributionsByPlugin.Count == 0)
        {
            Volatile.Write(ref commands, []);
            Volatile.Write(ref workflows, []);
            Volatile.Write(ref agents, []);
            Volatile.Write(ref promptContributors, []);
            return;
        }

        var ordered = new List<KeyValuePair<string, PluginContributions>>(contributionsByPlugin);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        var commandList = new List<ICommand>();
        var workflowList = new List<WorkflowDefinition>();
        var agentList = new List<AgentDefinition>();
        var promptList = new List<PluginPromptReference>();
        var commandNames = new HashSet<string>(StringComparer.Ordinal);
        var workflowNames = new HashSet<string>(StringComparer.Ordinal);
        var agentNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (pluginId, snapshot) in ordered)
        {
            foreach (var declaration in snapshot.Contributions.Commands)
            {
                if (!commandNames.Add(declaration.Name))
                {
                    messages.Add(
                        $"Command '{declaration.Name}' of plugin '{pluginId}' clashes with another contribution; it was ignored.");
                    continue;
                }

                commandList.Add(new ExtensionHostCommand(this, pluginId, declaration));
            }

            foreach (var workflow in snapshot.Contributions.Workflows)
            {
                if (!workflowNames.Add(workflow.Name))
                {
                    messages.Add(
                        $"Workflow '{workflow.Name}' of plugin '{pluginId}' clashes with another contribution; it was ignored.");
                    continue;
                }

                workflowList.Add(ContributionSnapshots.ToWorkflow(workflow));
            }

            foreach (var agent in snapshot.Contributions.Agents)
            {
                if (!agentNames.Add(agent.Name))
                {
                    messages.Add(
                        $"Agent '{agent.Name}' of plugin '{pluginId}' clashes with another contribution; it was ignored.");
                    continue;
                }

                agentList.Add(ContributionSnapshots.ToAgent(agent));
            }

            foreach (var prompt in snapshot.Contributions.Prompts)
            {
                promptList.Add(new PluginPromptReference(pluginId, prompt.Id));
            }
        }

        Volatile.Write(ref commands, commandList);
        Volatile.Write(ref workflows, workflowList);
        Volatile.Write(ref agents, agentList);
        Volatile.Write(ref promptContributors, promptList);
    }

    private Task OnRequestAsync(IpcPeer active, IpcMessage message, CancellationToken ct) =>
        string.Equals(message.Operation, Sdk.ExtensionProtocol.HostToolCall, StringComparison.Ordinal)
            ? HandleHostToolCallAsync(active, message, ct)
            : RespondErrorAsync(active, message.Id, "unsupported", $"The main program does not handle '{message.Operation}'.");

    /// <summary>A worker, via the host, calls a main-program tool. Only a call that names a live run of this
    /// connection generation and the plugin identity recorded for it is resolved, approved and executed; the session,
    /// working directory, depth and plugin chain come from that record, not from the message.</summary>
    private async Task HandleHostToolCallAsync(IpcPeer active, IpcMessage request, CancellationToken ct)
    {
        if (!HostProtocolJson.TryReadBoundToolCall(request.Arguments, out var call, out var problem))
        {
            Diagnose("host.tool_call refused: " + problem);
            await RespondToolOutputAsync(active, request.Id, problem, isError: true).ConfigureAwait(false);
            return;
        }

        if (string.Equals(call.Name, RunCodeProxyTool.Name, StringComparison.Ordinal))
        {
            Diagnose("host.tool_call refused: " + NestedRunCodeRefusal);
            await RespondToolOutputAsync(active, request.Id, NestedRunCodeRefusal, isError: true).ConfigureAwait(false);
            return;
        }

        if (!TryGetActiveRun(call.RunId, out var run, out problem))
        {
            Diagnose($"host.tool_call refused: {problem}");
            await RespondToolOutputAsync(active, request.Id, problem, isError: true).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(run.PluginId, call.PluginId, StringComparison.Ordinal))
        {
            problem =
                $"'{call.Name}' was not run: the call claims plugin '{call.PluginId}' but the run belongs to '{run.PluginId}'.";
            Diagnose("host.tool_call refused: " + problem);
            await RespondToolOutputAsync(active, request.Id, problem, isError: true).ConfigureAwait(false);
            return;
        }

        var tool = run.IsCode
            ? run.Registry is { } registry && registry.TryGet(call.Name, out var registered) ? registered : null
            : toolResolver?.Invoke(call.Name);
        if (tool is null)
        {
            await RespondToolOutputAsync(active, request.Id, $"Unknown tool '{call.Name}'.", isError: true)
                .ConfigureAwait(false);
            return;
        }

        var approval = approvalPath;
        if (approval is null)
        {
            await RespondToolOutputAsync(
                    active, request.Id,
                    $"'{call.Name}' was not run: the main program has no approval path for plugin tool calls.",
                    isError: true)
                .ConfigureAwait(false);
            return;
        }

        if (!run.TryLease())
        {
            Diagnose("host.tool_call refused: " + EndedRunRefusal);
            await RespondToolOutputAsync(active, request.Id, EndedRunRefusal, isError: true).ConfigureAwait(false);
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Token);
            var token = linked.Token;
            try
            {
                await run.Gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!run.IsActive)
                {
                    Diagnose("host.tool_call refused: " + EndedRunRefusal);
                    await RespondToolOutputAsync(active, request.Id, EndedRunRefusal, isError: true)
                        .ConfigureAwait(false);
                    return;
                }

                ApprovalDecision decision;
                try
                {
                    decision = await approval(
                            new ApprovalRequest(
                                call.Name, call.ArgumentsJson, tool.SideEffect, run.Context.WorkingDirectory),
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await RespondToolOutputAsync(
                            active, request.Id, $"'{call.Name}' was not run: the approval path failed: {ex.Message}",
                            isError: true)
                        .ConfigureAwait(false);
                    return;
                }

                if (decision != ApprovalDecision.Allow)
                {
                    await RespondToolOutputAsync(
                            active, request.Id, $"The main program denied '{call.Name}'.", isError: true)
                        .ConfigureAwait(false);
                    return;
                }

                // Approval may ignore cancellation and still answer Allow; only an active run may start a tool.
                if (!run.IsActive)
                {
                    Diagnose("host.tool_call refused: " + EndedRunRefusal);
                    await RespondToolOutputAsync(active, request.Id, EndedRunRefusal, isError: true)
                        .ConfigureAwait(false);
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                var childContext = new ToolContext(
                    run.Context.SessionId, run.Context.WorkingDirectory, run.Depth + 1);
                var toolCall = new ToolCall(
                    $"hostcall-{Interlocked.Increment(ref nextCallId)}", call.Name, call.ArgumentsJson);
                var previous = trustedCall.Value;
                trustedCall.Value = new TrustedCall(run.RunId, run.Generation, run.Chain, childContext.AgentDepth);
                ToolResult result;
                try
                {
                    result = await tool.InvokeAsync(toolCall, childContext, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await RespondToolOutputAsync(
                            active, request.Id, $"{ex.GetType().Name}: {ex.Message}", isError: true)
                        .ConfigureAwait(false);
                    return;
                }
                finally
                {
                    trustedCall.Value = previous;
                }

                var content = TextBounds.Truncate(
                    result.Content ?? string.Empty, run.MaxInnerResultChars, "tool result");
                await RespondToolOutputAsync(active, request.Id, content, result.IsError).ConfigureAwait(false);
            }
            finally
            {
                run.Gate.Release();
            }
        }
        finally
        {
            run.ReleaseLease();
        }
    }

    /// <summary>Registers a new execution. It is bound to the connection generation it starts on; if the connection
    /// was replaced in the meantime the run is not created at all.</summary>
    private bool BeginRun(
        string pluginId,
        CallOrigin origin,
        IToolRegistry? registry,
        ToolContext context,
        bool isCode,
        int maxConcurrency,
        int maxInnerResultChars,
        [NotNullWhen(true)] out ExtensionRun? run)
    {
        run = null;
        CancellationTokenSource lifetime;
        try
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(origin.Token);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        lock (runsGate)
        {
            if (Volatile.Read(ref activeGeneration) != origin.Generation)
            {
                lifetime.Dispose();
                return false;
            }

            string runId;
            do
            {
                runId = RandomNumberGenerator.GetHexString(32);
            }
            while (runs.ContainsKey(runId));

            var chain = new string[origin.Ancestors.Count + 1];
            for (var index = 0; index < origin.Ancestors.Count; index++)
            {
                chain[index] = origin.Ancestors[index];
            }

            chain[^1] = pluginId;
            run = new ExtensionRun(
                runId,
                origin.RootRunId ?? runId,
                origin.Generation,
                pluginId,
                chain,
                EffectiveContext(origin, context),
                origin.Depth,
                isCode,
                registry,
                maxConcurrency,
                maxInnerResultChars,
                lifetime);
            runs.Add(runId, run);
            return true;
        }
    }

    private void EndRun(ExtensionRun? run)
    {
        if (run is null)
        {
            return;
        }

        lock (runsGate)
        {
            if (runs.TryGetValue(run.RunId, out var stored) && ReferenceEquals(stored, run))
            {
                runs.Remove(run.RunId);
            }
        }

        try
        {
            run.End();
        }
        finally
        {
            run.ReleaseLease();
        }
    }

    /// <summary>Ends every execution of the connection being torn down, so a late callback of an old run finds
    /// nothing to run instead of reaching the resolver.</summary>
    private void InvalidateRuns()
    {
        ExtensionRun[] stale;
        lock (runsGate)
        {
            if (runs.Count == 0)
            {
                return;
            }

            stale = [.. runs.Values];
            runs.Clear();
        }

        foreach (var run in stale)
        {
            run.End();
        }
    }

    private bool TryGetActiveRun(string runId, [NotNullWhen(true)] out ExtensionRun? run, out string problem)
    {
        problem = string.Empty;
        ExtensionRun? found;
        lock (runsGate)
        {
            runs.TryGetValue(runId, out found);
        }

        if (found is null)
        {
            problem = "The call is not part of an execution that is in flight on this connection; it was refused.";
            run = null;
            return false;
        }

        if (found.Generation != Volatile.Read(ref activeGeneration))
        {
            problem = "The call belongs to an execution of an earlier connection; it was refused.";
            run = null;
            return false;
        }

        if (!found.IsActive)
        {
            problem = "The execution that made this call already ended; the call was refused.";
            run = null;
            return false;
        }

        run = found;
        return true;
    }

    /// <summary>Resolves the trusted context of a new execution: the caller's top-level depth for an agent- or
    /// UI-initiated call, or the parent callback's chain and depth for a nested one. It is never taken from a message.</summary>
    private bool TryGetCallOrigin(int topLevelDepth, CancellationToken ct, out CallOrigin origin, out string problem)
    {
        origin = default;
        problem = string.Empty;
        var generation = Volatile.Read(ref activeGeneration);
        if (trustedCall.Value is not { } parent)
        {
            origin = new CallOrigin([], Math.Max(0, topLevelDepth), generation, ct, null, RootRunId: null);
            return true;
        }

        if (!TryGetActiveRun(parent.RunId, out var parentRun, out problem))
        {
            return false;
        }

        if (parentRun.Generation != parent.Generation)
        {
            problem = "The call belongs to an execution of an earlier connection; it was refused.";
            return false;
        }

        if (parent.Chain.Count >= MaxCallChain)
        {
            problem = $"The plugin call chain already has {parent.Chain.Count} links; the limit is {MaxCallChain}.";
            return false;
        }

        origin = new CallOrigin(
            parent.Chain, parent.ChildDepth, parent.Generation, parentRun.Token, parentRun.Context,
            parentRun.RootRunId);
        return true;
    }

    private static bool TryFindCycle(IReadOnlyList<string> ancestors, string pluginId, out string problem)
    {
        foreach (var ancestor in ancestors)
        {
            if (string.Equals(ancestor, pluginId, StringComparison.Ordinal))
            {
                problem =
                    $"Plugin '{pluginId}' is already on the call chain; a plugin may not be called recursively.";
                return true;
            }
        }

        problem = string.Empty;
        return false;
    }

    private bool IsRegisteredPrompt(PluginPromptReference contributor)
    {
        foreach (var registered in PromptContributors)
        {
            if (string.Equals(registered.PluginId, contributor.PluginId, StringComparison.Ordinal) &&
                string.Equals(registered.ContributorId, contributor.ContributorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sends a request with a per-call deadline: the caller's token, this call's budget and the connection
    /// lifetime.</summary>
    private static async Task<IpcMessage> SendAsync(
        IpcPeer active, string operation, JsonElement? args, TimeSpan? budget, CancellationToken ct)
    {
        if (budget is not { } timeout)
        {
            return await active.SendRequestAsync(operation, args, ct).ConfigureAwait(false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await active.SendRequestAsync(operation, args, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new IpcTimeoutException(operation, timeout, oce);
        }
    }

    private TimeSpan? RequestBudget =>
        options.RequestTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : null;

    /// <summary>The budget a plugin execution reports to the host; a client configured without a deadline still
    /// tells the host the documented two-minute default.</summary>
    private int WireTimeoutMilliseconds => (int)Math.Clamp(
        (RequestBudget ?? TimeSpan.FromSeconds(120)).TotalMilliseconds, 1, int.MaxValue);

    private static bool IsFullyQualified([NotNullWhen(true)] string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    /// <summary>The context an execution actually runs with. A nested call inherits the trusted parent's context, so a
    /// caller cannot swap in a different working directory; only a top-level call uses the context it was handed.
    /// Nothing here normalizes the path: a relative path stays relative and is refused by
    /// <see cref="IsFullyQualified"/> instead of being silently resolved against the process directory.</summary>
    private static ToolContext EffectiveContext(in CallOrigin origin, ToolContext context) =>
        origin.ParentContext ?? context;

    /// <summary>The context a request carries on the wire. Every field, the root invocation identity included, is taken
    /// from the trusted pending record of the run: <see cref="ExtensionRun.RootRunId"/> is this run's own id for a
    /// top-level execution and the inherited one for a nested call, so it can never be supplied by a worker, a tool
    /// context or a callback message.</summary>
    private static Sdk.ExtensionCallContext WireContext(ExtensionRun run, in CallOrigin origin) =>
        new(run.RunId, run.Context.SessionId, run.Context.WorkingDirectory, run.Depth, origin.Ancestors)
        {
            RootRunId = run.RootRunId,
        };

    /// <summary>Refusal text for an execution whose effective working directory is unusable.</summary>
    private static string WorkingDirectoryProblem(ToolContext context) =>
        string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? "the working directory is blank; it is not a fully qualified path"
            : $"the working directory '{context.WorkingDirectory}' is not a fully qualified path";

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static Task RespondToolOutputAsync(IpcPeer active, long requestId, string content, bool isError) =>
        active.IsClosed ? Task.CompletedTask : RespondAsync(active, requestId, content, isError);

    private static Task RespondErrorAsync(IpcPeer active, long requestId, string code, string message) =>
        active.IsClosed ? Task.CompletedTask : RespondErrorCoreAsync(active, requestId, code, message);

    private static async Task RespondErrorCoreAsync(IpcPeer active, long requestId, string code, string message)
    {
        try
        {
            await active.RespondErrorAsync(requestId, new IpcError(code, message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
        }
    }

    private static async Task RespondAsync(IpcPeer active, long requestId, string content, bool isError)
    {
        try
        {
            await active.RespondAsync(
                    requestId, HostProtocolJson.SerializeHostToolOutput(content, isError), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
        }
    }

    private IpcPeer RequireRunning()
    {
        lock (peerGate)
        {
            if (peer is { IsClosed: false } active)
            {
                return active;
            }
        }

        var current = Status;
        throw current switch
        {
            HostStatus.Stopped => new InvalidOperationException(
                "The Extension Host client is not running; call StartAsync first."),
            HostStatus.Starting => new InvalidOperationException("The Extension Host client is still starting."),
            _ => new IpcConnectionClosedException(
                $"The Extension Host connection is closed (status {current})."),
        };
    }

    private void SetStatus(HostStatus next, string? detail, bool forceDetail = false)
    {
        lock (statusGate)
        {
            if ((HostStatus)status != next)
            {
                Volatile.Write(ref status, (int)next);
            }
            else if (!forceDetail)
            {
                return;
            }
        }

        RaiseStatus(next, detail);
    }

    /// <summary>Commits the Running transition of <paramref name="connected"/> under the status lock the close callback
    /// uses, after re-checking that the connection is still the active one, still open and that the start is still in
    /// <see cref="HostStatus.Starting"/>. A connection that closed in the connect-to-Running window therefore never ends
    /// up reported as running: the client is Failed with the close cause and the start fails.</summary>
    private bool TryCommitRunning(PeerBundle connected, out string failure)
    {
        bool committed;
        bool announce;
        lock (statusGate)
        {
            var current = (HostStatus)status;
            committed = current == HostStatus.Starting &&
                connected.Generation == Volatile.Read(ref activeGeneration) &&
                !connected.Peer.IsClosed;
            announce = !committed && current == HostStatus.Starting;
            if (committed)
            {
                Volatile.Write(ref status, (int)HostStatus.Running);
            }
            else if (announce)
            {
                Volatile.Write(ref status, (int)HostStatus.Failed);
            }
        }

        if (committed)
        {
            RaiseStatus(HostStatus.Running, null);
            failure = string.Empty;
            return true;
        }

        failure = DescribeClose(null);
        if (announce)
        {
            RaiseStatus(HostStatus.Failed, failure);
        }

        return false;
    }

    /// <summary>Reports a start failure exactly once: when the connection's close callback already moved the status out of
    /// <see cref="HostStatus.Starting"/>, that callback reported the cause and this reports nothing.</summary>
    private void ReportStartFailure(string detail)
    {
        lock (statusGate)
        {
            if ((HostStatus)status != HostStatus.Starting)
            {
                return;
            }

            Volatile.Write(ref status, (int)HostStatus.Failed);
        }

        RaiseStatus(HostStatus.Failed, detail);
    }

    private void RaiseStatus(HostStatus next, string? detail)
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(next, detail);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseToolsChanged()
    {
        var handler = ToolsChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception)
        {
        }
    }

    private void RaiseContributionsChanged()
    {
        var handler = ContributionsChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception)
        {
        }
    }

    private void Diagnose(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (diagnosticsGate)
        {
            diagnostics.Enqueue(message);
            while (diagnostics.Count > MaxDiagnostics)
            {
                diagnostics.Dequeue();
            }
        }
    }

    private static async Task DisposeQuietlyAsync(IpcPeer active)
    {
        try
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
            or OperationCanceledException)
        {
        }
    }

    private string DescribeFailure(Exception failure)
    {
        var tail = OutputTail(null);
        return failure.Message + tail;
    }

    private string DescribeClose(Exception? cause)
    {
        var problem = Volatile.Read(ref protocolProblem);
        var text = cause is null
            ? $"The Extension Host connection on pipe '{connection.PipeName}' closed."
            : $"The Extension Host connection on pipe '{connection.PipeName}' closed: {cause.Message}";
        if (!string.IsNullOrWhiteSpace(problem))
        {
            text += " " + problem;
        }

        return text + OutputTail(null);
    }

    private string OutputTail(HostProcess? process)
    {
        HostProcess? started;
        lock (peerGate)
        {
            started = hostProcess;
        }

        started ??= process;
        var tail = started?.OutputTail();
        return string.IsNullOrWhiteSpace(tail) ? string.Empty : Environment.NewLine + tail;
    }

    private readonly record struct PeerBundle(IpcPeer Peer, NamedPipeClientTransport Transport, long Generation);

    /// <summary>One plugin's tool proxies plus the connection generation that registered them.</summary>
    private readonly record struct PluginTools(long Generation, IReadOnlyList<ExtensionHostTool> Tools);

    /// <summary>Where a new execution starts: the trusted chain, depth and context it inherits, the connection
    /// generation it is bound to, the parent lifetime a nested call is cancelled with, and the root invocation
    /// identity it belongs to (<see langword="null"/> for a top-level call, which starts a new root).</summary>
    private readonly record struct CallOrigin(
        IReadOnlyList<string> Ancestors,
        int Depth,
        long Generation,
        CancellationToken Token,
        ToolContext? ParentContext,
        string? RootRunId);
}
