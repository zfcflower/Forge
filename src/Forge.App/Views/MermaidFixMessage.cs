using System.Globalization;

namespace Forge.App.Views;

/// <summary>
/// Builds the message a failed Mermaid block hands back to the model (wave W11): the error, the source, and one instruction -
/// the failure loop exists so a bad diagram is fixed in the conversation instead of leaving the user with a blank box.
/// <para>The message is fixed English, not localized: it is an instruction to another AI, not interface copy, so it must not
/// drift with the UI language (the same rule the W5 checks hand-off follows). Only what the user reads around the hand-off -
/// the button, the status and the origin label (<see cref="Localization.MermaidStrings.FixOrigin"/>) - is localized.</para>
/// </summary>
internal static class MermaidFixMessage
{
    /// <summary>Longest source carried in the message; a larger diagram is truncated with a note rather than flooding the prompt.</summary>
    internal const int MaxSourceCharacters = 16 * 1024;

    /// <summary>
    /// The request to the model: <c>{0}</c> the render error, <c>{1}</c> the diagram source, <c>{2}</c> the fence to wrap it
    /// in. Fixed English - model-directed text, see the class summary.
    /// </summary>
    private const string Request =
        "This Mermaid diagram failed to render locally; please fix it.\n\nError: {0}\n\nSource:\n{2}mermaid\n{1}\n{2}\n\nReply with the corrected, complete Mermaid code block only.";

    /// <summary>Appended when the source had to be cut to fit the message. Fixed English - model-directed text.</summary>
    private const string Truncated = "… (the source was truncated)";

    /// <summary>
    /// Builds the hand-off message in fixed English. The source is fenced with more backticks than it contains, so a diagram
    /// whose labels include a fence cannot end the block early.
    /// </summary>
    internal static string Build(string source, string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(error);

        var text = source.Length > MaxSourceCharacters
            ? source[..MaxSourceCharacters] + "\n" + Truncated
            : source;
        return string.Format(CultureInfo.InvariantCulture, Request, error, text, Fence(text));
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
