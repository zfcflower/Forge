using Forge.Editor.Text;

namespace Forge.Editor.Diff;

public enum DiffLineKind
{
    Equal,
    Insert,
    Delete,
}

/// <summary>One entry of the edit script; a line index is -1 when the line does not exist on that side.</summary>
public readonly record struct DiffLine(DiffLineKind Kind, int OldLine, int NewLine)
{
    public bool IsChange => Kind != DiffLineKind.Equal;
}

public sealed record DiffOptions
{
    public int ContextLines { get; init; } = 3;

    public bool IgnoreWhitespace { get; init; }

    public bool IgnoreCase { get; init; }

    /// <summary>Compare lines without their terminators, so <c>"a\r\n"</c> and <c>"a\n"</c> are equal.</summary>
    public bool IgnoreLineEndings { get; init; }
}

/// <summary>A contiguous run of changes plus surrounding context.</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines)
{
    public int OldEnd => OldStart + OldCount;

    public int NewEnd => NewStart + NewCount;

    public bool HasChanges => Lines.Any(static line => line.IsChange);

    public int OldLineAt(int index) => Lines[index].OldLine;

    public int NewLineAt(int index) => Lines[index].NewLine;
}

public sealed record DiffResult(
    IReadOnlyList<DiffLine> Lines,
    IReadOnlyList<DiffHunk> Hunks,
    int EqualCount,
    int InsertedCount,
    int DeletedCount)
{
    public static DiffResult Empty { get; } = new([], [], 0, 0, 0);

    public bool IsEmpty => InsertedCount == 0 && DeletedCount == 0;

    public int ChangedCount => InsertedCount + DeletedCount;
}

/// <summary>
/// Line diff over snapshots. Common prefixes and suffixes are trimmed first, then the remaining region is aligned with
/// unique-line anchors (patience) recursing between them; regions without anchors use an exact LCS when small and a
/// replace block when not, so the cost stays bounded on pathological input. Lines are compared by 64-bit content hash.
/// </summary>
public static class LineDiff
{
    private const int DpCellLimit = 2500;

    public static DiffResult Compute(TextSnapshot oldText, TextSnapshot newText, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var settings = options ?? new DiffOptions();
        return settings.IgnoreLineEndings
            ? Compute(oldText.GetLineTexts(), newText.GetLineTexts(), settings)
            : Compute(oldText.GetLineTextsWithBreak(), newText.GetLineTextsWithBreak(), settings);
    }

    public static DiffResult Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        var settings = options ?? new DiffOptions();
        var oldHashes = HashLines(oldLines, settings);
        var newHashes = HashLines(newLines, settings);
        var script = new List<DiffLine>(Math.Max(oldLines.Count, newLines.Count) + 16);
        Align(oldHashes, newHashes, script);

        var equal = 0;
        var inserted = 0;
        var deleted = 0;
        foreach (var line in script)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Equal:
                    equal++;
                    break;
                case DiffLineKind.Insert:
                    inserted++;
                    break;
                case DiffLineKind.Delete:
                    deleted++;
                    break;
            }
        }

        return new DiffResult(script, BuildHunks(script, Math.Max(0, settings.ContextLines)), equal, inserted, deleted);
    }

    /// <summary>Replacement pairing of one hunk: deleted line runs aligned with the inserted run that follows them.</summary>
    public static IReadOnlyList<(int OldLine, int NewLine)> PairReplacements(IReadOnlyList<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var pairs = new List<(int, int)>();
        var index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Kind != DiffLineKind.Delete)
            {
                index++;
                continue;
            }

            var deletes = new List<int>();
            while (index < lines.Count && lines[index].Kind == DiffLineKind.Delete)
            {
                deletes.Add(lines[index].OldLine);
                index++;
            }

            var inserts = new List<int>();
            while (index < lines.Count && lines[index].Kind == DiffLineKind.Insert)
            {
                inserts.Add(lines[index].NewLine);
                index++;
            }

            for (var i = 0; i < deletes.Count && i < inserts.Count; i++)
            {
                pairs.Add((deletes[i], inserts[i]));
            }
        }

        return pairs;
    }

    private static ulong[] HashLines(IReadOnlyList<string> lines, DiffOptions options)
    {
        var hashes = new ulong[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i] ?? string.Empty;
            if (options.IgnoreWhitespace)
            {
                text = CollapseWhitespace(text);
            }

            if (options.IgnoreCase)
            {
                hashes[i] = Fnv(text.ToLowerInvariant());
            }
            else
            {
                hashes[i] = Fnv(text);
            }
        }

        return hashes;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static ulong Fnv(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    private static void Align(ulong[] a, ulong[] b, List<DiffLine> output)
    {
        var stack = new Stack<WorkItem>();
        stack.Push(WorkItem.Segment(0, a.Length, 0, b.Length));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.Kind == WorkItem.EqualRunKind)
            {
                for (var i = 0; i < item.AHi; i++)
                {
                    output.Add(new DiffLine(DiffLineKind.Equal, item.ALo + i, item.BLo + i));
                }

                continue;
            }

            var aLo = item.ALo;
            var aHi = item.AHi;
            var bLo = item.BLo;
            var bHi = item.BHi;

            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
            {
                output.Add(new DiffLine(DiffLineKind.Equal, aLo, bLo));
                aLo++;
                bLo++;
            }

            var suffix = 0;
            while (aHi - suffix > aLo && bHi - suffix > bLo && a[aHi - 1 - suffix] == b[bHi - 1 - suffix])
            {
                suffix++;
            }

            aHi -= suffix;
            bHi -= suffix;

            if (aLo >= aHi || bLo >= bHi)
            {
                for (; aLo < aHi; aLo++)
                {
                    output.Add(new DiffLine(DiffLineKind.Delete, aLo, -1));
                }

                for (; bLo < bHi; bLo++)
                {
                    output.Add(new DiffLine(DiffLineKind.Insert, -1, bLo));
                }

                PushEqualRun(output, aHi, bHi, suffix);
                continue;
            }

            var anchors = FindAnchors(a, b, aLo, aHi, bLo, bHi);
            if (anchors.Count == 0)
            {
                if ((long)(aHi - aLo) * (bHi - bLo) <= DpCellLimit)
                {
                    EmitExact(a, b, aLo, aHi, bLo, bHi, output);
                }
                else
                {
                    for (var i = aLo; i < aHi; i++)
                    {
                        output.Add(new DiffLine(DiffLineKind.Delete, i, -1));
                    }

                    for (var j = bLo; j < bHi; j++)
                    {
                        output.Add(new DiffLine(DiffLineKind.Insert, -1, j));
                    }
                }

                PushEqualRun(output, aHi, bHi, suffix);
                continue;
            }

            stack.Push(WorkItem.EqualRun(aHi, bHi, suffix));
            stack.Push(WorkItem.Segment(anchors[^1].A + 1, aHi, anchors[^1].B + 1, bHi));
            for (var k = anchors.Count - 1; k >= 0; k--)
            {
                var (anchorA, anchorB) = anchors[k];
                stack.Push(WorkItem.EqualRun(anchorA, anchorB, 1));
                stack.Push(k == 0
                    ? WorkItem.Segment(aLo, anchorA, bLo, anchorB)
                    : WorkItem.Segment(anchors[k - 1].A + 1, anchorA, anchors[k - 1].B + 1, anchorB));
            }
        }
    }

    private static void PushEqualRun(List<DiffLine> output, int aStart, int bStart, int count)
    {
        for (var i = 0; i < count; i++)
        {
            output.Add(new DiffLine(DiffLineKind.Equal, aStart + i, bStart + i));
        }
    }

    private static List<(int A, int B)> FindAnchors(ulong[] a, ulong[] b, int aLo, int aHi, int bLo, int bHi)
    {
        var countA = new Dictionary<ulong, int>(aHi - aLo);
        for (var i = aLo; i < aHi; i++)
        {
            countA[a[i]] = countA.TryGetValue(a[i], out var existing) ? existing + 1 : 1;
        }

        var countB = new Dictionary<ulong, int>(bHi - bLo);
        for (var j = bLo; j < bHi; j++)
        {
            countB[b[j]] = countB.TryGetValue(b[j], out var existing) ? existing + 1 : 1;
        }

        var indexA = new Dictionary<ulong, int>(aHi - aLo);
        for (var i = aLo; i < aHi; i++)
        {
            if (countA[a[i]] == 1)
            {
                indexA[a[i]] = i;
            }
        }

        var candidates = new List<(int A, int B)>();
        for (var j = bLo; j < bHi; j++)
        {
            if (countB[b[j]] == 1 && indexA.TryGetValue(b[j], out var i))
            {
                candidates.Add((i, j));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        return LongestIncreasing(candidates);
    }

    private static List<(int A, int B)> LongestIncreasing(List<(int A, int B)> candidates)
    {
        var tails = new int[candidates.Count];
        var previous = new int[candidates.Count];
        var length = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var value = candidates[i].A;
            var low = 0;
            var high = length;
            while (low < high)
            {
                var mid = (low + high) >> 1;
                if (candidates[tails[mid]].A < value)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;
            tails[low] = i;
            if (low == length)
            {
                length++;
            }
        }

        var result = new List<(int A, int B)>(length);
        for (var index = length > 0 ? tails[length - 1] : -1; index >= 0; index = previous[index])
        {
            result.Add(candidates[index]);
        }

        result.Reverse();
        return result;
    }

    private static void EmitExact(ulong[] a, ulong[] b, int aLo, int aHi, int bLo, int bHi, List<DiffLine> output)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        var width = m + 1;
        var lcs = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[(i * width) + j] = a[aLo + i] == b[bLo + j]
                    ? lcs[((i + 1) * width) + j + 1] + 1
                    : Math.Max(lcs[((i + 1) * width) + j], lcs[(i * width) + j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[aLo + x] == b[bLo + y])
            {
                output.Add(new DiffLine(DiffLineKind.Equal, aLo + x, bLo + y));
                x++;
                y++;
            }
            else if (lcs[((x + 1) * width) + y] >= lcs[(x * width) + y + 1])
            {
                output.Add(new DiffLine(DiffLineKind.Delete, aLo + x, -1));
                x++;
            }
            else
            {
                output.Add(new DiffLine(DiffLineKind.Insert, -1, bLo + y));
                y++;
            }
        }

        while (x < n)
        {
            output.Add(new DiffLine(DiffLineKind.Delete, aLo + x++, -1));
        }

        while (y < m)
        {
            output.Add(new DiffLine(DiffLineKind.Insert, -1, bLo + y++));
        }
    }

    private static IReadOnlyList<DiffHunk> BuildHunks(List<DiffLine> lines, int context)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var hunks = new List<DiffHunk>();
        var index = 0;
        while (index < lines.Count)
        {
            if (!lines[index].IsChange)
            {
                index++;
                continue;
            }

            var lastChange = index;
            var cursor = index;
            while (cursor < lines.Count)
            {
                if (lines[cursor].IsChange)
                {
                    lastChange = cursor;
                    cursor++;
                    continue;
                }

                var next = cursor;
                while (next < lines.Count && !lines[next].IsChange)
                {
                    next++;
                }

                if (next >= lines.Count || next - cursor > context * 2)
                {
                    break;
                }

                cursor = next;
            }

            var start = Math.Max(0, index - context);
            var end = Math.Min(lines.Count, lastChange + 1 + context);
            hunks.Add(CreateHunk(lines, start, end));
            index = end;
        }

        return hunks;
    }

    private static DiffHunk CreateHunk(List<DiffLine> lines, int start, int end)
    {
        var slice = new DiffLine[end - start];
        lines.CopyTo(start, slice, 0, slice.Length);

        var oldStart = -1;
        var newStart = -1;
        int oldCount = 0, newCount = 0;
        foreach (var line in slice)
        {
            if (line.OldLine >= 0)
            {
                if (oldStart < 0)
                {
                    oldStart = line.OldLine;
                }

                oldCount++;
            }

            if (line.NewLine >= 0)
            {
                if (newStart < 0)
                {
                    newStart = line.NewLine;
                }

                newCount++;
            }
        }

        if (oldStart < 0)
        {
            oldStart = PreviousOldLine(lines, start);
        }

        if (newStart < 0)
        {
            newStart = PreviousNewLine(lines, start);
        }

        return new DiffHunk(oldStart, oldCount, newStart, newCount, slice);
    }

    private static int PreviousOldLine(List<DiffLine> lines, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (lines[i].OldLine >= 0)
            {
                return lines[i].OldLine + 1;
            }
        }

        return 0;
    }

    private static int PreviousNewLine(List<DiffLine> lines, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (lines[i].NewLine >= 0)
            {
                return lines[i].NewLine + 1;
            }
        }

        return 0;
    }

    private readonly record struct WorkItem(int Kind, int ALo, int AHi, int BLo, int BHi)
    {
        public const int SegmentKind = 0;
        public const int EqualRunKind = 1;

        public static WorkItem Segment(int aLo, int aHi, int bLo, int bHi) => new(SegmentKind, aLo, aHi, bLo, bHi);

        public static WorkItem EqualRun(int aStart, int bStart, int count) => new(EqualRunKind, aStart, count, bStart, 0);
    }
}
