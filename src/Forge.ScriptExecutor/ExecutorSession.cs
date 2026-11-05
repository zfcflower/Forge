using System.Globalization;
using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.Scripting;
using Forge.ScriptRuntime;

namespace Forge.ScriptExecutor;

/// <summary>
/// One executor's protocol surface: <c>executor.load</c> validates an artifact and loads it from memory,
/// <c>executor.run</c> runs it exactly once through <see cref="ScriptHarness"/>, <c>executor.stop</c> exits cleanly.
/// Every other operation is refused. A second load or a second run is refused rather than served, so an executor can
/// never carry one call's image, run binding or tool metadata into another.
/// </summary>
internal sealed class ExecutorSession
{
    private readonly IIpcPeer peer;
    private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScriptEntryPoint? entryPoint;
    private int loads;
    private int runs;

    public ExecutorSession(IIpcPeer peer)
    {
        this.peer = peer;
        peer.Closed += cause => disconnected.TrySetResult(cause);
        peer.RequestReceived += HandleRequestAsync;
    }

    /// <summary>Serves until <c>executor.stop</c> arrives or the connection closes. Returns 0 for a clean stop
    /// (including the host dropping the pipe), 1 when the connection failed.</summary>
    public async Task<int> ServeAsync(CancellationToken ct)
    {
        using var registration = ct.Register(static state => ((ExecutorSession)state!).RequestStop(), this);
        await Task.WhenAny(stopRequested.Task, disconnected.Task).ConfigureAwait(false);
        var failed = !stopRequested.Task.IsCompleted && !IsCleanClose(disconnected);
        await peer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    private Task HandleRequestAsync(IpcMessage message, CancellationToken ct) => message.Operation switch
    {
        ScriptExecutorProtocol.Load => HandleLoadAsync(message),
        ScriptExecutorProtocol.Run => HandleRunAsync(message, ct),
        ScriptExecutorProtocol.Stop => HandleStopAsync(message),
        _ => RespondErrorAsync(
            message.Id,
            "unsupported_operation",
            $"The script executor does not serve '{message.Operation}'; it only loads one image, runs it once and stops."),
    };

    private async Task HandleLoadAsync(IpcMessage message)
    {
        if (Volatile.Read(ref loads) != 0)
        {
            await RespondErrorAsync(
                message.Id,
                "already_loaded",
                "This executor has already loaded an image; a single-use executor never replaces it.").ConfigureAwait(false);
            return;
        }

        ScriptArtifactLoadRequest? request;
        try
        {
            request = message.Arguments?.Deserialize(ScriptExecutorJsonContext.Default.ScriptArtifactLoadRequest);
        }
        catch (JsonException ex)
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", ex.Message).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", "executor.load requires an artifact and its descriptor.").ConfigureAwait(false);
            return;
        }

        ScriptEntryPoint loaded;
        try
        {
            // The bound is checked on the encoded length first, so a hostile length cannot make this process allocate;
            // the decoded bytes are then validated before anything is loaded.
            var image = ScriptArtifact.Decode(request.AssemblyBase64);
            ScriptArtifact.Validate(request.Descriptor, image);
            loaded = LoadEntryPoint(image, request.Descriptor);
        }
        catch (ScriptArtifactException ex)
        {
            await RespondErrorAsync(message.Id, "invalid_artifact", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or TypeLoadException or ArgumentException)
        {
            await RespondErrorAsync(message.Id, "load_failed", $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return;
        }

        // Only a load that actually succeeded spends the one image this executor will ever hold: a rejected load leaves
        // the executor empty and still able to refuse the next one for its own reason.
        if (Interlocked.Exchange(ref loads, 1) != 0)
        {
            await RespondErrorAsync(
                message.Id,
                "already_loaded",
                "This executor has already loaded an image; a single-use executor never replaces it.").ConfigureAwait(false);
            return;
        }

        entryPoint = loaded;
        await peer.RespondAsync(message.Id, EmptyResult(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleRunAsync(IpcMessage message, CancellationToken ct)
    {
        if (Volatile.Read(ref entryPoint) is not { } entry)
        {
            await RespondErrorAsync(message.Id, "not_loaded", "No script image was loaded; executor.run is refused.").ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref runs) != 0)
        {
            await RespondErrorAsync(
                message.Id,
                "already_ran",
                "This executor has already run its script; a single-use executor never runs a second one.").ConfigureAwait(false);
            return;
        }

        ScriptRunRequest? request;
        try
        {
            request = message.Arguments?.Deserialize(ScriptExecutorJsonContext.Default.ScriptRunRequest);
        }
        catch (JsonException ex)
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", ex.Message).ConfigureAwait(false);
            return;
        }

        if (request is null || request.Context is null)
        {
            await RespondErrorAsync(
                message.Id,
                "invalid_arguments",
                "executor.run requires the call context assigned by the main program.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Context.RunId))
        {
            await RespondErrorAsync(
                message.Id,
                "invalid_arguments",
                "executor.run carries no run id; an unbound run is refused.").ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref runs, 1) != 0)
        {
            await RespondErrorAsync(
                message.Id,
                "already_ran",
                "This executor has already run its script; a single-use executor never runs a second one.").ConfigureAwait(false);
            return;
        }

        CodeExecutionResult result;
        try
        {
            var settings = new ScriptRunSettings(
                request.Tools ?? [],
                request.Options ?? new CodeExecutionOptions(),
                request.TimeoutMilliseconds);
            result = await ScriptHarness
                .RunAsync(entry, new ExecutorHostFacade(peer, request.Context.RunId), settings, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = new CodeExecutionResult($"{ex.GetType().Name}: {ex.Message}", IsError: true);
        }

        await peer
            .RespondAsync(message.Id, HostJson(result), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task HandleStopAsync(IpcMessage message)
    {
        await peer.RespondAsync(message.Id, EmptyResult(), CancellationToken.None).ConfigureAwait(false);
        RequestStop();
    }

    private static ScriptEntryPoint LoadEntryPoint(byte[] image, ScriptArtifactDescriptor descriptor)
    {
        // LoadFromStream only: no Assembly.Load, no temporary file and no probing of the file system.
        var context = new System.Runtime.Loader.AssemblyLoadContext("forge-script-executor", isCollectible: false);
        var assembly = context.LoadFromStream(new MemoryStream(image, writable: false));
        var type = assembly.GetType(descriptor.EntryPointType, throwOnError: false) ??
            throw new ScriptArtifactException(
                $"The script image does not contain the entry point type '{descriptor.EntryPointType}'.");
        var method = type.GetMethod(
            descriptor.EntryPointMethod,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) ??
            throw new ScriptArtifactException(
                $"The script image does not contain the entry point '{descriptor.EntryPointType}.{descriptor.EntryPointMethod}'.");
        return method.CreateDelegate<ScriptEntryPoint>();
    }

    private void RequestStop() => stopRequested.TrySetResult();

    private Task RespondErrorAsync(long requestId, string code, string message) =>
        peer.RespondErrorAsync(requestId, new IpcError(code, message), CancellationToken.None);

    private static JsonElement EmptyResult() => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement HostJson(CodeExecutionResult result) =>
        JsonSerializer.SerializeToElement(result, ScriptExecutorJsonContext.Default.CodeExecutionResult);

    private static bool IsCleanClose(TaskCompletionSource<Exception?> source) =>
        !source.Task.IsCompletedSuccessfully ||
        source.Task.Result is null ||
        source.Task.Result is IOException or OperationCanceledException;
}

/// <summary>
/// Relays <see cref="IHostFacade.CallToolAsync"/> to the host as a <c>host.tool_call</c> request
/// <c>{runId, name, args}</c>. One instance belongs to exactly one run: the run id is the one the host bound, so the host
/// validates the callback against its active-run table and stamps the identity itself. This process performs no
/// permission check and never reports a session, working directory or depth.
/// </summary>
internal sealed class ExecutorHostFacade(IIpcPeer peer, string runId) : IHostFacade
{
    public string RunId { get; } = runId;

    public async Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var request = JsonSerializer.SerializeToElement(
            new BoundToolCallRequest(
                RunId,
                name,
                arguments.ValueKind == JsonValueKind.Undefined ? EmptyObject() : arguments),
            ScriptExecutorJsonContext.Default.BoundToolCallRequest);

        try
        {
            var response = await peer
                .SendRequestAsync(ScriptExecutorProtocol.HostToolCall, request, ct)
                .ConfigureAwait(false);
            if (response.Result is not { } payload)
            {
                return new HostToolResult($"host tool '{name}' returned no result", IsError: true);
            }

            var result = payload.Deserialize(ScriptExecutorJsonContext.Default.ToolResultPayload);
            return result is null
                ? new HostToolResult($"host tool '{name}' returned no result", IsError: true)
                : new HostToolResult(result.Content ?? string.Empty, result.IsError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcRemoteException ex)
        {
            return new HostToolResult(
                string.Create(CultureInfo.InvariantCulture, $"{ex.Code}: {ex.Message}"),
                IsError: true);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IpcTimeoutException or IpcProtocolException or IOException)
        {
            return new HostToolResult($"the host did not answer '{name}': {ex.Message}", IsError: true);
        }
        catch (JsonException ex)
        {
            return new HostToolResult($"the host answered '{name}' with an invalid result: {ex.Message}", IsError: true);
        }
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
