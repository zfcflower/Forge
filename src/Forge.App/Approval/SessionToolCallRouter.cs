using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Core.Tools;
using Forge.HostClient;

namespace Forge.App.Approval;

/// <summary>
/// What the router needs from one live session: its current tool set (built-ins and external tools), the policy the agent
/// loop of that session evaluates and the session's own approval handler.
/// </summary>
internal interface ISessionToolScope
{
    ITool? FindTool(string toolName);

    IApprovalPolicy Policy { get; }

    /// <summary>The session's handler; null means an "ask" is a denial, exactly as in the agent loop.</summary>
    IApprovalHandler? ApprovalHandler { get; }
}

/// <summary>
/// Routes the <c>host.tool_call</c> requests of plugin and workflow runs to the session the run was registered with. The
/// tool comes from that session's tool set, the policy is the one the session's agent loop uses and an "ask" goes to that
/// session's <see cref="SessionApprovalHandler"/>, so remembered rules and the card's session label are right. An unknown or
/// closed session fails closed. A running sub-agent's child session id is registered as an alias of its root's scope (by
/// <c>SessionRuntime</c>), so a child call resolves against the root's tool set and policy and its cards name the root session. The five extension authoring tools are never reachable from a plugin: their policy is
/// "allow without asking" because the plugin card is the real gate, and a plugin must not be able to stage drafts.
/// </summary>
internal sealed class SessionToolCallRouter : IHostToolCallRouter
{
    private static readonly HashSet<string> Blocked = new(
        [
            ExtensionToolNames.List,
            ExtensionToolNames.Create,
            ExtensionToolNames.Update,
            ExtensionToolNames.Disable,
            ExtensionToolNames.Delete,
        ],
        StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ISessionToolScope> scopes = new(StringComparer.Ordinal);

    /// <summary>Makes a session reachable; a rebuilt runtime of the same session replaces the previous scope.</summary>
    public void Register(string sessionId, ISessionToolScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(scope);
        scopes[sessionId] = scope;
    }

    /// <summary>Removes the session, but only if the scope is still the registered one (a newer runtime keeps its slot).</summary>
    public void Unregister(string sessionId, ISessionToolScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(scope);
        scopes.TryRemove(new KeyValuePair<string, ISessionToolScope>(sessionId, scope));
    }

    public ITool? Resolve(string sessionId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrEmpty(toolName) || Blocked.Contains(toolName))
        {
            return null;
        }

        return scopes.TryGetValue(sessionId, out var scope) ? scope.FindTool(toolName) : null;
    }

    public async ValueTask<ApprovalDecision> ApproveAsync(string sessionId, ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(sessionId) || Blocked.Contains(request.ToolName) ||
            !scopes.TryGetValue(sessionId, out var scope))
        {
            return ApprovalDecision.Deny;
        }

        switch (scope.Policy.Evaluate(request))
        {
            case ApprovalMode.Allow:
                return ApprovalDecision.Allow;
            case ApprovalMode.Ask when scope.ApprovalHandler is { } handler:
                return await handler.RequestAsync(request, ct).ConfigureAwait(false);
            default:
                return ApprovalDecision.Deny;
        }
    }
}
