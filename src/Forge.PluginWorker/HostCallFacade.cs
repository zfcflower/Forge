using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>Relays <see cref="IHostFacade.CallToolAsync"/> to the host over the worker's own pipe as a
/// <c>host.tool_call</c> request. The worker performs no permission checks: the main program decides and
/// its answer is returned verbatim.</summary>
internal sealed class HostCallFacade(IIpcPeer peer) : IHostFacade
{
    public async Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var request = WorkerJson.Serialize(
            new HostToolCallRequest { Name = name, Arguments = WorkerJson.ObjectOrEmpty(arguments) },
            WorkerJsonContext.Default.HostToolCallRequest);

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
