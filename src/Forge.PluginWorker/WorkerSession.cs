using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>Handles the host's requests on the worker pipe: <c>worker.load</c>, <c>worker.execute</c>,
/// <c>worker.unload</c> and <c>worker.stop</c>. Tool executions run concurrently behind one bounded gate;
/// a failing tool never terminates the session.</summary>
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
        ExtensionProtocol.WorkerUnload => HandleUnloadAsync(message, ct),
        ExtensionProtocol.WorkerStop => HandleStopAsync(message),
        _ => RespondErrorAsync(
            message.Id,
            "unsupported_operation",
            $"The worker does not handle '{message.Operation}'."),
    };

    private async Task HandleLoadAsync(IpcMessage message, CancellationToken ct)
    {
        var request = WorkerJson.TryDeserialize(
            message.Arguments,
            WorkerJsonContext.Default.WorkerLoadRequest,
            out var error);
        if (request is null)
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
            var tools = await plugins
                .LoadAsync(
                    request.PluginId,
                    request.Version ?? "",
                    request.AssemblyPath,
                    request.AssemblySha256,
                    request.Files,
                    ct)
                .ConfigureAwait(false);
            var result = WorkerJson.Serialize(
                new WorkerLoadResult { Tools = tools },
                WorkerJsonContext.Default.WorkerLoadResult);
            await peer.RespondAsync(message.Id, result, CancellationToken.None).ConfigureAwait(false);
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
        var request = WorkerJson.TryDeserialize(
            message.Arguments,
            WorkerJsonContext.Default.WorkerExecuteRequest,
            out var error);
        if (request is null)
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", error).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Tool))
        {
            await RespondErrorAsync(message.Id, "invalid_arguments", "'tool' is required.").ConfigureAwait(false);
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
            // Per invocation: one facade per execution, so concurrent calls never share session, directory or depth.
            var host = new HostCallFacade(
                peer,
                request.SessionId ?? "",
                request.WorkingDir ?? "",
                request.Depth ?? 0);

            ToolResult result;
            try
            {
                result = await plugins
                    .ExecuteAsync(
                        request.PluginId,
                        request.Tool,
                        WorkerJson.ObjectOrEmpty(request.Arguments),
                        host,
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

            var payload = WorkerJson.Serialize(
                new ToolResultPayload { Content = result.Content, IsError = result.IsError },
                WorkerJsonContext.Default.ToolResultPayload);
            await peer.RespondAsync(message.Id, payload, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task HandleUnloadAsync(IpcMessage message, CancellationToken ct)
    {
        var request = WorkerJson.TryDeserialize(
            message.Arguments,
            WorkerJsonContext.Default.WorkerUnloadRequest,
            out _);

        try
        {
            await plugins.UnloadAsync(request?.PluginId, ct).ConfigureAwait(false);
            await peer.RespondAsync(message.Id, WorkerJson.EmptyObject, CancellationToken.None).ConfigureAwait(false);
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
        await peer.RespondAsync(message.Id, WorkerJson.EmptyObject, CancellationToken.None).ConfigureAwait(false);
        RequestStop();
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
