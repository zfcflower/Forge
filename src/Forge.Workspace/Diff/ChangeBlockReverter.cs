using System.Text;
using Forge.IO;

namespace Forge.Workspace;

public enum RevertOutcome
{
    Reverted = 0,
    /// <summary>The block's new-side text is no longer where (or anywhere unique near where) the diff said it was.</summary>
    FileChanged = 1,
    /// <summary>Renamed/copied/binary/truncated diffs, or a block from a diff whose new side is not the working tree file.</summary>
    Unsupported = 2,
    Missing = 3,
}

public sealed record RevertResult(RevertOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome == RevertOutcome.Reverted;
}

/// <summary>
/// Reverts change blocks in the working tree by plain file edits (no git apply): the block's new-side lines are located
/// in the current file (expected position first, else a unique match within ±200 lines) and replaced with its old-side
/// lines. Line endings (dominant LF/CRLF) and the trailing-newline state of the file are preserved; writes go through
/// <c>Forge.IO.AtomicFile</c>. Paths are relative to <paramref name="repositoryRoot"/> (git diff paths).
/// </summary>
public static class ChangeBlockReverter
{
    private const int DriftWindow = 200;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reverts one block of <paramref name="diff"/>.</summary>
    public static async Task<RevertResult> RevertBlockAsync(string repositoryRoot, FileDiff diff, ChangeBlock block, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(block);
        if (UnsupportedKind(diff) is { } unsupported)
        {
            return unsupported;
        }

        if (diff.Status == FileDiffStatus.Deleted)
        {
            return new RevertResult(RevertOutcome.Unsupported, "A block of a deleted file cannot be reverted on its own.");
        }

        return await EditAsync(repositoryRoot, diff, [block], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts the whole file: Modified = every block bottom-up in one write; Added = delete the file; Deleted = recreate it
    /// from the old side (requires a full-context diff, otherwise Unsupported); Renamed/Copied/binary/truncated = Unsupported.
    /// </summary>
    public static async Task<RevertResult> RevertFileAsync(string repositoryRoot, FileDiff diff, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (UnsupportedKind(diff) is { } unsupported)
        {
            return unsupported;
        }

        switch (diff.Status)
        {
            case FileDiffStatus.Modified:
                var blocks = DiffBlocks.Split(diff);
                return blocks.Count == 0
                    ? new RevertResult(RevertOutcome.Reverted)
                    : await EditAsync(repositoryRoot, diff, blocks, ct).ConfigureAwait(false);
            case FileDiffStatus.Added:
                return DeleteAdded(repositoryRoot, diff);
            case FileDiffStatus.Deleted:
                return await RecreateDeletedAsync(repositoryRoot, diff, ct).ConfigureAwait(false);
            default:
                return new RevertResult(RevertOutcome.Unsupported, "Rename and copy diffs cannot be reverted.");
        }
    }

    private static RevertResult? UnsupportedKind(FileDiff diff)
    {
        if (diff.IsBinary || diff.IsTruncated || diff.IsRenameOrCopy)
        {
            return new RevertResult(RevertOutcome.Unsupported, "Binary, truncated, renamed and copied diffs cannot be reverted.");
        }

        return null;
    }

    private static string? ResolvePath(string repositoryRoot, string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        var root = Path.GetFullPath(repositoryRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task<RevertResult> EditAsync(string repositoryRoot, FileDiff diff, IReadOnlyList<ChangeBlock> blocks, CancellationToken ct)
    {
        var path = ResolvePath(repositoryRoot, diff.NewPath);
        if (path is null)
        {
            return new RevertResult(RevertOutcome.Unsupported, "The diff has no usable working tree path.");
        }

        var bytes = AtomicFile.ReadAllBytesShared(path, cancellationToken: ct);
        if (bytes is null)
        {
            return new RevertResult(RevertOutcome.Missing);
        }

        if (!TextFile.TryParse(bytes, out var file))
        {
            return new RevertResult(RevertOutcome.Unsupported, "The file is not valid UTF-8 text.");
        }

        var edits = new List<Edit>(blocks.Count);
        foreach (var block in blocks)
        {
            var edit = Plan(file, diff, block);
            if (edit is null)
            {
                return new RevertResult(RevertOutcome.FileChanged, $"Block {block.Index} no longer matches the file.");
            }

            edits.Add(edit.Value);
        }

        // Apply from the bottom so earlier positions stay valid.
        edits.Sort(static (a, b) => b.Start != a.Start ? b.Start.CompareTo(a.Start) : b.End.CompareTo(a.End));
        for (var i = 1; i < edits.Count; i++)
        {
            if (edits[i].End > edits[i - 1].Start)
            {
                return new RevertResult(RevertOutcome.FileChanged, "Blocks overlap in the current file.");
            }
        }

        foreach (var edit in edits)
        {
            file.Replace(edit.Start, edit.End, edit.Replacement, edit.FinalNoEol);
        }

        await AtomicFile.WriteAllBytesAsync(path, file.ToBytes(), cancellationToken: ct).ConfigureAwait(false);
        return new RevertResult(RevertOutcome.Reverted);
    }

    private static RevertResult DeleteAdded(string repositoryRoot, FileDiff diff)
    {
        var path = ResolvePath(repositoryRoot, diff.NewPath);
        if (path is null)
        {
            return new RevertResult(RevertOutcome.Unsupported, "The diff has no usable working tree path.");
        }

        var bytes = AtomicFile.ReadAllBytesShared(path);
        if (bytes is null)
        {
            return new RevertResult(RevertOutcome.Missing);
        }

        if (!TextFile.TryParse(bytes, out var file))
        {
            return new RevertResult(RevertOutcome.FileChanged, "The file is not valid UTF-8 text.");
        }

        var added = new List<string>();
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != DiffLineKind.Added)
                {
                    return new RevertResult(RevertOutcome.FileChanged, "The diff of an added file contains non-added lines.");
                }

                added.Add(line.Text);
            }
        }

        if (file.Lines.Count != added.Count)
        {
            return new RevertResult(RevertOutcome.FileChanged, "The file no longer matches the added content.");
        }

        for (var i = 0; i < added.Count; i++)
        {
            if (!Same(file.Lines[i], added[i]))
            {
                return new RevertResult(RevertOutcome.FileChanged, "The file no longer matches the added content.");
            }
        }

        File.Delete(path);
        return new RevertResult(RevertOutcome.Reverted);
    }

    private static async Task<RevertResult> RecreateDeletedAsync(string repositoryRoot, FileDiff diff, CancellationToken ct)
    {
        var path = ResolvePath(repositoryRoot, diff.OldPath ?? diff.NewPath);
        if (path is null)
        {
            return new RevertResult(RevertOutcome.Unsupported, "The diff has no usable working tree path.");
        }

        // The old side is only fully known when a single hunk starts at line 1 and holds every old line.
        const string Partial = "The deleted file's old side is not fully present in the diff.";
        if (diff.Hunks.Count > 1 || diff.Hunks.Any(static hunk => hunk.IsCorrupt))
        {
            return new RevertResult(RevertOutcome.Unsupported, Partial);
        }

        var lines = new List<string>();
        var noNewLineAtEnd = false;
        if (diff.Hunks.Count == 1)
        {
            var hunk = diff.Hunks[0];
            if (hunk.OldStart != 1)
            {
                return new RevertResult(RevertOutcome.Unsupported, Partial);
            }

            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Added)
                {
                    return new RevertResult(RevertOutcome.Unsupported, Partial);
                }

                lines.Add(line.Text);
                noNewLineAtEnd = line.NoNewLineAtEnd;
            }

            if (lines.Count != hunk.OldCount)
            {
                return new RevertResult(RevertOutcome.Unsupported, Partial);
            }
        }

        if (File.Exists(path))
        {
            return new RevertResult(RevertOutcome.FileChanged, "A file already exists at the deleted path.");
        }

        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(TrimCr(lines[i]));
            if (i < lines.Count - 1 || !noNewLineAtEnd)
            {
                builder.Append('\n');
            }
        }

        await AtomicFile.WriteAllBytesAsync(path, StrictUtf8.GetBytes(builder.ToString()), cancellationToken: ct).ConfigureAwait(false);
        return new RevertResult(RevertOutcome.Reverted);
    }

    private static Edit? Plan(TextFile file, FileDiff diff, ChangeBlock block)
    {
        var replacement = new string[block.OldLines.Count];
        for (var i = 0; i < replacement.Length; i++)
        {
            replacement[i] = TrimCr(block.OldLines[i]);
        }

        if (block.NewLines.Count > 0)
        {
            var start = FindUnique(block.NewStart - 1, position => MatchesBlock(file, position, block.NewLines));
            return start < 0 ? null : new Edit(start, start + block.NewLines.Count, replacement, FinalNoEol(diff, block));
        }

        // Pure deletion: re-insert the old lines between the context lines that surrounded the gap in the diff.
        string? before = null;
        string? after = null;
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind != DiffLineKind.Context)
                {
                    continue;
                }

                if (block.NewStart >= 1 && line.NewLineNumber == block.NewStart)
                {
                    before = line.Text;
                }
                else if (line.NewLineNumber == block.NewStart + 1)
                {
                    after = line.Text;
                }
            }
        }

        if (before is null && after is null)
        {
            return null;
        }

        var point = FindUnique(
            block.NewStart,
            position =>
                position >= 0 &&
                position <= file.Lines.Count &&
                (before is null || (position >= 1 && Same(file.Lines[position - 1], before))) &&
                (after is null || (position < file.Lines.Count && Same(file.Lines[position], after))));
        return point < 0 ? null : new Edit(point, point, replacement, FinalNoEol(diff, block));
    }

    private static bool MatchesBlock(TextFile file, int position, IReadOnlyList<string> expected)
    {
        if (position < 0 || position + expected.Count > file.Lines.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!Same(file.Lines[position + i], expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The expected position if it matches, else the only matching position within the drift window, else -1.</summary>
    private static int FindUnique(int expected, Func<int, bool> matches)
    {
        if (matches(expected))
        {
            return expected;
        }

        var found = -1;
        for (var distance = 1; distance <= DriftWindow; distance++)
        {
            for (var side = 0; side < 2; side++)
            {
                var candidate = side == 0 ? expected - distance : expected + distance;
                if (candidate >= 0 && matches(candidate))
                {
                    if (found >= 0)
                    {
                        return -1;
                    }

                    found = candidate;
                }
            }
        }

        return found;
    }

    private static string TrimCr(string text) => text.EndsWith('\r') ? text[..^1] : text;

    private static bool Same(string left, string right) => TrimCr(left).AsSpan().SequenceEqual(TrimCr(right));

    private readonly record struct Edit(int Start, int End, string[] Replacement, bool? FinalNoEol);

    /// <summary>
    /// End-of-file newline state the replacement's last line must take: true = unterminated (the old side's last line has
    /// no newline), false = terminated (only the new side's last line lacked one), null = block does not involve it.
    /// </summary>
    private static bool? FinalNoEol(FileDiff diff, ChangeBlock block)
    {
        var newTouches = false;
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (!line.NoNewLineAtEnd)
                {
                    continue;
                }

                if (line.Kind == DiffLineKind.Removed && block.OldLines.Count > 0 && line.OldLineNumber == block.OldEnd)
                {
                    return true;
                }

                if (line.Kind == DiffLineKind.Added && block.NewLines.Count > 0 && line.NewLineNumber == block.NewEnd)
                {
                    newTouches = true;
                }
            }
        }

        return newTouches ? false : null;
    }

    private sealed class TextFile
    {
        private TextFile(List<string> lines, List<string> endings, string dominant, bool bom)
        {
            Lines = lines;
            Endings = endings;
            Dominant = dominant;
            Bom = bom;
        }

        public List<string> Lines { get; }

        /// <summary>Terminator of each line: "\n", "\r\n", or "" for an unterminated last line.</summary>
        public List<string> Endings { get; }

        public string Dominant { get; }

        public bool Bom { get; }

        public static bool TryParse(byte[] bytes, out TextFile file)
        {
            file = null!;
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }
            catch (ArgumentException)
            {
                return false;
            }

            var lines = new List<string>();
            var endings = new List<string>();
            int crlf = 0, lf = 0, start = 0;
            while (start < text.Length)
            {
                var newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    lines.Add(text[start..]);
                    endings.Add(string.Empty);
                    break;
                }

                if (newline > start && text[newline - 1] == '\r')
                {
                    lines.Add(text[start..(newline - 1)]);
                    endings.Add("\r\n");
                    crlf++;
                }
                else
                {
                    lines.Add(text[start..newline]);
                    endings.Add("\n");
                    lf++;
                }

                start = newline + 1;
            }

            file = new TextFile(lines, endings, crlf > lf ? "\r\n" : "\n", bom);
            return true;
        }

        public void Replace(int start, int end, string[] replacement, bool? finalNoEol)
        {
            // Without end-of-file info an unterminated last line stays unterminated: the replacement's last line inherits
            // that state. With it, the old side's end-of-file state is restored.
            var lastEnding = finalNoEol switch
            {
                true => string.Empty,
                false => Dominant,
                null => end > start && end == Lines.Count ? Endings[end - 1] : null,
            };
            if (start == Lines.Count && start > 0 && replacement.Length > 0 && Endings[start - 1].Length == 0)
            {
                // Appending after an unterminated last line: it needs its newline first.
                Endings[start - 1] = Dominant;
            }

            Lines.RemoveRange(start, end - start);
            Endings.RemoveRange(start, end - start);
            var inserted = new string[replacement.Length];
            for (var i = 0; i < inserted.Length; i++)
            {
                inserted[i] = i == inserted.Length - 1 && lastEnding is not null ? lastEnding : Dominant;
            }

            Lines.InsertRange(start, replacement);
            Endings.InsertRange(start, inserted);
        }

        public byte[] ToBytes()
        {
            var builder = new StringBuilder();
            for (var i = 0; i < Lines.Count; i++)
            {
                builder.Append(Lines[i]).Append(Endings[i]);
            }

            var body = StrictUtf8.GetBytes(builder.ToString());
            if (!Bom)
            {
                return body;
            }

            var result = new byte[body.Length + 3];
            result[0] = 0xEF;
            result[1] = 0xBB;
            result[2] = 0xBF;
            body.CopyTo(result, 3);
            return result;
        }
    }
}
