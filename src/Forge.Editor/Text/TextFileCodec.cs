using System.Buffers;
using System.Text;
using Forge.IO;

namespace Forge.Editor.Text;

/// <summary>Encoding, BOM presence and dominant line ending of a file on disk.</summary>
public sealed record TextFileFormat(Encoding Encoding, bool HasBom, LineEnding LineEnding)
{
    public static TextFileFormat Utf8 { get; } = new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false, LineEnding.Lf);

    /// <summary>Rewrite line breaks to <see cref="LineEnding"/> on save; off by default so a save never reflows a file.</summary>
    public bool NormalizeLineEndingsOnSave { get; init; }

    public string LineEndingText => LineEndingInfo.Text(LineEnding);
}

/// <summary>Text read from disk together with the format that reproduces it byte-for-byte on save.</summary>
public sealed record TextFileData(string Text, TextFileFormat Format, bool IsBinary);

/// <summary>
/// Reads and writes text files with BOM/encoding/line-ending detection. Writes stream to a temporary file in the
/// same directory, flush it to disk, and replace the destination through <see cref="AtomicFile"/>, so a cancelled or
/// failed save never truncates the original.
/// </summary>
public static class TextFileCodec
{
    private const int SniffBytes = 8192;
    private const int StreamBufferSize = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false);
    private static readonly UTF32Encoding Utf32Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UTF32Encoding Utf32Be = new(bigEndian: true, byteOrderMark: false);

    public static async ValueTask<TextFileData> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static TextFileData Decode(ReadOnlySpan<byte> bytes)
    {
        var (encoding, bomLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes[bomLength..]);
        var wide = encoding.CodePage is 1200 or 1201 or 12000 or 12001;
        var isBinary = !wide && bomLength == 0 && LooksBinary(bytes);
        var lineEnding = DetectLineEnding(text);
        return new TextFileData(text, new TextFileFormat(encoding, bomLength > 0, lineEnding), isBinary);
    }

    public static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (Utf32Le, 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (Utf32Be, 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8NoBom, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Utf16Le, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Utf16Be, 2);
        }

        if (DetectBomlessWide(bytes) is { } wide)
        {
            return (wide, 0);
        }

        if (IsValidUtf8(bytes))
        {
            return (Utf8NoBom, 0);
        }

        return (Encoding.Latin1, 0);
    }

    public static LineEnding DetectLineEnding(ReadOnlySpan<char> text)
    {
        int lf = 0, crlf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        return new LineEndingInfo(lf, crlf, cr).Dominant;
    }

    public static async ValueTask WriteAsync(string path, TextSnapshot snapshot, TextFileFormat format, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(format);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                StreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteContentAsync(stream, snapshot, format, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await AtomicFile.ReplaceFileAsync(temporary, path, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async ValueTask WriteContentAsync(Stream stream, TextSnapshot snapshot, TextFileFormat format, CancellationToken ct)
    {
        byte[] bom = format.HasBom ? BomFor(format.Encoding) : [];
        if (bom.Length > 0)
        {
            await stream.WriteAsync(bom, ct).ConfigureAwait(false);
        }

        var encoder = format.Encoding.GetEncoder();
        if (format.NormalizeLineEndingsOnSave && snapshot.LineEnding != format.LineEnding)
        {
            var normalized = Normalize(snapshot.GetText(), format.LineEnding);
            var bytes = format.Encoding.GetBytes(normalized);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return;
        }

        var encoded = ArrayPool<byte>.Shared.Rent(StreamBufferSize * 4);
        try
        {
            foreach (var chunk in snapshot.Rope)
            {
                var memory = chunk;
                var offset = 0;
                while (offset < memory.Length)
                {
                    encoder.Convert(memory.Span[offset..], encoded, flush: false, out var charsUsed, out var bytesUsed, out _);
                    offset += charsUsed;
                    if (bytesUsed > 0)
                    {
                        await stream.WriteAsync(encoded.AsMemory(0, bytesUsed), ct).ConfigureAwait(false);
                    }

                    if (charsUsed == 0)
                    {
                        break;
                    }
                }

                ct.ThrowIfCancellationRequested();
            }

            encoder.Convert(ReadOnlySpan<char>.Empty, encoded, flush: true, out _, out var tail, out _);
            if (tail > 0)
            {
                await stream.WriteAsync(encoded.AsMemory(0, tail), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    public static string Normalize(string text, LineEnding ending)
    {
        var target = LineEndingInfo.Text(ending == LineEnding.Mixed ? LineEnding.Lf : ending);
        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(target);
            }
            else if (c == '\n')
            {
                builder.Append(target);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static byte[] BomFor(Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble is null || preamble.Length == 0)
        {
            return encoding.CodePage switch
            {
                65001 => [0xEF, 0xBB, 0xBF],
                1200 => [0xFF, 0xFE],
                1201 => [0xFE, 0xFF],
                12000 => [0xFF, 0xFE, 0x00, 0x00],
                12001 => [0x00, 0x00, 0xFE, 0xFF],
                _ => [],
            };
        }

        return preamble;
    }

    /// <summary>
    /// Recognises BOM-less UTF-16 by its zero-byte parity; requires enough bytes that a binary file is not mistaken
    /// for text, so files without a BOM that hold control bytes stay detectable as binary.
    /// </summary>
    private static Encoding? DetectBomlessWide(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return null;
        }

        var sample = bytes.Length > SniffBytes ? bytes[..SniffBytes] : bytes;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            if (sample[i] != 0)
            {
                continue;
            }

            if ((i & 1) == 0)
            {
                evenZeros++;
            }
            else
            {
                oddZeros++;
            }
        }

        var threshold = Math.Max(4, sample.Length / 4);
        if (oddZeros >= threshold && evenZeros == 0)
        {
            return Utf16Le;
        }

        if (evenZeros >= threshold && oddZeros == 0)
        {
            return Utf16Be;
        }

        return null;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes.Length > SniffBytes ? bytes[..SniffBytes] : bytes;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
