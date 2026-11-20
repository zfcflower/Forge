using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Forge.IO;

/// <summary>
/// How a child's bytes become text. Cross-platform tools (git, dotnet, node) write UTF-8, but a native Windows console tool
/// (ping, ipconfig, dir) writes the console OEM code page (936 on a Chinese system), which UTF-8 decoding turns into
/// U+FFFD. So: valid UTF-8 stays UTF-8 (ASCII is both), and the first invalid sequence switches the stream to the OEM code
/// page. Off Windows there is no legacy page and the old behaviour (replace invalid bytes) stays.
/// </summary>
internal static class ProcessTextDecoding
{
    private static readonly Encoding? Legacy = ResolveLegacy();

    /// <summary>One decoder per stream; it carries a split character and the UTF-8 / legacy decision across chunks.</summary>
    public static Decoder CreateDecoder() => new AdaptiveDecoder(Legacy);

    /// <summary>Decodes a whole captured buffer: UTF-8 unless it holds an invalid sequence (a cap cutting the last character is not one).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (Legacy is not null && !IsUtf8(bytes))
        {
            return Legacy.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        // Utf8.IsValid is false for a character cut at the end; treat an incomplete tail as fine.
        var status = Utf8.ToUtf16(bytes, [], out _, out _, replaceInvalidSequences: false, isFinalBlock: false);
        return status != System.Buffers.OperationStatus.InvalidData;
    }

    private static Encoding? ResolveLegacy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var page = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return page == 65001 ? null : Encoding.GetEncoding(page);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class AdaptiveDecoder(Encoding? legacy) : Decoder
    {
        private readonly Decoder? legacyDecoder = legacy?.GetDecoder();
        private readonly Decoder utf8Fallback = Encoding.UTF8.GetDecoder();
        private byte[] carry = [];
        private bool useLegacy;

        public override int GetCharCount(byte[] bytes, int index, int count) => count + carry.Length;

        public override int GetCharCount(ReadOnlySpan<byte> bytes, bool flush) => bytes.Length + carry.Length;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex), flush: false);

        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
        {
            if (useLegacy)
            {
                return legacyDecoder!.GetChars(bytes, chars, flush);
            }

            ReadOnlySpan<byte> input = bytes;
            byte[]? joined = null;
            if (carry.Length > 0)
            {
                joined = new byte[carry.Length + bytes.Length];
                carry.CopyTo(joined, 0);
                bytes.CopyTo(joined.AsSpan(carry.Length));
                input = joined;
                carry = [];
            }

            var status = Utf8.ToUtf16(input, chars, out var read, out var written, replaceInvalidSequences: false, isFinalBlock: flush);
            switch (status)
            {
                case System.Buffers.OperationStatus.InvalidData when legacyDecoder is not null:
                    useLegacy = true;
                    return written + legacyDecoder.GetChars(input[read..], chars[written..], flush);
                case System.Buffers.OperationStatus.InvalidData:
                    // No legacy page: replace, exactly as before.
                    return written + utf8Fallback.GetChars(input[read..], chars[written..], flush);
                case System.Buffers.OperationStatus.NeedMoreData:
                    carry = input[read..].ToArray();
                    return written;
                default:
                    return written;
            }
        }

        public override void Reset()
        {
            carry = [];
            useLegacy = false;
            legacyDecoder?.Reset();
            utf8Fallback.Reset();
        }
    }
}
