using System.Text.Json;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>Relays <see cref="IHostFacade.CallToolAsync"/> to the host over the worker's own pipe as a
/// <c>host.tool_call</c> request (<see cref="HostToolCallRequest"/> → <see cref="ToolResult"/>). One instance belongs to
/// exactly one execution: the run id is the one the host bound to that execution, so the host can validate the callback
/// against its active-run table and stamp the plugin identity itself. The worker performs no permission checks and never
/// reports a session, working directory or depth: those come from the host's binding, not from the plugin process.</summary>
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

    public Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
        HostToolCalls.CallAsync(peer, RunId, name, arguments, labelDenied: true, ct);
}
