using System.Buffers;
using System.Text;

namespace Forge.IO;

/// <summary>Whole lines taken from a text file, with the 1-based number of the first one.</summary>
public readonly record struct TextFileWindow(string[] Lines, int FirstLine, bool Truncated)
{
    /// <summary>How many lines the window holds.</summary>
    public int LineCount => Lines.Length;
}

/// <summary>The bytes are not text: the leading probe holds a NUL, or it is not valid UTF-8.</summary>
public sealed class NotTextFileException(long byteLength)
    : Exception($"The file looks binary or is not UTF-8 text ({byteLength} bytes).")
{
    /// <summary>The size of the whole file in bytes, for messages that name the file.</summary>
    public long ByteLength { get; } = byteLength;
}

/// <summary>
/// Text reads bounded by a byte budget. The encoding comes from a leading BOM (UTF-8, UTF-16LE, UTF-16BE); a file
/// without one must be strict UTF-8 in its first <see cref="ProbeBytes"/> bytes. A window streams the file in pooled
/// chunks, so reading a hundred lines of a hundred-megabyte file touches one chunk and allocates the size of the answer.
/// </summary>
public static class TextFileReader
{
    /// <summary>How many leading bytes decide the encoding and the binary check.</summary>
    public const int ProbeBytes = 8 * 1024;

    private const int ChunkBytes = 64 * 1024;
    private const int Utf8BomLength = 3;
    private const int Utf16BomLength = 2;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads the lines from <paramref name="firstLine"/> up to <paramref name="maxLines"/> lines or
    /// <paramref name="maxBytes"/> bytes, whichever comes first. Nothing beyond the limit that fires is kept, so a
    /// window of an arbitrarily large file allocates the size of the window.
    /// </summary>
    /// <exception cref="NotTextFileException">The leading bytes are binary or not UTF-8.</exception>
    public static async ValueTask<TextFileWindow> ReadWindowAsync(
        string path,
        int firstLine,
        int maxLines,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var chunk = ArrayPool<byte>.Shared.Rent(Math.Max(ChunkBytes, ProbeBytes));
        var window = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            using var stream = OpenRead(path);
            var probed = await FillAsync(stream, chunk, ProbeBytes, cancellationToken).ConfigureAwait(false);
            var (encoding, breaks, header) = Sniff(chunk.AsSpan(0, probed), stream.Length);

            // The probe is the first slice of the scan; dropping the BOM keeps every later slice aligned.
            var available = probed - header;
            chunk.AsSpan(header, available).CopyTo(chunk);

            var lineNumber = 1;
            var windowLength = 0;
            var windowScan = 0;
            var capturedLines = 0;
            var truncated = false;
            var eof = false;
            var finished = false;

            while (true)
            {
                var usable = breaks.Wide ? available & ~1 : available;
                var slice = chunk.AsSpan(0, usable);
                var position = 0;

                while (position < usable)
                {
                    if (lineNumber < firstLine)
                    {
                        var newline = breaks.IndexOfNewline(slice, position);
                        if (newline < 0)
                        {
                            position = usable;
                            break;
                        }

                        position = newline + breaks.Length;
                        lineNumber++;
                        continue;
                    }

                    var room = maxBytes - windowLength;
                    if (room == 0)
                    {
                        truncated = true;
                        finished = true;
                        break;
                    }

                    var take = Math.Min(usable - position, room);
                    var appended = windowLength + take;
                    slice.Slice(position, take).CopyTo(window.AsSpan(windowLength));
                    position += take;

                    var scan = windowScan;
                    var newlines = 0;
                    var cut = -1;
                    while (true)
                    {
                        var newline = breaks.IndexOfNewline(window.AsSpan(0, appended), scan);
                        if (newline < 0)
                        {
                            scan = appended;
                            break;
                        }

                        newlines++;
                        scan = newline + breaks.Length;
                        if (capturedLines + newlines == maxLines)
                        {
                            cut = scan;
                            break;
                        }
                    }

                    if (cut >= 0)
                    {
                        truncated = cut < appended;
                        windowLength = cut;
                        windowScan = cut;
                        capturedLines = maxLines;
                        finished = true;
                        break;
                    }

                    capturedLines += newlines;
                    windowScan = scan;
                    windowLength = appended;
                }

                if (finished)
                {
                    break;
                }

                var carry = available - usable;
                if (carry > 0)
                {
                    chunk[0] = chunk[usable];
                }

                if (eof)
                {
                    break;
                }

                var want = lineNumber >= firstLine
                    ? Math.Max(1, Math.Min(chunk.Length - carry, maxBytes - windowLength))
                    : chunk.Length - carry;
                var read = await stream.ReadAsync(chunk.AsMemory(carry, want), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    eof = true;
                    available = 0;
                    continue;
                }

                available = carry + read;
                if (lineNumber >= firstLine && windowLength >= maxBytes)
                {
                    truncated = true;
                    break;
                }
            }

            // A window that ended on its last line still has to ask whether the file has more, or the answer overstates
            // itself as complete.
            if (finished && !truncated && !eof)
            {
                truncated = await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0;
            }

            if (truncated)
            {
                windowLength = TrimIncompleteTail(window.AsSpan(0, windowLength), breaks);
            }

            return new TextFileWindow(SplitLines(window, windowLength, capturedLines + 1, encoding, breaks), firstLine, truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    /// <summary>Reads a whole text file for a read-modify-write, without its BOM.</summary>
    /// <exception cref="InvalidOperationException">The file is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="NotTextFileException">The leading bytes are binary or not UTF-8.</exception>
    public static async ValueTask<string> ReadAllTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        using var stream = OpenRead(path);
        var length = stream.Length;
        if (length > maxBytes)
        {
            throw new InvalidOperationException(
                $"'{path}' is {length} bytes, over the {maxBytes}-byte read limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            var read = await FillAsync(stream, buffer, (int)length, cancellationToken).ConfigureAwait(false);
            var (encoding, _, header) = Sniff(buffer.AsSpan(0, Math.Min(read, ProbeBytes)), length);
            return encoding.GetString(buffer, header, read - header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });

    private static async ValueTask<int> FillAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static (Encoding Encoding, LineBreaks Breaks, int Header) Sniff(ReadOnlySpan<byte> probe, long byteLength)
    {
        if (probe.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (Utf8, LineBreaks.Narrow, Utf8BomLength);
        }

        if (probe.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode, LineBreaks.LittleEndian16, Utf16BomLength);
        }

        if (probe.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode, LineBreaks.BigEndian16, Utf16BomLength);
        }

        if (probe.Contains((byte)0))
        {
            throw new NotTextFileException(byteLength);
        }

        try
        {
            // A sequence the probe cut in half is not a decoding failure; the rest of the file is read leniently.
            StrictUtf8.GetDecoder().GetCharCount(probe, flush: false);
        }
        catch (DecoderFallbackException)
        {
            throw new NotTextFileException(byteLength);
        }

        return (Utf8, LineBreaks.Narrow, 0);
    }

    private static string[] SplitLines(byte[] window, int length, int capacity, Encoding encoding, in LineBreaks breaks)
    {
        var body = window.AsSpan(0, length);
        if (body.Length == 0)
        {
            return [];
        }

        var lines = new List<string>(capacity);
        var start = 0;
        while (start < body.Length)
        {
            var newline = breaks.IndexOfNewline(body, start);
            if (newline < 0)
            {
                lines.Add(DecodeLine(body[start..], encoding, breaks));
                break;
            }

            lines.Add(DecodeLine(body[start..newline], encoding, breaks));
            start = newline + breaks.Length;
        }

        return [.. lines];
    }

    private static string DecodeLine(ReadOnlySpan<byte> line, Encoding encoding, in LineBreaks breaks) =>
        encoding.GetString(breaks.TrimCarriageReturn(line));

    /// <summary>Drops the bytes of a character the byte budget cut in half, so the window never ends in U+FFFD.</summary>
    private static int TrimIncompleteTail(ReadOnlySpan<byte> window, in LineBreaks breaks)
    {
        var end = window.Length;
        if (breaks.Wide)
        {
            return end & ~1;
        }

        var continuation = 0;
        while (continuation < 3 && continuation < end && (window[end - continuation - 1] & 0xC0) == 0x80)
        {
            continuation++;
        }

        var lead = end - continuation - 1;
        if (lead < 0)
        {
            return end;
        }

        var needed = window[lead] switch
        {
            >= 0xF0 => 4,
            >= 0xE0 => 3,
            >= 0xC0 => 2,
            _ => 1,
        };

        return needed > continuation + 1 ? lead : end;
    }

    /// <summary>One line terminator in a byte stream: a single <c>\n</c> unit, or a two-byte unit of a UTF-16 stream.</summary>
    private readonly struct LineBreaks
    {
        public static readonly LineBreaks Narrow = new(wide: false, (byte)'\n', 0, (byte)'\r', 0);
        public static readonly LineBreaks LittleEndian16 = new(wide: true, (byte)'\n', 0, (byte)'\r', 0);
        public static readonly LineBreaks BigEndian16 = new(wide: true, 0, (byte)'\n', 0, (byte)'\r');

        private readonly bool wide;
        private readonly byte newlineFirst;
        private readonly byte newlineSecond;
        private readonly byte carriageFirst;
        private readonly byte carriageSecond;

        private LineBreaks(bool wide, byte newlineFirst, byte newlineSecond, byte carriageFirst, byte carriageSecond)
        {
            this.wide = wide;
            this.newlineFirst = newlineFirst;
            this.newlineSecond = newlineSecond;
            this.carriageFirst = carriageFirst;
            this.carriageSecond = carriageSecond;
        }

        /// <summary>Two bytes per unit, so a line can only start and end on an even offset.</summary>
        public bool Wide => wide;

        /// <summary>The width of one terminator in bytes.</summary>
        public int Length => wide ? 2 : 1;

        /// <summary>The index of the first terminator at or after <paramref name="start"/>, or -1.</summary>
        public int IndexOfNewline(ReadOnlySpan<byte> buffer, int start)
        {
            if (!wide)
            {
                var found = buffer[start..].IndexOf((byte)'\n');
                return found < 0 ? -1 : start + found;
            }

            for (var index = start; index + 1 < buffer.Length; index += 2)
            {
                if (buffer[index] == newlineFirst && buffer[index + 1] == newlineSecond)
                {
                    return index;
                }
            }

            return -1;
        }

        public ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line)
        {
            if (!wide)
            {
                return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
            }

            return line.Length >= 2 && line[^2] == carriageFirst && line[^1] == carriageSecond ? line[..^2] : line;
        }
    }
}
