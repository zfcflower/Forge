using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>Relays <see cref="IHostFacade.CallToolAsync"/> to the host over the worker's own pipe as a
/// <c>host.tool_call</c> request of the form <c>{runId, name, args}</c>. One instance belongs to exactly one execution:
/// the run id is the one the host bound to that execution, so the host can validate the callback against its active-run
/// table and stamp the plugin identity itself. The worker performs no permission checks and never reports a session,
/// working directory or depth: those come from the host's binding, not from the plugin process.</summary>
internal sealed class HostCallFacade(
    IIpcPeer peer,
    string runId,
    string sessionId,
    string workingDirectory,
    int depth) : IHostFacade
{
    public string RunId { get; } = runId;

    public string SessionId { get; } = sessionId;

    public string WorkingDirectory { get; } = workingDirectory;

    public int Depth { get; } = depth;

    public async Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var request = WorkerJson.Serialize(
            new BoundToolCallRequest
            {
                RunId = RunId,
                Name = name,
                Arguments = WorkerJson.ObjectOrEmpty(arguments),
            },
            WorkerJsonContext.Default.BoundToolCallRequest);

        try
        {
            var response = await peer.SendRequestAsync(ExtensionProtocol.HostToolCall, request, ct).ConfigureAwait(false);
            if (response.Result is not { } payload)
            {
                return new HostToolResult($"host tool '{name}' returned no result", IsError: true);
            }

            var result = payload.Deserialize(WorkerJsonContext.Default.ToolResultPayload);
            return result is null
                ? new HostToolResult($"host tool '{name}' returned no result", IsError: true)
                : new HostToolResult(result.Content, result.IsError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcRemoteException ex)
        {
            return ex.Code.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? new HostToolResult($"denied: {ex.Message}", IsError: true)
                : new HostToolResult($"{ex.Code}: {ex.Message}", IsError: true);
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
}
