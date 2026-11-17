namespace Forge.App.Agents;

/// <summary>How one message is orchestrated: the session's routing as usual, no workflow at all, or one named workflow.</summary>
internal enum OrchestrationMode
{
    /// <summary>The root agent routes the work itself (the default; nothing is added to the message).</summary>
    Auto,

    /// <summary>The message is handled directly: no routing, no workflow.</summary>
    Direct,

    /// <summary>The message goes through the named workflow skill.</summary>
    Workflow,
}

/// <summary>The per-message orchestration choice picked in the composer. It applies to one message only.</summary>
internal readonly record struct OrchestrationChoice(OrchestrationMode Mode = OrchestrationMode.Auto, string Skill = "")
{
    public static OrchestrationChoice Auto => default;

    public static OrchestrationChoice Direct => new(OrchestrationMode.Direct);

    public static OrchestrationChoice ForWorkflow(string skill) => new(OrchestrationMode.Workflow, skill);
}

/// <summary>
/// The per-message orchestration directive. It travels as a tagged block at the head of the user message, not in the role
/// text: the root agent's role (with the skills index) is part of the cached prompt prefix, and changing it per message
/// would invalidate that cache. The block is stripped again wherever the message is shown or replayed.
/// </summary>
internal static class OrchestrationDirective
{
    private const string Open = "<orchestration>";
    private const string Close = "</orchestration>";

    /// <summary>The text sent to the model: <paramref name="text"/> unchanged for Auto, otherwise the directive plus the text.</summary>
    public static string Apply(OrchestrationChoice choice, string text) =>
        choice.Mode switch
        {
            OrchestrationMode.Direct =>
                Open + "For this message only, skip workflow routing: do not load choosing-a-workflow or any workflow skill, and handle the request directly." + Close + "\n\n" + text,
            OrchestrationMode.Workflow when choice.Skill.Length > 0 =>
                Open + "For this message only, the user chose the \"" + choice.Skill + "\" workflow: skip choosing-a-workflow, load the \"" + choice.Skill + "\" skill with the skill tool and follow it for this work." + Close + "\n\n" + text,
            _ => text,
        };

    /// <summary>Removes a leading directive added by <see cref="Apply"/>; text without one is returned as is.</summary>
    public static string Strip(string text)
    {
        if (!text.StartsWith(Open, StringComparison.Ordinal))
        {
            return text;
        }

        var end = text.IndexOf(Close, StringComparison.Ordinal);
        if (end < 0)
        {
            return text;
        }

        return text[(end + Close.Length)..].TrimStart('\n', '\r');
    }
}
