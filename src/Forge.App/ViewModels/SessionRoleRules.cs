using Forge.Abstractions;

namespace Forge.App.ViewModels;

/// <summary>What an open tab has to do to show the row the store holds for it.</summary>
internal enum SessionRoleAction
{
    /// <summary>The view already runs the role, root and read-only state the row holds.</summary>
    Keep,

    /// <summary>The row left Active: the tab keeps its view as the read-only history view it now is.</summary>
    ReadOnly,

    /// <summary>The role or the root moved: the tab is given a view built for the row.</summary>
    Rebuild,
}

/// <summary>
/// A tab's role against its stored row. The role is the row's parent link, nothing else: a row with a parent is an agent
/// session and a row without one is the branch's root, and that role decides the tool surface (Core strips the extension
/// tools from every Depth &gt; 0 agent), the runtime a tab runs on, the turn gate it checks and the composer it shows.
/// A promote, a demote, a handover or a restore moves the row; the view that was built for the old role is stale from that
/// moment, however green the rest of its wiring looks.
/// </summary>
internal static class SessionRoleRules
{
    /// <summary>Whether <paramref name="row"/> is an agent session (a child of a root).</summary>
    public static bool IsAgent(SessionInfo row) => row.ParentSessionId is not null;

    /// <summary>
    /// What <paramref name="view"/> has to do. A read-only view runs nothing, so it only moves when its row became Active
    /// again ("continue"); a live view follows its row everywhere the role reaches.
    /// </summary>
    /// <param name="viewRootId">The root an agent-session view hangs under; null for an ordinary session's view.</param>
    public static SessionRoleAction ActionFor(SessionInfo row, bool viewIsAgentSession, bool viewIsReadOnly, string? viewRootId)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (viewIsReadOnly)
        {
            // A history tab has no runtime and no turn to get wrong: its label is the row's own title, so a role that
            // moved behind its back changes nothing a reader could act on.
            return row.Status == SessionStatus.Active ? SessionRoleAction.Rebuild : SessionRoleAction.Keep;
        }

        if (row.Status != SessionStatus.Active)
        {
            return SessionRoleAction.ReadOnly;
        }

        if (viewIsAgentSession != IsAgent(row))
        {
            return SessionRoleAction.Rebuild;
        }

        return viewIsAgentSession && !string.Equals(viewRootId, row.ParentSessionId, StringComparison.Ordinal)
            ? SessionRoleAction.Rebuild
            : SessionRoleAction.Keep;
    }
}
