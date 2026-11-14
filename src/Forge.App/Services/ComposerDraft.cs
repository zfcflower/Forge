namespace Forge.App.Services;

/// <summary>
/// How a message handed to a session is appended to whatever the user already typed (wave W11: the checks panel and a Mermaid
/// block's failed diagram both do this). One owner, so a queued hand-off never erases a draft and never produces a different
/// blank-line rhythm depending on which surface queued it.
/// </summary>
internal static class ComposerDraft
{
    /// <summary>The composer text after a queued message: the existing draft, a blank line, then the message.</summary>
    internal static string Append(string? draft, string message) =>
        string.IsNullOrWhiteSpace(draft) ? message : draft.TrimEnd() + "\n\n" + message;
}
