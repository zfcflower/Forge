using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>Handles the host's requests on the worker pipe: <c>worker.load</c>, <c>worker.execute</c>,
/// <c>worker.command</c>, <c>worker.prompt</c>, <c>worker.unload</c> and <c>worker.stop</c>. Tool executions run
/// concurrently behind one bounded gate and a failing tool never terminates the session. Scripts are not served here:
/// they run in a single-use <c>Forge.ScriptExecutor</c> process, so this process never compiles or runs script code.</summary>
internal sealed class WorkerSession
{
    private readonly IIpcPeer peer;
    private readonly PluginHost plugins;
    private readonly SemaphoreSlim executionGate;
    private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkerSession(IIpcPeer peer, WorkerOptions options)
    {
        this.peer = peer;
        plugins = new PluginHost { UnloadTimeout = options.UnloadTimeout };
        var limit = Math.Clamp(options.MaxConcurrency, 1, WorkerOptions.MaxAllowedConcurrency);
        executionGate = new SemaphoreSlim(limit, limit);

        peer.Closed += cause => disconnected.TrySetResult(cause);
        peer.RequestReceived += HandleRequestAsync;
    }

    /// <summary>Serves until <c>worker.stop</c> arrives or the connection closes. Returns 0 for a clean
    /// stop (including the host dropping the pipe), 1 when the connection failed.</summary>
    public async Task<int> ServeAsync(CancellationToken ct)
    {
        using var registration = ct.Register(
            static state => ((WorkerSession)state!).RequestStop(),
            this);

        await Task.WhenAny(stopRequested.Task, disconnected.Task).ConfigureAwait(false);
        var failed = !stopRequested.Task.IsCompleted && !IsCleanClose(disconnected);
        await peer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    /// <summary>Unloads whatever is loaded; called when the session ends so plugin code never outlives it.</summary>
    public Task ReleasePluginAsync() => plugins.UnloadAsync(pluginId: null, CancellationToken.None);

    private Task HandleRequestAsync(IpcMessage message, CancellationToken ct) => message.Operation switch
        {
            ExtensionProtocol.WorkerLoad => HandleLoadAsync(message, ct),
            ExtensionProtocol.WorkerExecute => HandleExecuteAsync(message, ct),
            ExtensionProtocol.WorkerCommand => HandleCommandAsync(message, ct),
            ExtensionProtocol.WorkerPrompt => HandlePromptAsync(message, ct),
            ExtensionProtocol.WorkerUnload => HandleUnloadAsync(message, ct),
            ExtensionProtocol.WorkerStop => HandleStopAsync(message),
            _ => RespondErrorAsync(
                message.Id,
                "unsupported_operation",
                $"The worker does not handle '{message.Operation}'."),
        };

    private async Task HandleLoadAsync(IpcMessage message, CancellationToken ct)
    {
        if (!TryRead<WorkerLoadRequest>(message, out var request, out var error))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", error).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", "'pluginId' is required.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.AssemblyPath))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", "'assemblyPath' is required.").ConfigureAwait(false);
            return;
        }

        try
        {
            var loadout = await plugins
                .LoadAsync(
                    request.PluginId,
                    request.Version ?? "",
                    request.AssemblyPath,
                    request.AssemblySha256,
                    request.Files,
                    request.Permissions,
                    ct)
                .ConfigureAwait(false);
            await peer
                .RespondAsync(message.Id, new WorkerLoadResponse(loadout.Contributions), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PluginLoadException ex)
        {
            await RespondErrorAsync(message.Id, "load_failed", ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(
                message.Id,
                "load_failed",
                $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task HandleExecuteAsync(IpcMessage message, CancellationToken ct)
    {
        if (!TryRead<WorkerExecuteRequest>(message, out var request, out var error))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", error).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Tool))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", "'tool' is required.").ConfigureAwait(false);
            return;
        }

        if (!WorkerCallContext.TryBind(request.Context, out var context, out var contextProblem))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", contextProblem).ConfigureAwait(false);
            return;
        }

        try
        {
            await executionGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Per invocation: one facade per execution, so concurrent calls never share a run id, session or directory.
            var host = new HostCallFacade(
                peer,
                context.RunId,
                context.SessionId,
                context.WorkingDirectory,
                context.Depth);

            ToolResult result;
            try
            {
                result = await plugins
                    .ExecuteAsync(
                        request.PluginId,
                        request.Tool,
                        request.ArgumentsJson,
                        host,
                        context,
                        request.TimeoutMilliseconds,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = new ToolResult($"{ex.GetType().Name}: {ex.Message}", IsError: true);
            }

            await peer.RespondAsync(message.Id, result, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task HandleCommandAsync(IpcMessage message, CancellationToken ct)
    {
        if (!TryRead<CommandExecutionRequest>(message, out var request, out var error))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", error).ConfigureAwait(false);
            return;
        }

        if (!WorkerCallContext.TryBind(request.Context, out var context, out var contextProblem))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", contextProblem).ConfigureAwait(false);
            return;
        }

        try
        {
            await executionGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var host = new HostCallFacade(peer, context.RunId, context.SessionId, context.WorkingDirectory, context.Depth);
            CommandOutput output;
            try
            {
                output = await plugins.ExecuteCommandAsync(request, host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                output = new CommandOutput($"{ex.GetType().Name}: {ex.Message}", IsError: true);
            }

            await peer.RespondAsync(message.Id, output, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task HandlePromptAsync(IpcMessage message, CancellationToken ct)
    {
        if (!TryRead<PromptExecutionRequest>(message, out var request, out var error))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", error).ConfigureAwait(false);
            return;
        }

        if (!WorkerCallContext.TryBind(request.Context, out var context, out var contextProblem))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", contextProblem).ConfigureAwait(false);
            return;
        }

        try
        {
            await executionGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var host = new HostCallFacade(peer, context.RunId, context.SessionId, context.WorkingDirectory, context.Depth);
            PromptOutput output;
            try
            {
                output = await plugins.EvaluatePromptAsync(request, host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (PluginExecutionException ex)
            {
                await RespondErrorAsync(message.Id, "prompt_failed", ex.Message).ConfigureAwait(false);
                return;
            }

            await peer.RespondAsync(message.Id, output, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task HandleUnloadAsync(IpcMessage message, CancellationToken ct)
    {
        // An unload without a readable id unloads whatever is loaded.
        TryRead<ExtensionIdRequest>(message, out var request, out _);

        try
        {
            await plugins.UnloadAsync(request?.PluginId, ct).ConfigureAwait(false);
            await peer.RespondAsync(message.Id, IpcEmpty.Instance, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(
                message.Id,
                "unload_failed",
                $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task HandleStopAsync(IpcMessage message)
    {
        await peer.RespondAsync(message.Id, IpcEmpty.Instance, CancellationToken.None).ConfigureAwait(false);
        RequestStop();
    }

    private static bool TryRead<T>(IpcMessage message, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value, out string? error)
        where T : class
    {
        try
        {
            value = message.Read<T>();
            error = null;
            return true;
        }
        catch (IpcProtocolException ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private void RequestStop() => stopRequested.TrySetResult();

    private Task RespondErrorAsync(long requestId, string code, string? message) =>
        peer.RespondErrorAsync(
            requestId,
            new IpcError(code, message ?? "the request could not be handled"),
            CancellationToken.None);

    private static bool IsCleanClose(TaskCompletionSource<Exception?> source) =>
        !source.Task.IsCompletedSuccessfully ||
        source.Task.Result is null ||
        source.Task.Result is IOException or OperationCanceledException;
}
