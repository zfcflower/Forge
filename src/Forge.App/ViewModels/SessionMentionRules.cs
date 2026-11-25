using Forge.Abstractions;

namespace Forge.App.ViewModels;

/// <summary>
/// The <c>@</c> picker's session group as a rule rather than a window: which sessions one tab may mention, and which tree's
/// pickers a rename has to re-read. A tab's role decides the list - an agent-session tab offers its root, a root offers the
/// agent sessions the user opened under it - and a session that is no longer active is not a target at all: its history tab is
/// a read-only replay, and a mention of it could no longer be delivered. Keeping this here (and pure) is what lets the rules be
/// tested without building the shell that owns the tabs.
/// </summary>
internal static class SessionMentionRules
{
    /// <summary>
    /// The sessions <paramref name="asking"/> may mention, in the order the picker shows them: the live agent tabs of a root,
    /// then the rows the shell read from the store (a closed session is offered from there).
    /// </summary>
    /// <param name="openAgentTabs">Every agent-session tab the window holds, whatever its root.</param>
    /// <param name="stored">The rows the shell read for this root's picker; null when it has not read them yet.</param>
    public static IReadOnlyList<SessionMentionTarget> Targets(
        SessionWorkspaceViewModel asking,
        IReadOnlyCollection<SessionWorkspaceViewModel> openAgentTabs,
        IReadOnlyList<SessionMentionTarget>? stored)
    {
        ArgumentNullException.ThrowIfNull(asking);
        ArgumentNullException.ThrowIfNull(openAgentTabs);
        if (asking.IsAgentSession)
        {
            return asking.AgentSessionRoot is { } root ? [new SessionMentionTarget(root.Id, root.Title)] : [];
        }

        var rootId = asking.SessionId;
        var targets = new List<SessionMentionTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in openAgentTabs)
        {
            if (MentionableChildId(tab, rootId) is { } childId && seen.Add(childId))
            {
                targets.Add(new SessionMentionTarget(childId, tab.AgentSessionName));
            }
        }

        if (stored is not null)
        {
            foreach (var target in stored)
            {
                if (seen.Add(target.SessionId))
                {
                    targets.Add(target);
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// The tree a renamed session's pickers belong to: the root it hangs under when its own agent-session tab is open, and the
    /// session itself otherwise - it is a root, or its tab is closed and only the stored lists carry its name.
    /// </summary>
    public static string TreeRootOf(string sessionId, IReadOnlyCollection<SessionWorkspaceViewModel> openAgentTabs)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(openAgentTabs);
        foreach (var tab in openAgentTabs)
        {
            if (string.Equals(tab.SessionId, sessionId, StringComparison.Ordinal))
            {
                return tab.AgentSessionRoot?.Id ?? sessionId;
            }
        }

        return sessionId;
    }

    /// <summary>
    /// The child session id of one open agent tab this root may mention; null when the tab hangs under another root, or when it
    /// is a history row's read-only replay - the session it shows is not active, so offering it would promise a delivery that
    /// can no longer happen.
    /// </summary>
    private static string? MentionableChildId(SessionWorkspaceViewModel tab, string rootId) =>
        tab.AgentChildSessionId is { } childId
        && !tab.IsReadOnlyView
        && tab.AgentSessionRoot is { } root
        && string.Equals(root.Id, rootId, StringComparison.Ordinal)
            ? childId
            : null;
}
