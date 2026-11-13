using System.Runtime.InteropServices;
using System.Text.Json;
using Forge.Plugin.Sdk;

namespace Forge.Ipc.Protocol;

/// <summary>The one <c>host.tool_call</c> client both callback facades use (Plugin Worker and script executor): the
/// run id is the one the host bound, so the host validates the call against its active-run table and stamps the
/// identity itself; the caller never reports a session, working directory or depth.</summary>
public static class HostToolCalls
{
    /// <summary>Sends one call and maps every failure that is not the caller's own cancellation to an error result.
    /// With <paramref name="labelDenied"/>, a remote error whose code mentions <c>denied</c> reads <c>denied: …</c>.</summary>
    public static async Task<HostToolResult> CallAsync(
        IIpcPeer peer,
        string runId,
        string name,
        JsonElement arguments,
        bool labelDenied,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var request = new HostToolCallRequest(runId, name, ArgumentsUtf8(arguments));
            var result = await peer
                .RequestAsync<HostToolCallRequest, ToolResult>(ExtensionProtocol.HostToolCall, request, ct)
                .ConfigureAwait(false);
            return new HostToolResult(result.Content ?? string.Empty, result.IsError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcRemoteException ex)
        {
            return labelDenied && ex.Code.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? new HostToolResult($"denied: {ex.Message}", IsError: true)
                : new HostToolResult($"{ex.Code}: {ex.Message}", IsError: true);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IpcTimeoutException or IpcProtocolException or IOException)
        {
            return new HostToolResult($"the host did not answer '{name}': {ex.Message}", IsError: true);
        }
    }

    /// <summary>The wire form of tool arguments: the element's raw UTF-8 bytes (one copy, no re-serialization); an
    /// absent or null value is the empty payload, which the receiver reads as <c>{}</c>.</summary>
    public static ReadOnlyMemory<byte> ArgumentsUtf8(JsonElement arguments) =>
        arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? ReadOnlyMemory<byte>.Empty
            : JsonMarshal.GetRawUtf8Value(arguments).ToArray();
}
