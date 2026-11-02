using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Workspace;

internal static class FileDiffFactory
{
    private const int BinaryScanBytes = 8000;

    /// <summary>Builds an add diff for a file git does not track, so reviews can show it.</summary>
    public static async Task<FileDiff?> FromUntrackedFileAsync(string root, string relativePath, int maxBytes, CancellationToken ct)
    {
        var fullPath = Path.Combine(root, relativePath);
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var truncated = info.Length > maxBytes;
        var length = (int)Math.Min(info.Length, Math.Max(maxBytes, 0));
        var buffer = new byte[length];
        var read = 0;
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            while (read < length)
            {
                var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (read == 0)
        {
            return new FileDiff(null, relativePath.Replace('\\', '/'), FileDiffStatus.Added, false, []);
        }

        var span = buffer.AsSpan(0, read);
        var scan = span[..Math.Min(span.Length, BinaryScanBytes)];
        if (scan.Contains((byte)0))
        {
            return new FileDiff(null, relativePath.Replace('\\', '/'), FileDiffStatus.Added, true, []);
        }

        var text = Encoding.UTF8.GetString(span);
        var lines = new List<DiffLine>();
        var lineStart = 0;
        var newLine = 1;
        while (lineStart <= text.Length)
        {
            var end = text.IndexOf('\n', lineStart);
            var hasNewLine = end >= 0;
            if (!hasNewLine)
            {
                end = text.Length;
            }

            var content = text[lineStart..end];
            if (content.EndsWith('\r'))
            {
                content = content[..^1];
            }

            if (!hasNewLine && content.Length == 0)
            {
                break;
            }

            lines.Add(new DiffLine(DiffLineKind.Added, 0, newLine++, content, !hasNewLine && !truncated));
            if (!hasNewLine)
            {
                break;
            }

            lineStart = end + 1;
        }

        var hunk = lines.Count == 0
            ? Array.Empty<DiffHunk>()
            : new[] { new DiffHunk(0, 0, 1, lines.Count, lines) };
        return new FileDiff(null, relativePath.Replace('\\', '/'), FileDiffStatus.Added, false, hunk) { IsTruncated = truncated };
    }
}

public enum DiffAnchorState { Exact, Moved, Stale }

public sealed record DiffAnchorMatch(DiffAnchorState State, int NewStart, int NewCount);

/// <summary>Anchored excerpt resolved against a diff, with the lines rendered in unified-diff form.</summary>
public sealed record DiffQuote(DiffAnchorState State, int NewStart, int NewEnd, string Text);

/// <summary>
/// Stable handle for a review comment on a new-line range of one file: path, hunk header and index, offsets
/// inside the hunk, and a hash of the quoted text. The hash distinguishes "the code moved" from "the code
/// changed", so a comment can be reattached or marked stale instead of silently pointing at other lines.
/// </summary>
public sealed record DiffAnchor(
    string Path,
    string HunkHeader,
    int HunkIndex,
    int OldLine,
    int NewLine,
    int NewLineCount,
    string ContentHash)
{
    internal const int HashLength = 16;

    private const int HashBytes = HashLength / 2;

    public static DiffAnchor Create(FileDiff file, int newLineStart, int newLineCount = 1)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfLessThan(newLineCount, 1);
        var hunkIndex = IndexOfHunk(file, newLineStart);
        if (hunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newLineStart), $"Line {newLineStart} of '{file.Path}' is not part of the diff.");
        }

        var hunk = file.Hunks[hunkIndex];
        var fragment = Fragment(hunk, newLineStart, newLineCount);
        if (fragment.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newLineStart), $"Line {newLineStart} of '{file.Path}' has no new-side content.");
        }

        return new DiffAnchor(
            file.Path,
            hunk.Header,
            hunkIndex,
            fragment[0].OldLineNumber,
            newLineStart,
            newLineCount,
            Hash(fragment));
    }

    public static DiffAnchor? Create(IReadOnlyList<FileDiff> files, string path, int newLineStart, int newLineCount = 1)
    {
        ArgumentNullException.ThrowIfNull(files);
        var file = FindFile(files, path);
        return file is null ? null : Create(file, newLineStart, newLineCount);
    }

    /// <summary>Resolves against the named file; null when the file is absent.</summary>
    public DiffAnchorMatch? Resolve(FileDiff file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new DiffAnchorResolver(file).Resolve(this);
    }

    public DiffAnchorMatch? Resolve(IReadOnlyList<FileDiff> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var file = FindFile(files, Path);
        return file is null ? null : Resolve(file);
    }

    /// <summary>Renders the anchored lines plus context in unified-diff form; null when the file or lines are gone.</summary>
    public DiffQuote? Quote(FileDiff file, int contextLines = 2) => Quote(file, contextLines, new DiffAnchorResolver(file));

    public string? Quote(IReadOnlyList<FileDiff> files, int contextLines = 2)
    {
        ArgumentNullException.ThrowIfNull(files);
        var file = FindFile(files, Path);
        return file is null ? null : Quote(file, contextLines)?.Text;
    }

    internal DiffQuote? Quote(FileDiff file, int contextLines, DiffAnchorResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        var match = resolver.Resolve(this);
        if (match is null)
        {
            return null;
        }

        var hunkIndex = IndexOfHunk(file, match.NewStart);
        if (hunkIndex < 0)
        {
            return null;
        }

        var hunk = file.Hunks[hunkIndex];
        var first = -1;
        var last = -1;
        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            if (line.NewLineNumber < match.NewStart || line.NewLineNumber > match.NewStart + match.NewCount - 1)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            last = i;
        }

        if (first < 0)
        {
            return null;
        }

        var from = Math.Max(0, first - contextLines);
        var to = Math.Min(hunk.Lines.Count - 1, last + contextLines);
        var builder = new StringBuilder();
        var newStart = 0;
        var newEnd = 0;
        for (var i = from; i <= to; i++)
        {
            var line = hunk.Lines[i];
            builder.Append(line.Kind switch
            {
                DiffLineKind.Added => "+ ",
                DiffLineKind.Removed => "- ",
                _ => "  ",
            }).Append(line.Text).Append('\n');
            if (i >= first && i <= last)
            {
                if (newStart == 0 && line.NewLineNumber > 0)
                {
                    newStart = line.NewLineNumber;
                }

                if (line.NewLineNumber > 0)
                {
                    newEnd = line.NewLineNumber;
                }
            }
        }

        if (newStart == 0)
        {
            newStart = match.NewStart;
            newEnd = match.NewStart + match.NewCount - 1;
        }

        return new DiffQuote(match.State, newStart, newEnd, builder.ToString().TrimEnd('\n'));
    }

    internal static FileDiff? FindFile(IReadOnlyList<FileDiff> files, string path)
    {
        foreach (var file in files)
        {
            if (string.Equals(file.Path, path, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    internal static int IndexOfHunk(FileDiff file, int newLine)
    {
        for (var i = 0; i < file.Hunks.Count; i++)
        {
            var hunk = file.Hunks[i];
            if (hunk.NewCount == 0 ? newLine == hunk.NewStart : newLine >= hunk.NewStart && newLine <= hunk.NewEnd)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The new-side lines of a hunk whose numbers fall inside the range, in diff order.</summary>
    internal static List<DiffLine> Fragment(DiffHunk hunk, int newLineStart, int newLineCount)
    {
        var fragment = new List<DiffLine>(Math.Min(newLineCount, hunk.Lines.Count));
        foreach (var line in hunk.Lines)
        {
            if (line.NewLineNumber >= newLineStart && line.NewLineNumber < newLineStart + newLineCount)
            {
                fragment.Add(line);
            }
        }

        return fragment;
    }

    /// <summary>Canonical content hash: one kind prefix plus the line text plus LF per line.</summary>
    internal static string Hash(IReadOnlyList<DiffLine> lines)
    {
        var builder = new StringBuilder(lines.Count * 32);
        foreach (var line in lines)
        {
            builder.Append(CanonicalPrefix(line.Kind)).Append(line.Text).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(digest.AsSpan(0, HashBytes));
    }

    internal static char CanonicalPrefix(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => '+',
        DiffLineKind.Removed => '-',
        _ => ' ',
    };
}

/// <summary>
/// Resolves anchors against one diff. Each hunk gets an index of its new-side lines once, so resolving a fragment
/// is a hash of its bytes rather than a linear scan per candidate line; the moved-anchor search then walks every
/// window of the queried line count once, and later anchors of that count reuse the same table.
/// </summary>
public sealed class DiffAnchorResolver
{
    private readonly Dictionary<int, HunkIndex> indexes = [];
    private readonly Dictionary<int, Dictionary<ulong, int>> movedByCount = [];

    public DiffAnchorResolver(FileDiff file)
    {
        ArgumentNullException.ThrowIfNull(file);
        File = file;
    }

    public FileDiff File { get; }

    /// <summary>Resolves every anchor against the same file, reusing the per-hunk index.</summary>
    public IReadOnlyList<DiffAnchorMatch?> ResolveMany(IEnumerable<DiffAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var matches = new List<DiffAnchorMatch?>();
        foreach (var anchor in anchors)
        {
            matches.Add(Resolve(anchor));
        }

        return matches;
    }

    public DiffAnchorMatch? Resolve(DiffAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (!string.Equals(File.Path, anchor.Path, StringComparison.Ordinal))
        {
            return null;
        }

        if (anchor.NewLineCount <= 0)
        {
            return new DiffAnchorMatch(DiffAnchorState.Stale, anchor.NewLine, 0);
        }

        if (!TryParseHash(anchor.ContentHash, out var hash))
        {
            return new DiffAnchorMatch(DiffAnchorState.Stale, anchor.NewLine, anchor.NewLineCount);
        }

        if (anchor.HunkIndex >= 0 && anchor.HunkIndex < File.Hunks.Count)
        {
            var recorded = File.Hunks[anchor.HunkIndex];
            if (string.Equals(recorded.Header, anchor.HunkHeader, StringComparison.Ordinal)
                && Index(anchor.HunkIndex).MatchesAt(anchor.NewLine, anchor.NewLineCount, hash))
            {
                return new DiffAnchorMatch(DiffAnchorState.Exact, anchor.NewLine, anchor.NewLineCount);
            }
        }

        var moved = FindMoved(anchor.NewLineCount, hash);
        return moved >= 0
            ? new DiffAnchorMatch(DiffAnchorState.Moved, moved, anchor.NewLineCount)
            : new DiffAnchorMatch(DiffAnchorState.Stale, anchor.NewLine, anchor.NewLineCount);
    }

    private int FindMoved(int newLineCount, ulong hash)
    {
        if (!movedByCount.TryGetValue(newLineCount, out var cache))
        {
            cache = BuildMovedCache(newLineCount);
            movedByCount[newLineCount] = cache;
        }

        return cache.TryGetValue(hash, out var start) ? start : -1;
    }

    private static bool TryParseHash(string contentHash, out ulong hash) =>
        ulong.TryParse(contentHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);

    private Dictionary<ulong, int> BuildMovedCache(int count)
    {
        var cache = new Dictionary<ulong, int>();
        for (var hunkIndex = 0; hunkIndex < File.Hunks.Count; hunkIndex++)
        {
            if (File.Hunks[hunkIndex].NewCount == 0)
            {
                continue;
            }

            Index(hunkIndex).Collect(count, cache);
        }

        return cache;
    }

    private HunkIndex Index(int hunkIndex)
    {
        if (!indexes.TryGetValue(hunkIndex, out var index))
        {
            index = new HunkIndex(File.Hunks[hunkIndex]);
            indexes[hunkIndex] = index;
        }

        return index;
    }

    /// <summary>New-side lines of one hunk kept as one contiguous canonical byte buffer for O(1) window hashing.</summary>
    private sealed class HunkIndex
    {
        private const int HashBytes = DiffAnchor.HashLength / 2;

        private readonly int[] newNumbers;
        private readonly int[] offsets;
        private readonly byte[] canonical;
        private Dictionary<int, int>? positionByNewLine;

        public HunkIndex(DiffHunk hunk)
        {
            var side = new List<DiffLine>(hunk.Lines.Count);
            foreach (var line in hunk.Lines)
            {
                if (line.NewLineNumber > 0)
                {
                    side.Add(line);
                }
            }

            Count = side.Count;
            FirstNewLine = Count > 0 ? side[0].NewLineNumber : hunk.NewStart;
            newNumbers = new int[Count];
            offsets = new int[Count + 1];
            var total = 0;
            for (var i = 0; i < Count; i++)
            {
                newNumbers[i] = side[i].NewLineNumber;
                total += 2 + Encoding.UTF8.GetByteCount(side[i].Text);
            }

            canonical = new byte[total];
            var position = 0;
            for (var i = 0; i < Count; i++)
            {
                offsets[i] = position;
                canonical[position++] = (byte)DiffAnchor.CanonicalPrefix(side[i].Kind);
                position += Encoding.UTF8.GetBytes(side[i].Text, canonical.AsSpan(position));
                canonical[position++] = 0x0A;
            }

            offsets[Count] = position;
        }

        public int Count { get; }

        private int FirstNewLine { get; }

        public bool MatchesAt(int newLine, int count, ulong hash)
        {
            var index = IndexOf(newLine);
            return index >= 0
                && index + count <= Count
                && newNumbers[index + count - 1] == newLine + count - 1
                && HashAt(index, count) == hash;
        }

        /// <summary>Records the first new-line number of every window of <paramref name="count"/> lines by hash.</summary>
        public void Collect(int count, Dictionary<ulong, int> cache)
        {
            if (count <= 0 || count > Count)
            {
                return;
            }

            for (var i = 0; i + count <= Count; i++)
            {
                if (newNumbers[i + count - 1] != newNumbers[i] + count - 1)
                {
                    continue;
                }

                cache.TryAdd(HashAt(i, count), newNumbers[i]);
            }
        }

        private int IndexOf(int newLine)
        {
            if (Count == 0)
            {
                return -1;
            }

            var direct = newLine - FirstNewLine;
            if (direct >= 0 && direct < Count && newNumbers[direct] == newLine)
            {
                return direct;
            }

            positionByNewLine ??= BuildPositions();
            return positionByNewLine.TryGetValue(newLine, out var index) ? index : -1;
        }

        private Dictionary<int, int> BuildPositions()
        {
            var positions = new Dictionary<int, int>(Count);
            for (var i = 0; i < Count; i++)
            {
                positions.TryAdd(newNumbers[i], i);
            }

            return positions;
        }

        private ulong HashAt(int index, int count)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(canonical.AsSpan(offsets[index], offsets[index + count] - offsets[index]), digest);
            return BinaryPrimitives.ReadUInt64BigEndian(digest[..HashBytes]);
        }
    }
}
