namespace Forge.App.ViewModels;

/// <summary>
/// The session the plan panel reads, as the shell sees it. The panel never holds a session view model: it needs the id the
/// checklist lives under, the title in its header, and whether the session still accepts a message at all.
/// <paramref name="IsReadOnly"/> is true for an archived or deleted session, whose plan is shown but whose agent cannot be
/// sent anything.
/// </summary>
internal sealed record PlanPanelSession(string Id, string Title, bool IsReadOnly);

/// <summary>What happened to a step the panel handed to the agent.</summary>
internal enum PlanHandoff
{
    /// <summary>The message became the session's next turn.</summary>
    Sent,

    /// <summary>A turn was running, so the message waits in the session's queue.</summary>
    Queued,

    /// <summary>The composer holds unsent text the panel must not overwrite; the message was not written anywhere.</summary>
    BlockedByDraft,

    /// <summary>No session, a read-only session, or one handing over; nothing was written.</summary>
    Unavailable,
}

/// <summary>
/// The shell's side of the plan panel: everything that needs a window, a session or the git row lives behind this seam, so
/// <see cref="PlanPanelViewModel"/> stays a read-only projection of the store a test can drive without a shell.
/// </summary>
internal interface IPlanPanelHost
{
    /// <summary>The active session the panel shows, or null when the window has none (the panel then only says so).</summary>
    PlanPanelSession? PlanSession { get; }

    /// <summary>True while the git row offers "open a pull request" for this session's branch.</summary>
    bool CanOpenPlanPullRequest { get; }

    /// <summary>Runs the shell's compose-pull-request flow; its body already starts with the plan.</summary>
    void OpenPlanPullRequest();

    /// <summary>
    /// Writes <paramref name="prompt"/> into the session's composer and sends it, or queues it while a turn runs. A
    /// composer holding unsent text is left alone: the panel reports that instead of overwriting the user's typing.
    /// </summary>
    PlanHandoff HandoffToAgent(string prompt);

    /// <summary>Closes the overlay (its close button, Escape, or a session switch).</summary>
    void ClosePlan();
}
