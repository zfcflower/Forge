using System.Text;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The bounded reader: a window of whole lines, a byte budget that cuts anywhere and says so, BOM sniffing for UTF-8 and
/// both UTF-16 byte orders, a binary verdict for NUL bytes and for bytes that are not UTF-8, cancellation, and an
/// allocation cost that follows the window rather than the file.
/// </summary>
public class TextFileReaderTests
{
    private const int WideLimit = 1 << 20;

    [Fact]
    public async Task A_line_window_returns_the_requested_lines_and_reports_its_first_line()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));

        var window = await TextFileReader.ReadWindowAsync(path, 3, 4, WideLimit, CancellationToken.None);

        Assert.Equal(3, window.FirstLine);
        Assert.Equal(["line03", "line04", "line05", "line06"], window.Lines);
        Assert.True(window.Truncated);
    }

    [Fact]
    public async Task A_window_that_covers_the_whole_file_is_not_truncated()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(10, window.LineCount);
        Assert.Equal("line10", window.Lines[^1]);
        Assert.False(window.Truncated);
    }

    [Fact]
    public async Task An_offset_past_the_end_returns_no_lines_and_no_truncation_marker()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));

        var window = await TextFileReader.ReadWindowAsync(path, 100, 10, WideLimit, CancellationToken.None);

        Assert.Equal(100, window.FirstLine);
        Assert.Empty(window.Lines);
        Assert.False(window.Truncated);
    }

    [Fact]
    public async Task A_byte_budget_cuts_the_window_and_marks_it_truncated()
    {
        // Each numbered line is 7 bytes with its terminator, so a 15-byte budget holds two whole lines and the first byte
        // of the third; the reader drops the incomplete character tail but not the partial line, and the truncation flag
        // is what tells the caller the window is not the whole file.
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, 15, CancellationToken.None);

        Assert.Equal(["line01", "line02", "l"], window.Lines);
        Assert.True(window.Truncated);
    }

    [Fact]
    public async Task The_line_limit_wins_over_a_larger_byte_budget()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));

        var window = await TextFileReader.ReadWindowAsync(path, 1, 3, WideLimit, CancellationToken.None);

        Assert.Equal(["line01", "line02", "line03"], window.Lines);
        Assert.True(window.Truncated);
    }

    [Fact]
    public async Task An_empty_file_has_no_lines_and_is_not_truncated()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("empty.txt", string.Empty);

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(1, window.FirstLine);
        Assert.Empty(window.Lines);
        Assert.False(window.Truncated);
    }

    [Fact]
    public async Task A_file_without_a_trailing_newline_returns_its_last_line()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("no-newline.txt", "first\nlast");

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(["first", "last"], window.Lines);
        Assert.False(window.Truncated);
    }

    [Fact]
    public async Task Crlf_lines_keep_their_order_and_lose_their_carriage_return()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(["alpha", "beta", "gamma"], window.Lines);
    }

    [Fact]
    public async Task A_utf8_bom_is_dropped_from_the_first_line()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("bom.txt");
        File.WriteAllText(path, "bom line\nsecond\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(["bom line", "second"], window.Lines);
    }

    [Fact]
    public async Task Utf16_little_endian_text_is_decoded_when_the_bom_says_so()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("utf16le.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n", Encoding.Unicode);

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(["one", "two"], window.Lines);
    }

    [Fact]
    public async Task Utf16_big_endian_text_is_decoded_when_the_bom_says_so()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("utf16be.txt");
        File.WriteAllText(path, "one\ntwo\n", Encoding.BigEndianUnicode);

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None);

        Assert.Equal(["one", "two"], window.Lines);
    }

    [Fact]
    public async Task A_nul_byte_in_the_probe_marks_the_file_binary()
    {
        using var workspace = new TemporaryWorkspace();
        var bytes = new byte[] { (byte)'t', (byte)'x', (byte)'t', 0, (byte)'m', (byte)'o', (byte)'r', (byte)'e' };
        var path = workspace.WriteBytes("binary.bin", bytes);

        var exception = await Assert.ThrowsAsync<NotTextFileException>(
            async () => await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None));

        Assert.Equal((long)bytes.Length, exception.ByteLength);
        Assert.Contains("binary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_that_are_not_valid_utf8_mark_the_file_binary()
    {
        using var workspace = new TemporaryWorkspace();
        var bytes = new byte[] { (byte)'a', 0xC3, 0x28, (byte)'b', (byte)'\n' };
        var path = workspace.WriteBytes("latin1.txt", bytes);

        var exception = await Assert.ThrowsAsync<NotTextFileException>(
            async () => await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, CancellationToken.None));

        Assert.Equal((long)bytes.Length, exception.ByteLength);
    }

    [Fact]
    public async Task A_multibyte_character_cut_by_the_byte_budget_is_not_returned_as_a_replacement_character()
    {
        using var workspace = new TemporaryWorkspace();
        // One line of four two-byte characters: a five byte budget covers two of them and one lead byte.
        var path = workspace.Write("accents.txt", "éééé\n");

        var window = await TextFileReader.ReadWindowAsync(path, 1, 10, 5, CancellationToken.None);

        Assert.Equal(["éé"], window.Lines);
        Assert.True(window.Truncated);
    }

    [Fact]
    public async Task A_cancelled_token_throws_OperationCanceledException()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", Numbered(1, 10));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(
            async () => await TextFileReader.ReadWindowAsync(path, 1, 10, WideLimit, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public async Task A_line_position_below_one_is_rejected(int firstLine, int maxLines)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", "one\n");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await TextFileReader.ReadWindowAsync(path, firstLine, maxLines, WideLimit, CancellationToken.None));
    }

    [Fact]
    public async Task A_byte_budget_below_one_is_rejected()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("lines.txt", "one\n");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await TextFileReader.ReadWindowAsync(path, 1, 1, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Reading_a_hundred_lines_of_a_hundred_megabyte_file_allocates_the_window_not_the_file()
    {
        // A sparse 100 MiB file whose first 13 KiB is text, so the binary probe sees only text. The read asks for 100
        // lines; an implementation that read the file, or that buffered the tail of it, would allocate around 100 MiB.
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("huge.txt");
        var head = Numbered(1, 1000, "line{0:D4}");
        Assert.True(Encoding.UTF8.GetByteCount(head) > TextFileReader.ProbeBytes, "the head must cover the whole probe");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes(head));
            stream.SetLength(100L * 1024 * 1024);
        }

        Assert.Equal(100L * 1024 * 1024, new FileInfo(path).Length);
        TextFileWindow window = default;
        var lowest = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            window = await TextFileReader.ReadWindowAsync(path, 1, 100, 256 * 1024, CancellationToken.None);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            lowest = Math.Min(lowest, allocated);
        }

        Assert.Equal(100, window.LineCount);
        Assert.Equal("line0001", window.Lines[0]);
        Assert.True(window.Truncated);
        Assert.True(
            lowest < 4 * 1024 * 1024,
            $"reading 100 lines of a 100 MiB file allocated {lowest} bytes; the window, not the file, is the budget");
    }

    /// <summary>Text with one numbered line per entry, using the given format for the number.</summary>
    private static string Numbered(int from, int to, string format = "line{0:D2}")
    {
        var text = new StringBuilder();
        for (var number = from; number <= to; number++)
        {
            text.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, number)).Append('\n');
        }

        return text.ToString();
    }
}
