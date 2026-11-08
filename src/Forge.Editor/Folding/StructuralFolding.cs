using System.Buffers;
using Forge.Editor.Text;

namespace Forge.Editor.Folding;

/// <summary>Which structure a file folds along. The editor picks this from the file's language; this library only knows
/// the shapes, not the extensions.</summary>
public enum FoldingSyntax
{
    /// <summary>Only <c>#region</c>/<c>#endregion</c>-style markers.</summary>
    Region,

    /// <summary>Brace, bracket and parenthesis pairs, honouring comments and string literals.</summary>
    Braces,

    /// <summary>Indentation steps (Python/YAML style), with regions still honoured.</summary>
    Indent,
}

/// <summary>
/// Language-neutral folding heuristics over an immutable snapshot, used when no language server supplies folding ranges
/// or the server declares no such capability. One linear pass over a pooled window of the rope, so no per-line text is
/// allocated. A document above <see cref="MaxLines"/> yields no ranges at all rather than a scan that stalls editing, and
/// the result is capped at <see cref="MaxRanges"/>.
/// </summary>
public static class StructuralFolding
{
    /// <summary>Largest document the heuristic scans; above it the editor turns folding off and says so.</summary>
    public const int MaxLines = 20_000;

    /// <summary>Most ranges one document may produce; a pathological file stops here instead of building thousands.</summary>
    public const int MaxRanges = 5_000;

    /// <summary>Nesting depth the scanners track; deeper nesting is ignored, never unbounded.</summary>
    public const int MaxDepth = 256;

    private const int PrefixLength = 32;
    private const int WindowSize = 32 * 1024;
    private const int TabWidth = 4;

    /// <summary>
    /// Computes foldable ranges for <paramref name="text"/>. Ranges are valid (<see cref="FoldingRange.IsValid"/>), sorted
    /// by start line, and there is never more than one per start line: the longest wins, because the editor's folding
    /// manager matches sections by start offset and cannot tell two apart.
    /// </summary>
    public static IReadOnlyList<FoldingRange> Compute(TextSnapshot text, FoldingSyntax syntax)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IsEmpty || text.LineCount > MaxLines)
        {
            return [];
        }

        using var scanner = new Scanner(text, syntax);
        return scanner.Run();
    }

    private sealed class Scanner(TextSnapshot text, FoldingSyntax syntax) : IDisposable
    {
        private readonly List<FoldingRange> ranges = new(16);
        private readonly List<int> opens = new(16);
        private readonly List<OpenItem> indents = new(8);
        private readonly List<int> regions = new(4);
        private readonly char[] prefix = new char[PrefixLength];

        private char[] window = ArrayPool<char>.Shared.Rent(WindowSize);
        private int windowStart = -1;
        private int windowCount;

        private int line;
        private int prefixLength;
        private int indent;
        private bool leading = true;
        private bool blank = true;
        private int lastContentLine = -1;

        private bool inBlockComment;
        private bool inLineComment;
        private char quote;
        private bool verbatim;
        private int rawQuotes;

        public IReadOnlyList<FoldingRange> Run()
        {
            var length = text.Length;
            for (var offset = 0; offset <= length; offset++)
            {
                var atEnd = offset >= length;
                var c = atEnd ? '\n' : At(offset);
                if (!atEnd && c == '\r')
                {
                    // CRLF is one break, a lone CR is one too.
                    if (offset + 1 < length && At(offset + 1) == '\n')
                    {
                        offset++;
                    }

                    c = '\n';
                }

                if (c == '\n')
                {
                    EndLine();
                    if (ranges.Count >= MaxRanges)
                    {
                        break;
                    }

                    line++;
                    prefixLength = 0;
                    indent = 0;
                    leading = true;
                    blank = true;
                    inLineComment = false;
                    continue;
                }

                if (leading)
                {
                    if (c is ' ' or '\t')
                    {
                        indent += c == '\t' ? TabWidth : 1;
                        continue;
                    }

                    leading = false;
                    blank = false;
                }

                if (prefixLength < PrefixLength && !char.IsWhiteSpace(c))
                {
                    prefix[prefixLength++] = c;
                }

                if (syntax == FoldingSyntax.Braces)
                {
                    BraceStep(c, ref offset);
                }
            }

            CloseRegions();
            if (syntax == FoldingSyntax.Indent)
            {
                CloseIndents();
            }

            return Normalize();
        }

        public void Dispose()
        {
            if (window.Length > 0)
            {
                ArrayPool<char>.Shared.Return(window);
                window = [];
            }
        }

        private char At(int offset)
        {
            if (offset < windowStart || offset >= windowStart + windowCount)
            {
                windowStart = offset - (offset % WindowSize);
                windowCount = Math.Min(WindowSize, text.Length - windowStart);
                text.CopyTo(windowStart, window.AsSpan(0, windowCount));
            }

            return window[offset - windowStart];
        }

        private void BraceStep(char c, ref int offset)
        {
            if (inLineComment)
            {
                return;
            }

            if (inBlockComment)
            {
                if (c == '*' && offset + 1 < text.Length && At(offset + 1) == '/')
                {
                    inBlockComment = false;
                    offset++;
                }

                return;
            }

            if (rawQuotes > 0)
            {
                if (c != '"')
                {
                    return;
                }

                var run = 1;
                while (run < rawQuotes && offset + run < text.Length && At(offset + run) == '"')
                {
                    run++;
                }

                if (run >= rawQuotes)
                {
                    offset += rawQuotes - 1;
                    rawQuotes = 0;
                }

                return;
            }

            if (quote != '\0')
            {
                if (verbatim)
                {
                    if (c != quote)
                    {
                        return;
                    }

                    if (offset + 1 < text.Length && At(offset + 1) == quote)
                    {
                        offset++;
                        return;
                    }

                    quote = '\0';
                    return;
                }

                if (c == '\\')
                {
                    offset++;
                    return;
                }

                if (c == quote)
                {
                    quote = '\0';
                }

                return;
            }

            switch (c)
            {
                case '/' when offset + 1 < text.Length && At(offset + 1) == '/':
                    inLineComment = true;
                    offset++;
                    return;
                case '/' when offset + 1 < text.Length && At(offset + 1) == '*':
                    inBlockComment = true;
                    offset++;
                    return;
                case '"':
                    if (offset + 2 < text.Length && At(offset + 1) == '"' && At(offset + 2) == '"')
                    {
                        rawQuotes = 3;
                        while (rawQuotes < 8 && offset + rawQuotes < text.Length && At(offset + rawQuotes) == '"')
                        {
                            rawQuotes++;
                        }

                        offset += rawQuotes - 1;
                        return;
                    }

                    verbatim = offset > 0 && At(offset - 1) == '@';
                    quote = '"';
                    return;
                case '\'':
                case '`':
                    verbatim = false;
                    quote = c;
                    return;
                case '{':
                case '[':
                case '(':
                    if (opens.Count < MaxDepth)
                    {
                        opens.Add((line << 8) | c);
                    }

                    return;
                case '}':
                case ']':
                case ')':
                    var expected = c switch { '}' => '{', ']' => '[', _ => '(' };
                    for (var i = opens.Count - 1; i >= 0; i--)
                    {
                        if ((opens[i] & 0xFF) != expected)
                        {
                            continue;
                        }

                        var start = opens[i] >> 8;
                        if (line > start)
                        {
                            Add(new FoldingRange(start, line));
                        }

                        opens.RemoveRange(i, opens.Count - i);
                        return;
                    }

                    return;
            }
        }

        private void EndLine()
        {
            if (prefixLength > 0)
            {
                RegionStep();
            }

            if (syntax == FoldingSyntax.Indent && !blank)
            {
                IndentStep();
            }

            if (!blank)
            {
                lastContentLine = line;
            }
        }

        private void RegionStep()
        {
            var marker = TrimMarker(prefix.AsSpan(0, prefixLength));
            if (marker.StartsWith("endregion", StringComparison.OrdinalIgnoreCase))
            {
                if (regions.Count == 0)
                {
                    return;
                }

                var start = regions[^1];
                regions.RemoveAt(regions.Count - 1);
                Add(new FoldingRange(start, line, FoldingKind.Region));
                return;
            }

            if (marker.StartsWith("region", StringComparison.OrdinalIgnoreCase) && regions.Count < MaxDepth)
            {
                regions.Add(line);
            }
        }

        private void IndentStep()
        {
            if (indents.Count == 0)
            {
                indents.Add(new OpenItem(line, indent));
                return;
            }

            if (indent > indents[^1].Value)
            {
                if (indents.Count < MaxDepth)
                {
                    indents.Add(new OpenItem(line, indent));
                }

                return;
            }

            // lastContentLine is still the previous content line here, so trailing blank lines never become part of a block.
            while (indents.Count > 0 && indent <= indents[^1].Value)
            {
                var open = indents[^1];
                indents.RemoveAt(indents.Count - 1);
                if (lastContentLine > open.Line)
                {
                    Add(new FoldingRange(open.Line, lastContentLine));
                }
            }

            indents.Add(new OpenItem(line, indent));
        }

        private void CloseRegions()
        {
            if (lastContentLine < 0)
            {
                return;
            }

            for (var i = regions.Count - 1; i >= 0; i--)
            {
                Add(new FoldingRange(regions[i], lastContentLine, FoldingKind.Region));
            }

            regions.Clear();
        }

        private void CloseIndents()
        {
            if (lastContentLine < 0)
            {
                return;
            }

            for (var i = indents.Count - 1; i >= 0; i--)
            {
                Add(new FoldingRange(indents[i].Line, lastContentLine));
            }

            indents.Clear();
        }

        private void Add(FoldingRange range)
        {
            if (range.IsValid && range.EndLine < text.LineCount)
            {
                ranges.Add(range);
            }
        }

        private IReadOnlyList<FoldingRange> Normalize()
        {
            if (ranges.Count == 0)
            {
                return [];
            }

            ranges.Sort(static (a, b) => a.StartLine != b.StartLine
                ? a.StartLine.CompareTo(b.StartLine)
                : b.EndLine.CompareTo(a.EndLine));

            var kept = new List<FoldingRange>(Math.Min(ranges.Count, MaxRanges));
            var previousStart = -1;
            foreach (var range in ranges)
            {
                if (range.StartLine == previousStart || kept.Count >= MaxRanges)
                {
                    continue;
                }

                previousStart = range.StartLine;
                kept.Add(range);
            }

            return kept;
        }
    }

    /// <summary>Strips the comment and marker punctuation in front of a region keyword, so every dialect matches.</summary>
    private static ReadOnlySpan<char> TrimMarker(ReadOnlySpan<char> text)
    {
        while (text.Length > 0 && text[0] is '/' or '<' or '!' or '-' or '#' or '*' or '+' or ' ' or '\t')
        {
            text = text[1..];
        }

        return text;
    }

    private readonly record struct OpenItem(int Line, int Value);
}
