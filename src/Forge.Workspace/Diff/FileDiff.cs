using System.Globalization;

namespace Forge.Workspace;

public enum FileDiffStatus { Modified, Added, Deleted, Renamed, Copied }

public enum DiffLineKind { Context, Added, Removed }

/// <summary>
/// One line of a hunk. The line number that does not exist on the other side is 0, so an added line has
/// <see cref="OldLineNumber"/> 0 and a removed line has <see cref="NewLineNumber"/> 0.
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, int OldLineNumber, int NewLineNumber, string Text, bool NoNewLineAtEnd = false);

public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>Canonical header used for anchors; always carries explicit counts.</summary>
    public string Header =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@");

    /// <summary>The body did not match the counts declared in the header, so the line numbers cannot be trusted.</summary>
    public bool IsCorrupt { get; init; }

    public int OldEnd => OldCount == 0 ? OldStart : OldStart + OldCount - 1;

    public int NewEnd => NewCount == 0 ? NewStart : NewStart + NewCount - 1;

    public IEnumerable<DiffLine> LinesOf(DiffLineKind kind) => Lines.Where(line => line.Kind == kind);
}

public sealed record FileDiff(
    string? OldPath,
    string? NewPath,
    FileDiffStatus Status,
    bool IsBinary,
    IReadOnlyList<DiffHunk> Hunks)
{
    public string? OldMode { get; init; }

    public string? NewMode { get; init; }

    /// <summary>Rename/copy similarity in percent as reported by git, when present.</summary>
    public int? Similarity { get; init; }

    /// <summary>Content was cut off before parsing (oversized untracked file).</summary>
    public bool IsTruncated { get; init; }

    public string Path => NewPath ?? OldPath ?? string.Empty;

    public bool IsRenameOrCopy => Status is FileDiffStatus.Renamed or FileDiffStatus.Copied;

    public int AddedLineCount => Hunks.Sum(static hunk => hunk.Lines.Count(static line => line.Kind == DiffLineKind.Added));

    public int RemovedLineCount => Hunks.Sum(static hunk => hunk.Lines.Count(static line => line.Kind == DiffLineKind.Removed));

    public DiffHunk? FindHunk(int newLine)
    {
        foreach (var hunk in Hunks)
        {
            if (hunk.NewCount == 0 ? newLine == hunk.NewStart : newLine >= hunk.NewStart && newLine <= hunk.NewEnd)
            {
                return hunk;
            }
        }

        return null;
    }
}
