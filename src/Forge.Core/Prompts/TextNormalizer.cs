using System.Text;

namespace Forge.Core.Prompts;

/// <summary>
/// Canonicalizes prompt text so identical logical content yields identical bytes: line endings become
/// <c>\n</c>, trailing whitespace per line is dropped, and leading/trailing blank lines are removed.
/// </summary>
internal static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!NeedsNormalization(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var span = text.AsSpan();
        var index = 0;
        while (index <= span.Length)
        {
            var lineEnd = index;
            while (lineEnd < span.Length && span[lineEnd] != '\n' && span[lineEnd] != '\r')
            {
                lineEnd++;
            }

            var line = span[index..lineEnd];
            var contentEnd = line.Length;
            while (contentEnd > 0 && char.IsWhiteSpace(line[contentEnd - 1]))
            {
                contentEnd--;
            }

            builder.Append(line[..contentEnd]);

            if (lineEnd >= span.Length)
            {
                break;
            }

            builder.Append('\n');
            index = lineEnd + 1;
            if (span[lineEnd] == '\r' && index < span.Length && span[index] == '\n')
            {
                index++;
            }
        }

        return builder.ToString().Trim('\n');
    }

    private static bool NeedsNormalization(string text)
    {
        if (text[0] == '\n' || text[0] == '\r' || char.IsWhiteSpace(text[^1]))
        {
            return true;
        }

        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '\r')
            {
                return true;
            }

            if (c == '\n' || !char.IsWhiteSpace(c))
            {
                continue;
            }

            var next = i + 1 < span.Length ? span[i + 1] : '\n';
            if (next == '\n' || next == '\r')
            {
                return true;
            }
        }

        return false;
    }
}
