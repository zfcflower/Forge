using System.Text;
using Forge.Workspace;

namespace Forge.App.Changes;

internal enum DiffRowKind : byte
{
    FileHeader,
    BlockBar,
    Fold,
    Context,
    Added,
    Removed,
    Spacer,
    Embed,
}

/// <summary>
/// One document line of the diff editor. <see cref="Hunk"/>/<see cref="Line"/> locate the source <see cref="DiffLine"/>
/// (for syntax highlighting); <see cref="Count"/> is the hidden-line count of a fold; <see cref="Key"/> identifies an
/// expandable fold across rebuilds (its first hidden new-side line).
/// </summary>
internal readonly record struct DiffRow(
    DiffRowKind Kind,
    int File,
    int OldLine = 0,
    int NewLine = 0,
    int Block = -1,
    int Hunk = -1,
    int Line = -1,
    int Count = 0,
    int Key = -1,
    bool Expandable = false)
{
    /// <summary>The row is drawn by an overlay control and holds no text of its own.</summary>
    public bool IsPlaceholder => Kind is DiffRowKind.FileHeader or DiffRowKind.BlockBar or DiffRowKind.Fold or DiffRowKind.Embed;

    public bool IsCode => Kind is DiffRowKind.Context or DiffRowKind.Added or DiffRowKind.Removed;
}

/// <summary>A new-side line range of one file/hunk, as resolved from a span of document lines.</summary>
internal readonly record struct DiffRange(int File, int Hunk, int NewStart, int NewEnd);

/// <summary>Document line (1-based) to row mapping of one built document.</summary>
internal sealed class DiffRowMap
{
    private readonly DiffRow[] rows;
    private readonly int[] fileFirst;
    private readonly int[] fileLast;

    public DiffRowMap(DiffRow[] rows, int fileCount)
    {
        this.rows = rows;
        fileFirst = new int[fileCount];
        fileLast = new int[fileCount];
        Array.Fill(fileFirst, -1);
        Array.Fill(fileLast, -1);
        for (var i = 0; i < rows.Length; i++)
        {
            var file = rows[i].File;
            if (fileFirst[file] < 0)
            {
                fileFirst[file] = i;
            }

            fileLast[file] = i;
        }
    }

    private DiffRowMap(DiffRow[] rows, int[] fileFirst, int[] fileLast)
    {
        this.rows = rows;
        this.fileFirst = fileFirst;
        this.fileLast = fileLast;
    }

    public static DiffRowMap Empty { get; } = new([], 0);

    /// <summary>
    /// A copy of this map in which the rows of <paramref name="file"/> are replaced by <paramref name="replacement"/> (at least
    /// one row: the header). Later files shift; nothing is rescanned, so the cost is a block copy of the rows plus O(files).
    /// </summary>
    public DiffRowMap WithFile(int file, DiffRow[] replacement)
    {
        if ((uint)file >= (uint)fileFirst.Length || fileFirst[file] < 0 || replacement.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(file));
        }

        var first = fileFirst[file];
        var oldCount = fileLast[file] - first + 1;
        var delta = replacement.Length - oldCount;
        var next = new DiffRow[rows.Length + delta];
        Array.Copy(rows, 0, next, 0, first);
        Array.Copy(replacement, 0, next, first, replacement.Length);
        Array.Copy(rows, first + oldCount, next, first + replacement.Length, rows.Length - first - oldCount);
        var newFirst = (int[])fileFirst.Clone();
        var newLast = (int[])fileLast.Clone();
        newLast[file] = first + replacement.Length - 1;
        for (var f = file + 1; f < newFirst.Length; f++)
        {
            if (newFirst[f] >= 0)
            {
                newFirst[f] += delta;
                newLast[f] += delta;
            }
        }

        return new DiffRowMap(next, newFirst, newLast);
    }

    public int Count => rows.Length;

    public int FileCount => fileFirst.Length;

    public DiffRow this[int index] => rows[index];

    /// <summary>The row of 1-based document line <paramref name="docLine"/>.</summary>
    public DiffRow ForLine(int docLine) => rows[docLine - 1];

    public bool Contains(int docLine) => docLine >= 1 && docLine <= rows.Length;

    /// <summary>1-based document line of the file's header row, or 0 when the file has no rows.</summary>
    public int HeaderLine(int file) => (uint)file < (uint)fileFirst.Length ? fileFirst[file] + 1 : 0;

    public int FileLastLine(int file) => (uint)file < (uint)fileLast.Length ? fileLast[file] + 1 : 0;

    /// <summary>The file owning 1-based document line <paramref name="docLine"/>, or -1.</summary>
    public int FileAt(int docLine) => Contains(docLine) ? rows[docLine - 1].File : -1;

    /// <summary>Finds the document line that shows new-side line <paramref name="newLine"/> of <paramref name="file"/>.</summary>
    public bool TryFindNewLine(int file, int newLine, out int docLine)
    {
        docLine = 0;
        if ((uint)file >= (uint)fileFirst.Length || fileFirst[file] < 0)
        {
            return false;
        }

        for (var i = fileFirst[file]; i <= fileLast[file]; i++)
        {
            ref readonly var row = ref rows[i];
            if (row.NewLine == newLine && row.Kind is DiffRowKind.Context or DiffRowKind.Added)
            {
                docLine = i + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps document lines to the new-side range they cover. Fails when the span touches more than one file or hunk, or
    /// holds no new-side line (only removed lines, spacers or placeholders).
    /// </summary>
    public bool TryMapRange(int startLine, int endLine, out DiffRange range)
    {
        range = default;
        if (startLine < 1 || endLine < startLine || endLine > rows.Length)
        {
            return false;
        }

        var file = rows[startLine - 1].File;
        var hunk = -2;
        int min = int.MaxValue, max = 0;
        for (var i = startLine - 1; i < endLine; i++)
        {
            ref readonly var row = ref rows[i];
            if (row.File != file)
            {
                return false;
            }

            if (row.Kind is not (DiffRowKind.Context or DiffRowKind.Added) || row.NewLine <= 0)
            {
                continue;
            }

            if (hunk == -2)
            {
                hunk = row.Hunk;
            }
            else if (hunk != row.Hunk)
            {
                return false;
            }

            min = Math.Min(min, row.NewLine);
            max = Math.Max(max, row.NewLine);
        }

        if (max == 0)
        {
            return false;
        }

        range = new DiffRange(file, hunk, min, max);
        return true;
    }
}

/// <summary>The rows and text (LF separated, no trailing newline) of one file on one side.</summary>
internal sealed record DiffFileChunk(DiffRow[] Rows, string Text);

/// <summary>One built document: its text (one row per line, LF separated) and the row map.</summary>
internal sealed record DiffDocumentModel(string Text, DiffRowMap Map);

/// <summary>Inline: only <see cref="Primary"/>. Split: <see cref="Primary"/> is the old side, <see cref="Secondary"/> the new side.</summary>
internal sealed record DiffBuildResult(DiffDocumentModel Primary, DiffDocumentModel? Secondary);

/// <summary>What the builder needs to know about one file; independent of the view model so it can be tested alone.</summary>
internal sealed record DiffFileSpec(
    FileDiff Diff,
    bool Collapsed,
    bool MarkdownPreview,
    bool FullContext,
    IReadOnlySet<int>? ExpandedFolds = null,
    bool CanLoadContext = false);

/// <summary>Turns file diffs into the placeholder-laden documents the diff editor shows. Pure; no UI types.</summary>
internal static class DiffDocumentBuilder
{
    /// <summary>Unchanged lines kept visible next to a change.</summary>
    public const int ContextKeep = 3;

    /// <summary>A run is folded only when at least this many lines would hide.</summary>
    public const int MinFold = 2;

    /// <summary>Text of a placeholder line: one character the placeholder element replaces.</summary>
    public const string PlaceholderText = " ";

    public static DiffBuildResult Build(IReadOnlyList<DiffFileSpec> files, bool split)
    {
        ArgumentNullException.ThrowIfNull(files);
        var left = new Side();
        var right = split ? new Side() : null;
        for (var f = 0; f < files.Count; f++)
        {
            BuildFile(files[f], f, left, right);
        }

        return new DiffBuildResult(left.Finish(files.Count), right?.Finish(files.Count));
    }

    /// <summary>Builds the rows of a single file on its own, for replacing that file's range in an existing document.</summary>
    public static (DiffFileChunk Left, DiffFileChunk? Right) BuildOne(DiffFileSpec spec, int file, bool split)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var left = new Side();
        var right = split ? new Side() : null;
        BuildFile(spec, file, left, right);
        return (left.Chunk(), right?.Chunk());
    }

    /// <summary>
    /// Keys of every fold the diff could show (folded or not), so expanded-fold state can be carried to a new diff. A fold's
    /// key is the new-side number of its first hidden line, so the bar for the unloaded stretch between two 3-line-context
    /// hunks and the fold of the same stretch in the whole-file diff share a key.
    /// </summary>
    public static HashSet<int> FoldKeys(FileDiff diff)
    {
        var keys = new HashSet<int>();
        var prevLast = 0;
        foreach (var hunk in diff.Hunks)
        {
            var first = hunk.NewCount == 0 ? hunk.NewStart + 1 : hunk.NewStart;
            if (first - prevLast - 1 > 0)
            {
                keys.Add(prevLast + 1);
            }

            prevLast = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewEnd;

            var lines = hunk.Lines;
            var seenChange = false;
            var i = 0;
            while (i < lines.Count)
            {
                var start = i;
                if (lines[i].Kind != DiffLineKind.Context)
                {
                    seenChange = true;
                    while (i < lines.Count && lines[i].Kind != DiffLineKind.Context)
                    {
                        i++;
                    }

                    continue;
                }

                while (i < lines.Count && lines[i].Kind == DiffLineKind.Context)
                {
                    i++;
                }

                var head = seenChange ? ContextKeep : 0;
                var tail = i < lines.Count ? ContextKeep : 0;
                if (i - start - head - tail >= MinFold)
                {
                    keys.Add(lines[start + head].NewLineNumber);
                }
            }
        }

        return keys;
    }

    private sealed class Side
    {
        private readonly List<DiffRow> rows = [];
        private readonly StringBuilder text = new();

        public void Add(in DiffRow row, string content)
        {
            if (rows.Count > 0)
            {
                text.Append('\n');
            }

            rows.Add(row);
            text.Append(content);
        }

        public DiffFileChunk Chunk() => new([.. rows], text.ToString());

        public DiffDocumentModel Finish(int fileCount) => new(text.ToString(), new DiffRowMap([.. rows], fileCount));
    }

    private static void AddBoth(Side left, Side? right, in DiffRow row, string content)
    {
        left.Add(row, content);
        right?.Add(row, content);
    }

    private static string Clean(string text) =>
        text.AsSpan().IndexOfAny('\r', '\n') < 0 ? text : text.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static void BuildFile(DiffFileSpec spec, int file, Side left, Side? right)
    {
        AddBoth(left, right, new DiffRow(DiffRowKind.FileHeader, file), PlaceholderText);
        if (spec.Collapsed)
        {
            return;
        }

        if (spec.MarkdownPreview)
        {
            AddBoth(left, right, new DiffRow(DiffRowKind.Embed, file), PlaceholderText);
            return;
        }

        var diff = spec.Diff;
        var block = 0;
        var prevLast = 0;
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            var first = hunk.NewCount == 0 ? hunk.NewStart + 1 : hunk.NewStart;
            var gap = first - prevLast - 1;
            if (gap > 0)
            {
                // Lines git did not send: the bar opens by loading the whole file (same key as that stretch's fold there).
                AddBoth(
                    left,
                    right,
                    new DiffRow(DiffRowKind.Fold, file, Hunk: h, Count: gap, Key: prevLast + 1, Expandable: spec.CanLoadContext),
                    PlaceholderText);
            }

            prevLast = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewEnd;
            BuildHunk(spec, file, h, hunk, ref block, left, right);
        }
    }

    private static void BuildHunk(DiffFileSpec spec, int file, int h, DiffHunk hunk, ref int block, Side left, Side? right)
    {
        var lines = hunk.Lines;
        var i = 0;
        var seenChange = false;
        while (i < lines.Count)
        {
            var start = i;
            if (lines[i].Kind == DiffLineKind.Context)
            {
                while (i < lines.Count && lines[i].Kind == DiffLineKind.Context)
                {
                    i++;
                }

                EmitContext(spec, file, h, lines, start, i, seenChange, followedByChange: i < lines.Count, left, right);
                continue;
            }

            seenChange = true;
            var removed = new List<int>();
            var added = new List<int>();
            while (i < lines.Count && lines[i].Kind != DiffLineKind.Context)
            {
                (lines[i].Kind == DiffLineKind.Removed ? removed : added).Add(i);
                i++;
            }

            EmitBlock(file, h, block++, lines, removed, added, left, right);
        }
    }

    private static void EmitContext(
        DiffFileSpec spec, int file, int h, IReadOnlyList<DiffLine> lines, int start, int end,
        bool precededByChange, bool followedByChange, Side left, Side? right)
    {
        var head = precededByChange ? ContextKeep : 0;
        var tail = followedByChange ? ContextKeep : 0;
        var hidden = end - start - head - tail;
        var fold = hidden >= MinFold;
        var key = fold ? lines[start + head].NewLineNumber : -1;
        if (fold && spec.ExpandedFolds?.Contains(key) == true)
        {
            fold = false;
        }

        for (var k = start; k < end; k++)
        {
            if (fold && k == start + head)
            {
                AddBoth(left, right, new DiffRow(DiffRowKind.Fold, file, Hunk: h, Count: hidden, Key: key, Expandable: true), PlaceholderText);
                k = end - tail - 1;
                continue;
            }

            var line = lines[k];
            AddBoth(
                left,
                right,
                new DiffRow(DiffRowKind.Context, file, line.OldLineNumber, line.NewLineNumber, Hunk: h, Line: k),
                Clean(line.Text));
        }
    }

    private static void EmitBlock(
        int file, int h, int block, IReadOnlyList<DiffLine> lines, List<int> removed, List<int> added, Side left, Side? right)
    {
        AddBoth(left, right, new DiffRow(DiffRowKind.BlockBar, file, Block: block, Hunk: h), PlaceholderText);
        if (right is null)
        {
            foreach (var r in removed)
            {
                left.Add(RemovedRow(file, h, block, lines, r), Clean(lines[r].Text));
            }

            foreach (var a in added)
            {
                left.Add(AddedRow(file, h, block, lines, a), Clean(lines[a].Text));
            }

            return;
        }

        var rows = Math.Max(removed.Count, added.Count);
        for (var k = 0; k < rows; k++)
        {
            if (k < removed.Count)
            {
                left.Add(RemovedRow(file, h, block, lines, removed[k]), Clean(lines[removed[k]].Text));
            }
            else
            {
                left.Add(new DiffRow(DiffRowKind.Spacer, file, Block: block, Hunk: h), string.Empty);
            }

            if (k < added.Count)
            {
                right.Add(AddedRow(file, h, block, lines, added[k]), Clean(lines[added[k]].Text));
            }
            else
            {
                right.Add(new DiffRow(DiffRowKind.Spacer, file, Block: block, Hunk: h), string.Empty);
            }
        }
    }

    private static DiffRow RemovedRow(int file, int h, int block, IReadOnlyList<DiffLine> lines, int index) =>
        new(DiffRowKind.Removed, file, lines[index].OldLineNumber, 0, block, h, index);

    private static DiffRow AddedRow(int file, int h, int block, IReadOnlyList<DiffLine> lines, int index) =>
        new(DiffRowKind.Added, file, 0, lines[index].NewLineNumber, block, h, index);
}
