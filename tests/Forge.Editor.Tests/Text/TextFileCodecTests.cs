using System.Diagnostics;
using System.Text;
using Forge.Editor.Tests.Support;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Text;

public class TextFileCodecTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public async Task Utf8_file_with_bom_loads_without_the_bom_and_saves_byte_for_byte()
    {
        using var dir = new TempDirectory();
        var original = Utf8Bom.Concat(Encoding.UTF8.GetBytes("héllo\r\nworld")).ToArray();
        var path = dir.WriteBytes("bom.txt", original);

        var document = await TextDocument.LoadAsync(path);

        Assert.Equal("héllo\r\nworld", document.Snapshot.GetText());
        Assert.NotNull(document.FileFormat);
        Assert.True(document.FileFormat.HasBom);
        Assert.Equal(LineEnding.CrLf, document.FileFormat.LineEnding);
        Assert.Equal(Path.GetFullPath(path), document.Path);
        Assert.False(document.IsDirty);

        var copy = dir.PathOf("copy.txt");
        await document.SaveAsync(copy);
        Assert.Equal(original, await File.ReadAllBytesAsync(copy));
    }

    [Fact]
    public void Utf8_without_bom_is_detected_and_reported_as_such()
    {
        var data = TextFileCodec.Decode(Encoding.UTF8.GetBytes("plain ✓"));

        Assert.Equal("plain ✓", data.Text);
        Assert.False(data.IsBinary);
        Assert.False(data.Format.HasBom);
        Assert.Equal(65001, data.Format.Encoding.CodePage);
        Assert.Equal(LineEnding.None, data.Format.LineEnding);
    }

    [Fact]
    public async Task Utf16_little_endian_with_bom_round_trips()
    {
        using var dir = new TempDirectory();
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hi\n😀")).ToArray();
        var path = dir.WriteBytes("le.txt", bytes);

        var document = await TextDocument.LoadAsync(path);

        Assert.Equal("hi\n😀", document.Snapshot.GetText());
        Assert.Equal(1200, document.FileFormat!.Encoding.CodePage);
        var copy = dir.PathOf("le-copy.txt");
        await document.SaveAsync(copy);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
    }

    [Fact]
    public void Utf16_big_endian_with_bom_is_decoded()
    {
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("be")).ToArray();

        var data = TextFileCodec.Decode(bytes);

        Assert.Equal("be", data.Text);
        Assert.Equal(1201, data.Format.Encoding.CodePage);
        Assert.True(data.Format.HasBom);
        Assert.False(data.IsBinary);
    }

    [Fact]
    public void Utf32_little_endian_bom_is_detected_before_utf16()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0x00 }.Concat(Encoding.UTF32.GetBytes("x")).ToArray();

        var data = TextFileCodec.Decode(bytes);

        Assert.Equal("x", data.Text);
        Assert.Equal(12000, data.Format.Encoding.CodePage);
    }

    [Fact]
    public void Bomless_utf16_is_recognised_by_zero_byte_parity()
    {
        var bytes = Encoding.Unicode.GetBytes("hello there, wide world");

        var data = TextFileCodec.Decode(bytes);

        Assert.Equal("hello there, wide world", data.Text);
        Assert.Equal(1200, data.Format.Encoding.CodePage);
        Assert.False(data.Format.HasBom);
        Assert.False(data.IsBinary);
    }

    [Fact]
    public async Task Latin1_fallback_round_trips_non_utf8_bytes()
    {
        using var dir = new TempDirectory();
        var bytes = new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A, 0x31 };
        var path = dir.WriteBytes("latin.txt", bytes);

        var document = await TextDocument.LoadAsync(path);

        Assert.Equal("café\n1", document.Snapshot.GetText());
        Assert.Equal(28591, document.FileFormat!.Encoding.CodePage);
        var copy = dir.PathOf("latin-copy.txt");
        await document.SaveAsync(copy);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
    }

    [Fact]
    public async Task Png_header_is_rejected_as_binary()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("image.bin", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D]);

        Assert.True(TextFileCodec.Decode(await File.ReadAllBytesAsync(path)).IsBinary);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await TextDocument.LoadAsync(path));
    }

    [Fact]
    public async Task Valid_utf8_containing_nul_is_rejected_as_binary()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("nul.txt", Encoding.UTF8.GetBytes("abc\0def"));

        await Assert.ThrowsAsync<NotSupportedException>(async () => await TextDocument.LoadAsync(path));
    }

    [Fact]
    public void Empty_bytes_decode_to_an_empty_text()
    {
        var data = TextFileCodec.Decode(ReadOnlySpan<byte>.Empty);

        Assert.Equal(string.Empty, data.Text);
        Assert.False(data.IsBinary);
        Assert.Equal(LineEnding.None, data.Format.LineEnding);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf)]
    [InlineData("a\nb", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Cr)]
    [InlineData("a\r\nb\nc", LineEnding.Mixed)]
    [InlineData("abc", LineEnding.None)]
    public void Line_ending_detection_reports_the_dominant_kind(string text, LineEnding expected) =>
        Assert.Equal(expected, TextFileCodec.DetectLineEnding(text));

    [Fact]
    public async Task Save_keeps_existing_line_endings_unless_normalisation_is_requested()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("crlf.txt", Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var document = await TextDocument.LoadAsync(path);
        document.Insert(0, "X");

        await document.SaveAsync();

        Assert.Equal("Xa\r\nb\r\n", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task Normalisation_on_save_rewrites_all_breaks_to_the_format_ending()
    {
        using var dir = new TempDirectory();
        var path = dir.PathOf("normalised.txt");
        var format = new TextFileFormat(new UTF8Encoding(false), false, LineEnding.Lf) { NormalizeLineEndingsOnSave = true };
        var snapshot = TextSnapshot.FromText("a\r\nb\rc\nd");

        await TextFileCodec.WriteAsync(path, snapshot, format);

        Assert.Equal("a\nb\nc\nd", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public void Normalize_maps_mixed_target_to_lf_and_keeps_text_otherwise()
    {
        Assert.Equal("a\nb\nc", TextFileCodec.Normalize("a\r\nb\rc", LineEnding.Mixed));
        Assert.Equal("a\r\nb\r\n", TextFileCodec.Normalize("a\nb\r", LineEnding.CrLf));
        Assert.Equal("é\n", TextFileCodec.Normalize("é\n", LineEnding.Lf));
    }

    [Fact]
    public async Task Save_writes_bom_and_utf16_bytes_when_the_format_asks_for_them()
    {
        using var dir = new TempDirectory();
        var path = dir.PathOf("wide.txt");
        var format = new TextFileFormat(Encoding.Unicode, true, LineEnding.Lf);

        await TextFileCodec.WriteAsync(path, TextSnapshot.FromText("ok"), format);

        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x6F, 0x00, 0x6B, 0x00 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Surrogate_pair_split_across_rope_leaves_is_encoded_correctly()
    {
        using var dir = new TempDirectory();
        var text = new string('a', 1023) + "😀" + new string('b', 1500);
        var snapshot = TextSnapshot.FromText(text);
        var path = dir.PathOf("pair.txt");

        await TextFileCodec.WriteAsync(path, snapshot, TextFileFormat.Utf8);

        var data = TextFileCodec.Decode(await File.ReadAllBytesAsync(path));
        Assert.Equal(text, data.Text);
    }

    [Fact]
    public async Task Large_document_survives_a_save_and_reload()
    {
        using var dir = new TempDirectory();
        var text = string.Concat(Enumerable.Range(0, 20000).Select(i => $"line {i} ✓\r\n"));
        var document = new TextDocument(text);
        var path = dir.PathOf("big.txt");
        document.FileFormat = new TextFileFormat(new UTF8Encoding(false), false, LineEnding.CrLf);

        await document.SaveAsync(path);

        var reloaded = await TextDocument.LoadAsync(path);
        Assert.Equal(text, reloaded.Snapshot.GetText());
        Assert.Equal(20001, reloaded.Snapshot.LineCount);
    }

    [Fact]
    public async Task Saving_while_another_handle_holds_the_file_succeeds_once_it_is_released()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("held.txt", "original");
        var document = new TextDocument("replacement");
        document.Insert(0, "x");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long finished = 0;

        var save = Task.Run(async () =>
        {
            await document.SaveAsync(path);
            finished = Stopwatch.GetTimestamp();
        });
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var released = Stopwatch.GetTimestamp();
        held.Dispose();

        await save.WaitAsync(TimeSpan.FromSeconds(30));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(finished >= released, "the save completed while the holder still had the file open");
        }

        Assert.Equal("xreplacement", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Root));
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task A_failed_save_leaves_the_original_bytes_and_no_temporary_file()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("readonly.txt", "original\r\ncontent");
        var original = await File.ReadAllBytesAsync(path);
        var document = new TextDocument("replacement");
        document.Insert(0, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            var start = Stopwatch.GetTimestamp();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => document.SaveAsync(path).AsTask());
            var elapsed = Stopwatch.GetElapsedTime(start);

            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"the failed save returned after {elapsed.TotalMilliseconds:F0} ms, so it was retried");
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(dir.Root));
            Assert.True(document.IsDirty);
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "the file lost its ReadOnly attribute");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Cancelled_save_leaves_the_original_file_and_no_temporary_files()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("keep.txt", "original");
        var document = new TextDocument(new string('z', 5000), path);
        document.Insert(0, "!");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.SaveAsync(null, cts.Token).AsTask());

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Root));
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task Save_creates_missing_directories_and_marks_the_document_clean()
    {
        using var dir = new TempDirectory();
        var path = dir.PathOf(Path.Combine("nested", "deeper", "f.txt"));
        var document = new TextDocument("content");

        await document.SaveAsync(path);

        Assert.Equal("content", await File.ReadAllTextAsync(path));
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task Save_without_any_path_is_an_error()
    {
        var document = new TextDocument("x");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await document.SaveAsync());
    }

    [Fact]
    public async Task Save_replaces_an_existing_file_and_removes_its_temporary()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("replace.txt", "old text that is longer than the new one");
        var document = new TextDocument("new");

        await document.SaveAsync(path);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir.Root));
    }

    [Fact]
    public async Task Loading_a_missing_file_fails_with_a_file_not_found_error()
    {
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await TextDocument.LoadAsync(dir.PathOf("absent.txt")));
    }

    [Fact]
    public async Task Loading_with_a_cancelled_token_throws()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("c.txt", "x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TextFileCodec.ReadAsync(path, cts.Token).AsTask());
    }

    [Fact]
    public void Default_utf8_format_has_no_bom_and_lf()
    {
        var format = TextFileFormat.Utf8;

        Assert.False(format.HasBom);
        Assert.False(format.NormalizeLineEndingsOnSave);
        Assert.Equal(LineEnding.Lf, format.LineEnding);
        Assert.Equal("\n", format.LineEndingText);
    }

    [Fact]
    public void Detect_encoding_reports_bom_lengths()
    {
        Assert.Equal(3, TextFileCodec.DetectEncoding(Utf8Bom).BomLength);
        Assert.Equal(2, TextFileCodec.DetectEncoding(new byte[] { 0xFF, 0xFE, 0x41 }).BomLength);
        Assert.Equal(0, TextFileCodec.DetectEncoding(Encoding.UTF8.GetBytes("abc")).BomLength);
    }
}
