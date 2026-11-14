using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// A session that can take a message on behalf of the UI (wave W11): the checks panel's "ask the agent to fix" and a Mermaid
/// block's failed diagram both hand a prepared prompt to the session that owns the work. One contract keeps the two from
/// growing two different answers to "is a turn running, should this queue, where does a withdrawn message go".
/// </summary>
internal interface IAgentMessageHost
{
    /// <summary>False once the tab is closed, taken over, archived or removed: nothing may be delivered here any more.</summary>
    bool CanHandOff { get; }

    /// <summary>
    /// Delivers <paramref name="message"/>: runs it as one turn when the session is idle, otherwise appends it to the composer's
    /// draft so the user sends it when the current turn is done - never a second turn behind the user's back.
    /// </summary>
    /// <returns>True when a turn ran, false when the message was queued in the prompt box.</returns>
    Task<bool> HandMessageAsync(string message, LocText origin);
}
