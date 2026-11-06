using Forge.Abstractions;

namespace Forge.HostClient;

/// <summary>
/// Session-aware seam for <c>host.tool_call</c>: a plugin or workflow run calls a main-program tool on behalf of one
/// session, so the tool set and the approval path must be that session's. <paramref name="sessionId"/> is always the
/// session recorded for the run when its Host-validated context was registered, never anything the worker claims.
/// An unknown or closed session must fail closed (<see langword="null"/> tool, <see cref="ApprovalDecision.Deny"/>).
/// </summary>
public interface IHostToolCallRouter
{
    /// <summary>Finds the tool in the given session's tool set, or null when the session or the tool is unknown or not callable.</summary>
    ITool? Resolve(string sessionId, string toolName);

    /// <summary>Applies the session's policy and user approval to a call the run is about to make.</summary>
    ValueTask<ApprovalDecision> ApproveAsync(string sessionId, ApprovalRequest request, CancellationToken ct);
}
