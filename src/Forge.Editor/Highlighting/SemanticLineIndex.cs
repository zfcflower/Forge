// Governed by THIRD_PARTY_NOTICES.md.

using System.Buffers;

namespace Forge.Editor.Highlighting;

/// <summary>
/// The semantic tokens of one document, bucketed per line: what a view asks for while drawing a visible line, instead of
/// scanning the whole token set per line. One instance is immutable, so the UI thread reads it while the next one is built
/// off it; the offsets are line-local, in the coordinates the document version it was built against uses.
/// <para>The bucketing reuses the previous index's arrays for every line whose tokens did not change, so an update after an
/// edit allocates only for the lines the server reported differently — not for the whole document.</para>
/// </summary>
public sealed class SemanticLineIndex
{
    private static readonly SemanticToken[] NoTokens = [];

    private readonly SemanticToken[][] _lines;
    private readonly int _version;
    private readonly int _lineCount;
    private readonly int _tokenCount;

    private SemanticLineIndex(
        SemanticToken[][] lines,
        int lineCount,
        int version,
        int tokenCount,
        int builtLines,
        int reusedLines,
        int droppedTokens)
    {
        _lines = lines;
        _version = version;
        _lineCount = lineCount;
        _tokenCount = tokenCount;
        BuiltLines = builtLines;
        ReusedLines = reusedLines;
        DroppedTokens = droppedTokens;
    }

    /// <summary>The index of a document with no semantic tokens; its version never matches a document.</summary>
    public static SemanticLineIndex Empty { get; } = new([], 0, -1, 0, 0, 0, 0);

    /// <summary>The document version the offsets were mapped against; a view paints nothing when it no longer matches.</summary>
    public int Version => _version;

    /// <summary>The document line count this index was built for; every line past the last classified one is empty.</summary>
    public int LineCount => _lineCount;

    public int TokenCount => _tokenCount;

    /// <summary>Lines whose token array was allocated by this build (the affected ones).</summary>
    public int BuiltLines { get; }

    /// <summary>Lines that kept the previous build's array because their tokens did not change.</summary>
    public int ReusedLines { get; }

    /// <summary>Tokens the server sent at a line the document does not have, or with a non-positive length.</summary>
    public int DroppedTokens { get; }

    public bool IsEmpty => _tokenCount == 0;

    /// <summary>The tokens of one line, sorted by start; empty for a line outside the document or without tokens.</summary>
    public ReadOnlySpan<SemanticToken> GetLine(int line) => (uint)line < (uint)_lines.Length ? _lines[line] : NoTokens;

    /// <summary>
    /// Buckets line coordinates into a per-line index. <paramref name="lineNumbers"/> is parallel to
    /// <paramref name="tokens"/> and non-decreasing, as LSP decodes deltas into; anything else is sorted first.
    /// <paramref name="previous"/> is the index this one replaces and supplies the arrays of unchanged lines.
    /// </summary>
    public static SemanticLineIndex Build(
        ReadOnlySpan<int> lineNumbers,
        ReadOnlySpan<SemanticToken> tokens,
        int lineCount,
        int version,
        SemanticLineIndex? previous = null)
    {
        if (lineNumbers.Length != tokens.Length)
        {
            throw new ArgumentException("Every token needs one line number.", nameof(tokens));
        }

        if (lineCount <= 0 || tokens.IsEmpty)
        {
            return new SemanticLineIndex([], lineCount, version, 0, 0, 0, lineNumbers.Length);
        }

        var ordered = true;
        for (var i = 1; i < lineNumbers.Length; i++)
        {
            if (lineNumbers[i] < lineNumbers[i - 1])
            {
                ordered = false;
                break;
            }
        }

        if (!ordered)
        {
            var sorted = SortByLine(lineNumbers, tokens);
            return Bucket(sorted.Lines, sorted.Tokens, lineCount, version, previous);
        }

        return Bucket(lineNumbers, tokens, lineCount, version, previous);
    }

    private static SemanticLineIndex Bucket(
        ReadOnlySpan<int> lineNumbers,
        ReadOnlySpan<SemanticToken> tokens,
        int lineCount,
        int version,
        SemanticLineIndex? previous)
    {
        var counts = ArrayPool<int>.Shared.Rent(lineCount);
        try
        {
            var counted = counts.AsSpan(0, lineCount);
            counted.Clear();
            var dropped = 0;
            var extent = 0;
            for (var i = 0; i < tokens.Length; i++)
            {
                if ((uint)lineNumbers[i] >= (uint)lineCount || tokens[i].Length <= 0)
                {
                    dropped++;
                    continue;
                }

                counts[lineNumbers[i]]++;
                if (lineNumbers[i] + 1 > extent)
                {
                    extent = lineNumbers[i] + 1;
                }
            }

            // Only up to the last line the server classified: a server that covers the head of a long file should not cost
            // one array slot per unclassified line below it (a line past the extent has no tokens by definition).
            var lines = new SemanticToken[extent][];
            var built = 0;
            var reused = 0;
            var kept = 0;
            var source = 0;
            while (source < tokens.Length)
            {
                var line = lineNumbers[source];
                var end = source + 1;
                while (end < tokens.Length && lineNumbers[end] == line)
                {
                    end++;
                }

                var count = (uint)line < (uint)lineCount ? counts[line] : 0;
                if (count > 0)
                {
                    counts[line] = 0;
                    if (previous is not null && SameTokens(previous.GetLine(line), tokens, source, end))
                    {
                        lines[line] = previous.GetLineArray(line);
                        reused++;
                    }
                    else
                    {
                        lines[line] = Materialize(tokens[source..end], count);
                        built++;
                    }

                    kept += lines[line].Length;
                }

                source = end;
            }

            return new SemanticLineIndex(lines, lineCount, version, kept, built, reused, dropped);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    private static SemanticToken[] Materialize(ReadOnlySpan<SemanticToken> run, int count)
    {
        var result = new SemanticToken[count];
        var index = 0;
        var sorted = true;
        foreach (var token in run)
        {
            if (token.Length <= 0)
            {
                continue;
            }

            if (index > 0 && result[index - 1].Start > token.Start)
            {
                sorted = false;
            }

            result[index++] = token;
        }

        if (!sorted)
        {
            // Same-line deltas arrive ascending; a server that sends them otherwise still gets a sorted line, because the
            // merger walks the tokens once and needs ascending starts.
            Array.Sort(result, static (left, right) => left.Start.CompareTo(right.Start));
        }

        return result;
    }

    private static bool SameTokens(ReadOnlySpan<SemanticToken> existing, ReadOnlySpan<SemanticToken> tokens, int start, int end)
    {
        var index = 0;
        for (var i = start; i < end; i++)
        {
            if (tokens[i].Length <= 0)
            {
                continue;
            }

            if (index >= existing.Length || existing[index] != tokens[i])
            {
                return false;
            }

            index++;
        }

        return index == existing.Length;
    }

    private static (int[] Lines, SemanticToken[] Tokens) SortByLine(ReadOnlySpan<int> lineNumbers, ReadOnlySpan<SemanticToken> tokens)
    {
        var pairs = new (int Line, SemanticToken Token)[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            pairs[i] = (lineNumbers[i], tokens[i]);
        }

        Array.Sort(pairs, static (left, right) => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Token.Start.CompareTo(right.Token.Start));

        var sortedTokens = new SemanticToken[pairs.Length];
        var sortedLines = new int[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            sortedLines[i] = pairs[i].Line;
            sortedTokens[i] = pairs[i].Token;
        }

        return (sortedLines, sortedTokens);
    }

    private SemanticToken[] GetLineArray(int line) => (uint)line < (uint)_lines.Length ? _lines[line] ?? NoTokens : NoTokens;
}
