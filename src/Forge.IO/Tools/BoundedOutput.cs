using System.Text;

namespace Forge.IO.Tools;

/// <summary>
/// Caps a line-oriented tool result at the size the loop would otherwise spill, cutting at a line boundary and saying how to
/// narrow the call. A result this size stays in the conversation, so the model never needs a second call to read it back.
/// </summary>
internal static class BoundedOutput
{
    internal const int MaxChars = 32_000;

    public static string Join(IReadOnlyList<string> lines, string narrowingHint)
    {
        var total = 0;
        var shown = 0;
        while (shown < lines.Count && total + lines[shown].Length + 1 <= MaxChars)
        {
            total += lines[shown].Length + 1;
            shown++;
        }

        if (shown == lines.Count)
        {
            return string.Join('\n', lines);
        }

        var text = new StringBuilder(total + 160);
        for (var index = 0; index < shown; index++)
        {
            text.Append(lines[index]).Append('\n');
        }

        text.Append("… (output cut after ").Append(shown).Append(" lines; ").Append(lines.Count - shown).Append(" more not shown) ").Append(narrowingHint);
        return text.ToString();
    }
}
