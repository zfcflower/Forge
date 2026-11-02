using System.Text;
using Forge.Editor.Text;

namespace Forge.Editor.Diff;

public enum CommentAnchorKind
{
    Line,
    Block,
}

/// <summary>
/// Review comment anchor: a line or line-block range of one file plus the hash of the fragment it was written against
/// (and per-line hashes plus a short preview, which is what lets re-anchoring find the fragment again after edits).
/// </summary>
public sealed record CommentAnchor(
    string FilePath,
    int StartLine,
    int EndLine,
    string ContentHash,
    CommentAnchorKind Kind = CommentAnchorKind.Block,
    IReadOnlyList<ulong>? LineHashes = null,
    string? Preview = null)
{
    public int LineCount => EndLine - StartLine + 1;

    public (int Start, int End) Range => (StartLine, EndLine);

    public static CommentAnchor Create(
        string filePath,
        TextSnapshot snapshot,
        int startLine,
        int endLine,
        CommentAnchorKind kind = CommentAnchorKind.Block,
        int previewChars = 2000)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (startLine > endLine)
        {
            (startLine, endLine) = (endLine, startLine);
        }

        startLine = Math.Clamp(startLine, 0, Math.Max(0, snapshot.LineCount - 1));
        endLine = Math.Clamp(endLine, startLine, Math.Max(0, snapshot.LineCount - 1));

        var lines = LineHashesOf(snapshot, startLine, endLine);
        var hash = Combine(lines);
        var preview = PreviewOf(snapshot, startLine, endLine, previewChars);
        var effectiveKind = kind == CommentAnchorKind.Line || startLine == endLine ? CommentAnchorKind.Line : CommentAnchorKind.Block;
        return new CommentAnchor(filePath, startLine, endLine, hash.ToString("x16"), effectiveKind, lines, preview);
    }

    /// <summary>Relocates this anchor in a modified text; see <see cref="CommentAnchorSearch.Reanchor"/>.</summary>
    public ReanchorResult Reanchor(TextSnapshot snapshot, ReanchorOptions? options = null) =>
        CommentAnchorSearch.Reanchor(this, snapshot, options);

    /// <summary>True when the fragment at the recorded lines is byte-identical to what the comment was written against.</summary>
    public bool Matches(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (StartLine < 0 || EndLine >= snapshot.LineCount)
        {
            return false;
        }

        return Combine(LineHashesOf(snapshot, StartLine, EndLine)) == ParseHash(ContentHash);
    }

    public static ulong HashLine(string text) => Rope.Fnv(text);

    public static ulong Combine(IReadOnlyList<ulong> lineHashes)
    {
        ArgumentNullException.ThrowIfNull(lineHashes);
        ulong hash = 14695981039346656037UL;
        hash = (hash ^ (ulong)lineHashes.Count) * 1099511628211UL;
        foreach (var line in lineHashes)
        {
            hash = (hash ^ line) * 1099511628211UL;
        }

        return hash;
    }

    public static IReadOnlyList<ulong> LineHashesOf(TextSnapshot snapshot, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var count = Math.Max(0, endLine - startLine + 1);
        var hashes = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            hashes[i] = HashLine(snapshot.GetLineText(startLine + i));
        }

        return hashes;
    }

    private static string PreviewOf(TextSnapshot snapshot, int startLine, int endLine, int limit)
    {
        var builder = new StringBuilder(Math.Min(limit, 256));
        for (var line = startLine; line <= endLine && builder.Length < limit; line++)
        {
            if (line > startLine)
            {
                builder.Append('\n');
            }

            builder.Append(snapshot.GetLineText(line));
        }

        return builder.Length > limit ? builder.ToString(0, limit) : builder.ToString();
    }

    private static ulong ParseHash(string hash) =>
        ulong.TryParse(hash, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0UL;
}

public enum ReanchorStatus
{
    /// <summary>The fragment is still at the recorded lines.</summary>
    Exact,

    /// <summary>The identical fragment was found at a different line.</summary>
    Moved,

    /// <summary>A similar fragment was found and accepted by the similarity threshold.</summary>
    Fuzzy,

    /// <summary>Nothing close enough was found; the original lines are clamped into the new text.</summary>
    Lost,
}

public sealed record ReanchorOptions
{
    /// <summary>Minimum similarity for a <see cref="ReanchorStatus.Fuzzy"/> result, between 0 and 1.</summary>
    public double FuzzyThreshold { get; init; } = 0.6;

    /// <summary>How many lines away a candidate may start and still be considered; 0 means no limit.</summary>
    public int MaxDistance { get; init; }
}

/// <summary>The relocated anchor plus how it was found; the anchor already carries the new lines and hashes.</summary>
public sealed record ReanchorResult(CommentAnchor Anchor, ReanchorStatus Status, double Similarity)
{
    public int StartLine => Anchor.StartLine;

    public int EndLine => Anchor.EndLine;

    public bool Found => Status != ReanchorStatus.Lost;
}

/// <summary>
/// Relocates a <see cref="CommentAnchor"/> in a modified text: identical content first (same lines, then anywhere),
/// then the most similar window above a threshold, preferring windows near the original position.
/// </summary>
public static class CommentAnchorSearch
{
    public static ReanchorResult Reanchor(CommentAnchor anchor, TextSnapshot snapshot, ReanchorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = options ?? new ReanchorOptions();
        var lineCount = Math.Max(0, snapshot.LineCount - 1);
        var window = Math.Max(1, anchor.LineCount);

        if (anchor.StartLine >= 0 && anchor.EndLine <= lineCount && anchor.Matches(snapshot))
        {
            return new ReanchorResult(anchor with { LineHashes = CommentAnchor.LineHashesOf(snapshot, anchor.StartLine, anchor.EndLine) }, ReanchorStatus.Exact, 1.0);
        }

        var bestExact = -1;
        var bestExactDistance = int.MaxValue;
        var bestScore = 0.0;
        var bestLine = -1;
        var bestWindow = window;

        for (var size = Math.Max(1, window - 1); size <= Math.Min(window + 1, lineCount + 1); size++)
        {
            for (var start = 0; start + size - 1 <= lineCount; start++)
            {
                var distance = Math.Abs(start - anchor.StartLine);
                if (settings.MaxDistance > 0 && distance > settings.MaxDistance)
                {
                    continue;
                }

                var hashes = CommentAnchor.LineHashesOf(snapshot, start, start + size - 1);
                if (size == window && CommentAnchor.Combine(hashes) == ParseHash(anchor.ContentHash))
                {
                    if (distance < bestExactDistance)
                    {
                        bestExactDistance = distance;
                        bestExact = start;
                    }

                    continue;
                }

                var score = Similarity(anchor, snapshot, start, size, hashes);
                if (score > bestScore || (Math.Abs(score - bestScore) < 0.0001 && bestLine >= 0 && distance < Math.Abs(bestLine - anchor.StartLine)))
                {
                    bestScore = score;
                    bestLine = start;
                    bestWindow = size;
                }
            }

            if (bestExact >= 0)
            {
                break;
            }
        }

        if (bestExact >= 0)
        {
            var relocated = anchor with
            {
                StartLine = bestExact,
                EndLine = bestExact + window - 1,
                LineHashes = CommentAnchor.LineHashesOf(snapshot, bestExact, bestExact + window - 1),
                Preview = Preview(snapshot, bestExact, bestExact + window - 1, anchor.Preview?.Length ?? 2000),
            };
            return new ReanchorResult(relocated, ReanchorStatus.Moved, 1.0);
        }

        if (bestLine >= 0 && bestScore >= settings.FuzzyThreshold)
        {
            var relocated = anchor with
            {
                StartLine = bestLine,
                EndLine = bestLine + bestWindow - 1,
                ContentHash = CommentAnchor.Combine(CommentAnchor.LineHashesOf(snapshot, bestLine, bestLine + bestWindow - 1)).ToString("x16"),
                LineHashes = CommentAnchor.LineHashesOf(snapshot, bestLine, bestLine + bestWindow - 1),
                Preview = Preview(snapshot, bestLine, bestLine + bestWindow - 1, anchor.Preview?.Length ?? 2000),
            };
            return new ReanchorResult(relocated, ReanchorStatus.Fuzzy, bestScore);
        }

        var startLine = Math.Clamp(anchor.StartLine, 0, lineCount);
        var endLine = Math.Clamp(startLine + window - 1, startLine, lineCount);
        return new ReanchorResult(anchor with { StartLine = startLine, EndLine = endLine }, ReanchorStatus.Lost, bestScore);
    }

    private static double Similarity(CommentAnchor anchor, TextSnapshot snapshot, int start, int size, IReadOnlyList<ulong> hashes)
    {
        var reference = anchor.LineHashes;
        double lineScore;
        if (reference is { Count: > 0 })
        {
            var remaining = new Dictionary<ulong, int>(reference.Count);
            foreach (var hash in reference)
            {
                remaining[hash] = remaining.TryGetValue(hash, out var count) ? count + 1 : 1;
            }

            var matches = 0;
            foreach (var hash in hashes)
            {
                if (remaining.TryGetValue(hash, out var count) && count > 0)
                {
                    remaining[hash] = count - 1;
                    matches++;
                }
            }

            lineScore = (double)matches / Math.Max(reference.Count, size);
        }
        else
        {
            lineScore = 0;
        }

        if (string.IsNullOrEmpty(anchor.Preview))
        {
            return lineScore;
        }

        var text = Preview(snapshot, start, start + size - 1, anchor.Preview.Length + 64);
        return (lineScore + Dice(anchor.Preview, text)) / 2.0;
    }

    private static string Preview(TextSnapshot snapshot, int start, int end, int limit)
    {
        var builder = new StringBuilder(Math.Min(Math.Max(limit, 16), 256));
        for (var line = start; line <= end && builder.Length < limit; line++)
        {
            if (line > start)
            {
                builder.Append('\n');
            }

            builder.Append(snapshot.GetLineText(line));
        }

        return builder.Length > limit ? builder.ToString(0, limit) : builder.ToString();
    }

    /// <summary>Sørensen-Dice coefficient over character bigrams.</summary>
    private static double Dice(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 1.0;
        }

        if (left.Length < 2 || right.Length < 2)
        {
            return string.Equals(left, right, StringComparison.Ordinal) ? 1.0 : 0.0;
        }

        var counts = new Dictionary<int, int>(left.Length);
        for (var i = 0; i < left.Length - 1; i++)
        {
            var bigram = (left[i] << 16) | left[i + 1];
            counts[bigram] = counts.TryGetValue(bigram, out var count) ? count + 1 : 1;
        }

        var matches = 0;
        var total = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var bigram = (right[i] << 16) | right[i + 1];
            total++;
            if (counts.TryGetValue(bigram, out var count) && count > 0)
            {
                counts[bigram] = count - 1;
                matches++;
            }
        }

        var leftTotal = left.Length - 1;
        return 2.0 * matches / (leftTotal + total);
    }

    private static ulong ParseHash(string hash) =>
        ulong.TryParse(hash, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0UL;
}
