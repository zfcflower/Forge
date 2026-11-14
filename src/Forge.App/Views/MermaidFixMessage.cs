using Forge.App.Localization;

namespace Forge.App.Views;

/// <summary>
/// Builds the message a failed Mermaid block hands back to the model (wave W11): the error, the source, and one instruction -
/// the failure loop exists so a bad diagram is fixed in the conversation instead of leaving the user with a blank box.
/// </summary>
internal static class MermaidFixMessage
{
    /// <summary>Longest source carried in the message; a larger diagram is truncated with a note rather than flooding the prompt.</summary>
    internal const int MaxSourceCharacters = 16 * 1024;

    /// <summary>
    /// The prompt in the current UI language. The source is fenced with more backticks than it contains, so a diagram whose
    /// labels include a fence cannot end the block early.
    /// </summary>
    internal static string Build(string source, string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(error);

        var text = source.Length > MaxSourceCharacters
            ? source[..MaxSourceCharacters] + "\n" + Loc.Instance[MermaidStrings.FixTruncated]
            : source;
        return Loc.Instance.Format(MermaidStrings.FixRequest, error, text, Fence(text));
    }

    /// <summary>The fence to wrap <paramref name="source"/> in: three backticks, or one more than its longest run.</summary>
    internal static string Fence(string source)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in source)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest)
            {
                longest = run;
            }
        }

        return new string('`', Math.Max(3, longest + 1));
    }
}
