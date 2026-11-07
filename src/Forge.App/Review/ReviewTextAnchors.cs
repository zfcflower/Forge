using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using AvaloniaEdit.Document;
using Forge.Workspace;

namespace Forge.App.Review;

internal enum ReviewAnchorState { Exact, Moved, Lost }

internal readonly record struct ReviewResolution(ReviewAnchorState State, int Start, int End);

/// <summary>
/// Builds and re-finds <see cref="DiffAnchor"/>s for lines of a text document. A moved anchor is accepted only when
/// exactly one window of the file carries its hash, so a blank or repeated line is never reattached to other code.
/// </summary>
internal static class ReviewTextAnchors
{
    public const int MaxSearchLines = 20_000;

    /// <summary>One string copy of the document; split it off the UI thread with <see cref="SplitLines"/>.</summary>
    public static ITextSource Snapshot(TextDocument document) => document.CreateSnapshot();

    /// <summary>Lines of <paramref name="text"/> as the editor counts them: the last line is the (possibly empty) text after the final break.</summary>
    public static string[] SplitLines(ITextSource source)
    {
        var text = source.Text;
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n')
            {
                continue;
            }

            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        lines.Add(text.Substring(start));
        return [.. lines];
    }

    /// <summary>The anchor of lines <paramref name="start"/>.. of the file whose text is <paramref name="range"/>; only the range is touched.</summary>
    public static DiffAnchor Create(string path, IReadOnlyList<string> range, int start) =>
        DiffAnchor.Create(Window(path, range, 0, range.Count, start), start, range.Count);

    public static string[] RangeLines(TextDocument document, int start, int end)
    {
        var lines = new string[end - start + 1];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = document.GetLineByNumber(start + i);
            lines[i] = document.GetText(line.Offset, line.Length);
        }

        return lines;
    }

    public static ReviewResolution Resolve(ReviewComment comment, IReadOnlyList<string> lines)
    {
        var count = comment.NewLineEnd - comment.NewLineStart + 1;
        var lost = new ReviewResolution(ReviewAnchorState.Lost, comment.NewLineStart, comment.NewLineEnd);
        if (comment.Anchor is not { } anchor || count < 1 || anchor.NewLineCount != count)
        {
            return lost;
        }

        using var hasher = WindowHasher.TryCreate(anchor.ContentHash);
        if (hasher is null)
        {
            return lost;
        }
        if (comment.NewLineEnd <= lines.Count && hasher.Matches(lines, comment.NewLineStart, count))
        {
            return new ReviewResolution(ReviewAnchorState.Exact, comment.NewLineStart, comment.NewLineEnd);
        }

        if (lines.Count > MaxSearchLines)
        {
            return lost;
        }

        var found = -1;
        for (var start = 1; start + count - 1 <= lines.Count; start++)
        {
            if (!hasher.Matches(lines, start, count))
            {
                continue;
            }

            if (found >= 0)
            {
                return lost;
            }

            found = start;
        }

        return found < 0 ? lost : new ReviewResolution(ReviewAnchorState.Moved, found, found + count - 1);
    }

    // Matches DiffAnchor's added-line UTF-8 hash without constructing a diff for each candidate window.
    private sealed class WindowHasher : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] target;
        private byte[] buffer = ArrayPool<byte>.Shared.Rent(256);

        private WindowHasher(byte[] target) => this.target = target;

        public static WindowHasher? TryCreate(string hex)
        {
            Span<byte> bytes = stackalloc byte[32];
            var status = Convert.FromHexString(hex, bytes, out var consumed, out var written);
            return status == OperationStatus.Done && consumed == hex.Length && written > 0
                ? new WindowHasher(bytes[..written].ToArray())
                : null;
        }

        public bool Matches(IReadOnlyList<string> lines, int start, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var text = lines[start - 1 + i];
                var need = Encoding.UTF8.GetMaxByteCount(text.Length) + 2;
                if (buffer.Length < need)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(need);
                }
                buffer[0] = (byte)'+';
                var written = Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 1);
                buffer[written + 1] = (byte)'\n';
                hash.AppendData(buffer, 0, written + 2);
            }
            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            return digest[..target.Length].SequenceEqual(target);
        }

        public void Dispose()
        {
            hash.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileDiff Window(string path, IReadOnlyList<string> lines, int offset, int count, int start)
    {
        var list = new List<DiffLine>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new DiffLine(DiffLineKind.Added, 0, start + i, lines[offset + i]));
        }

        return new FileDiff(null, path, FileDiffStatus.Added, false, [new DiffHunk(0, 0, start, count, list)]);
    }
}
