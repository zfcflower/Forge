// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// The lines a highlighter reads: an immutable text snapshot, a diff view or a single string. Line text carries no line
// terminator.
public interface IHighlightSource
{
    int LineCount { get; }

    ReadOnlySpan<char> GetLine(int index);
}

// Splits a string into lines once, so callers without a document snapshot can highlight text directly.
public sealed class StringHighlightSource : IHighlightSource
{
    private readonly string[] _lines;

    public StringHighlightSource(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _lines = SplitLines(text);
    }

    public int LineCount => _lines.Length;

    public ReadOnlySpan<char> GetLine(int index) => _lines[index];

    public static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        var span = text.AsSpan();
        var start = 0;
        while (start <= span.Length)
        {
            var index = span[start..].IndexOfAny('\r', '\n');
            if (index < 0)
            {
                break;
            }

            var at = start + index;
            lines.Add(text.Substring(start, at - start));
            start = at + 1;
            if (span[at] == '\r' && start < span.Length && span[start] == '\n')
            {
                start++;
            }
        }

        lines.Add(text.Substring(start));
        return [.. lines];
    }
}
