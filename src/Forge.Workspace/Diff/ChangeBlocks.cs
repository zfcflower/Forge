using System.Security.Cryptography;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// One reviewable change block of a file diff: a maximal run of consecutive added/removed lines (context lines split
/// blocks). <see cref="OldStart"/>/<see cref="NewStart"/> are 1-based; when a side has no lines the start is the line the
/// block sits after on that side (0 = before the first line), matching git's zero-count hunk convention.
/// <see cref="Hash"/> is content-addressed (path + the block's -/+ lines), so it identifies "the same change" across reloads
/// and stops matching as soon as the change itself differs. Within one file, identical blocks are told apart by their
/// occurrence ordinal: the first block with a given content hash keeps the plain content hash (so stored marks of
/// non-duplicated blocks stay valid), the 2nd, 3rd... mix their 1-based occurrence number among identical blocks into it.
/// </summary>
public sealed record ChangeBlock(
    int Index,
    int OldStart,
    IReadOnlyList<string> OldLines,
    int NewStart,
    IReadOnlyList<string> NewLines,
    string Hash)
{
    public int OldCount => OldLines.Count;

    public int NewCount => NewLines.Count;

    /// <summary>Last new-side line covered by the block, or <see cref="NewStart"/> for a pure deletion.</summary>
    public int NewEnd => NewLines.Count == 0 ? NewStart : NewStart + NewLines.Count - 1;

    public int OldEnd => OldLines.Count == 0 ? OldStart : OldStart + OldLines.Count - 1;
}

public static class DiffBlocks
{
    /// <summary>Splits every hunk of <paramref name="diff"/> into change blocks, numbered in file order from 0.</summary>
    public static IReadOnlyList<ChangeBlock> Split(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var blocks = new List<ChangeBlock>();
        var path = diff.Path.Replace('\\', '/');
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hunk in diff.Hunks)
        {
            // Running positions: the last old/new line number seen so far inside this hunk.
            var lastOld = hunk.OldStart - (hunk.OldCount == 0 ? 0 : 1);
            var lastNew = hunk.NewStart - (hunk.NewCount == 0 ? 0 : 1);
            List<string>? oldLines = null;
            List<string>? newLines = null;
            var blockOldStart = 0;
            var blockNewStart = 0;

            void Flush()
            {
                if (oldLines is null && newLines is null)
                {
                    return;
                }

                IReadOnlyList<string> removed = oldLines ?? [];
                IReadOnlyList<string> added = newLines ?? [];
                var hash = HashOf(path, removed, added);
                var occurrence = seen.GetValueOrDefault(hash) + 1;
                seen[hash] = occurrence;
                if (occurrence > 1)
                {
                    hash = Hex(hash + "#" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                blocks.Add(new ChangeBlock(
                    blocks.Count,
                    removed.Count == 0 ? lastOld : blockOldStart,
                    removed,
                    added.Count == 0 ? lastNew : blockNewStart,
                    added,
                    hash));
                oldLines = null;
                newLines = null;
            }

            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Context:
                        Flush();
                        lastOld = line.OldLineNumber;
                        lastNew = line.NewLineNumber;
                        break;
                    case DiffLineKind.Removed:
                        if (oldLines is null)
                        {
                            oldLines = [];
                            blockOldStart = line.OldLineNumber;
                        }

                        oldLines.Add(line.Text);
                        break;
                    default:
                        if (newLines is null)
                        {
                            newLines = [];
                            blockNewStart = line.NewLineNumber;
                        }

                        newLines.Add(line.Text);
                        break;
                }

                if (line.Kind == DiffLineKind.Removed)
                {
                    lastOld = Math.Max(lastOld, line.OldLineNumber);
                }
                else if (line.Kind == DiffLineKind.Added)
                {
                    lastNew = Math.Max(lastNew, line.NewLineNumber);
                }
            }

            // Positions of a pending pure insertion/deletion are "after the last context line", not after the block.
            Flush();
        }

        return blocks;
    }

    /// <summary>
    /// Content fingerprint of a whole file diff (path, status and every block hash). The "viewed" mark is stored against
    /// it, so any change to the file's diff invalidates the mark.
    /// </summary>
    public static string Fingerprint(FileDiff diff, IReadOnlyList<ChangeBlock>? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        blocks ??= Split(diff);
        var builder = new StringBuilder();
        builder.Append(diff.Path.Replace('\\', '/')).Append('\n').Append(diff.Status).Append('\n');
        builder.Append(diff.OldPath).Append('\n').Append(diff.IsBinary ? '1' : '0').Append('\n');
        foreach (var block in blocks)
        {
            builder.Append(block.Hash).Append('\n');
        }

        return Hex(builder.ToString());
    }

    private static string HashOf(string path, IReadOnlyList<string> removed, IReadOnlyList<string> added)
    {
        var builder = new StringBuilder(path.Length + 64);
        builder.Append(path).Append('\n');
        foreach (var line in removed)
        {
            builder.Append('-').Append(line).Append('\n');
        }

        foreach (var line in added)
        {
            builder.Append('+').Append(line).Append('\n');
        }

        return Hex(builder.ToString());
    }

    private static string Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
