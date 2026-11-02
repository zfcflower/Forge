using Forge.Editor.Text;

namespace Forge.Editor.Diff;

public enum DiffRowKind
{
    Equal,
    Modified,
    Added,
    Removed,
}

/// <summary>One side-by-side row; a line index is -1 when that side has no line.</summary>
public readonly record struct DiffRow(DiffRowKind Kind, int OldLine, int NewLine)
{
    public bool IsChange => Kind != DiffRowKind.Equal;
}

public sealed record DiffStats(int EqualLines, int InsertedLines, int DeletedLines, int Hunks)
{
    public bool IsEmpty => InsertedLines == 0 && DeletedLines == 0;

    public int ChangedLines => InsertedLines + DeletedLines;
}

/// <summary>
/// Diff of two snapshots prepared for rendering: the inline edit script plus side-by-side rows where a run of deleted
/// lines is paired with the inserted run that replaces it. Text is fetched from the snapshots on demand, so the model
/// never copies the documents.
/// </summary>
public sealed class DiffModel
{
    public DiffModel(TextSnapshot oldText, TextSnapshot newText, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        Old = oldText;
        New = newText;
        Diff = LineDiff.Compute(oldText, newText, options);
        Rows = BuildRows(Diff.Lines);
    }

    public TextSnapshot Old { get; }

    public TextSnapshot New { get; }

    public DiffResult Diff { get; }

    public IReadOnlyList<DiffRow> Rows { get; }

    public IReadOnlyList<DiffHunk> Hunks => Diff.Hunks;

    public IReadOnlyList<DiffLine> Inline => Diff.Lines;

    public int RowCount => Rows.Count;

    public DiffStats Stats => new(Diff.EqualCount, Diff.InsertedCount, Diff.DeletedCount, Diff.Hunks.Count);

    public bool IsEmpty => Diff.IsEmpty;

    public DiffRow this[int index] => Rows[index];

    public string GetOldLineText(int line) => line >= 0 && line < Old.LineCount ? Old.GetLineText(line) : string.Empty;

    public string GetNewLineText(int line) => line >= 0 && line < New.LineCount ? New.GetLineText(line) : string.Empty;

    public string GetLeftText(DiffRow row) => GetOldLineText(row.OldLine);

    public string GetRightText(DiffRow row) => GetNewLineText(row.NewLine);

    /// <summary>The text a row shows when the consumer wants one column: the new side when present.</summary>
    public string GetText(DiffRow row) => row.NewLine >= 0 ? GetNewLineText(row.NewLine) : GetOldLineText(row.OldLine);

    /// <summary>Index of the first row that differs, or -1.</summary>
    public int FirstChangedRow => IndexOfFirstChange();

    public IEnumerable<(int OldLine, int NewLine)> Replacements(DiffHunk hunk) => LineDiff.PairReplacements(hunk.Lines);

    private int IndexOfFirstChange()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].IsChange)
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<DiffRow> BuildRows(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<DiffRow>(lines.Count);
        var index = 0;
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Kind == DiffLineKind.Equal)
            {
                rows.Add(new DiffRow(DiffRowKind.Equal, line.OldLine, line.NewLine));
                index++;
                continue;
            }

            var deletes = new List<int>(4);
            while (index < lines.Count && lines[index].Kind == DiffLineKind.Delete)
            {
                deletes.Add(lines[index].OldLine);
                index++;
            }

            var inserts = new List<int>(4);
            while (index < lines.Count && lines[index].Kind == DiffLineKind.Insert)
            {
                inserts.Add(lines[index].NewLine);
                index++;
            }

            var paired = Math.Min(deletes.Count, inserts.Count);
            for (var i = 0; i < paired; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Modified, deletes[i], inserts[i]));
            }

            for (var i = paired; i < deletes.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Removed, deletes[i], -1));
            }

            for (var i = paired; i < inserts.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Added, -1, inserts[i]));
            }

            if (deletes.Count == 0 && inserts.Count == 0)
            {
                index++;
            }
        }

        return rows;
    }
}
