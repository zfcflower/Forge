using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>
/// What a session's approval and question handlers know about the user-driven child sessions running in its runtime
/// (forge-app "Agent 会话的界面与接线"). A child run shares the root's host, tool set, policy and remembered rules, so an
/// <c>ApprovalRequest</c> names no session; the runtime answers which user session (if any) the calling flow belongs to,
/// and which child session ids it owns, so a card can be put in that session's own tab.
/// </summary>
internal interface IAgentSessionScope
{
    /// <summary>
    /// The user-driven child session whose run the calling flow belongs to; null in the root's own turn and in a
    /// model-spawned child's run (those cards stay with the root, as before).
    /// </summary>
    string? CurrentChildSessionId { get; }

    /// <summary>The name of a user-driven child session of this root, or null when the id is not one of them.</summary>
    string? ChildSessionTitle(string sessionId);

    /// <summary>
    /// Every user-driven child session of this root: releasing the runtime must withdraw their cards too, exactly as it
    /// withdraws the root's.
    /// </summary>
    IReadOnlyList<string> ChildSessionIds { get; }
}

/// <summary>Which session a card shows, and under which title.</summary>
internal static class AgentSessionCardOwner
{
    /// <summary>
    /// The session a card belongs to: the user-driven child that asked when <paramref name="requesterSessionId"/> is one of
    /// them, else the handler's own session (the root). The title of a child's card is the tab's own spelling,
    /// <c>root › child</c>; the tool set, the policy and the remembered rules stay the root's either way.
    /// </summary>
    public static (string SessionId, string Title) Resolve(
        IAgentSessionScope? scope,
        string? requesterSessionId,
        string sessionId,
        string sessionTitle)
    {
        if (requesterSessionId is { Length: > 0 } childId
            && scope?.ChildSessionTitle(childId) is { Length: > 0 } childTitle)
        {
            return (childId, sessionTitle + AgentSessionLabel.Separator + childTitle);
        }

        return (sessionId, sessionTitle);
    }
}
