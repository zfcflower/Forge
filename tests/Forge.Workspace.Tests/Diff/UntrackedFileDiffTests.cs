using System.Reflection;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Diff;

public sealed class UntrackedFileDiffTests
{
    [Fact]
    public async Task Text_without_trailing_newline_marks_last_line()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("note.txt"), "a\nb", new UTF8Encoding(false));

        var file = await FromUntrackedAsync(temp.Path, "note.txt", 1024);

        Assert.NotNull(file);
        Assert.Equal(FileDiffStatus.Added, file.Status);
        Assert.Equal("note.txt", file.Path);
        Assert.False(file.IsBinary);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["a", "b"], hunk.Lines.Select(static line => line.Text));
        Assert.Equal([1, 2], hunk.Lines.Select(static line => line.NewLineNumber));
        Assert.All(hunk.Lines, static line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.False(hunk.Lines[0].NoNewLineAtEnd);
        Assert.True(hunk.Lines[1].NoNewLineAtEnd);
        Assert.Equal(2, file.AddedLineCount);
    }

    [Fact]
    public async Task Crlf_text_loses_carriage_returns_in_line_text()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("win.txt"), Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        var file = await FromUntrackedAsync(temp.Path, "win.txt", 1024);

        var hunk = Assert.Single(file!.Hunks);
        Assert.Equal(["a", "b"], hunk.Lines.Select(static line => line.Text));
        Assert.All(hunk.Lines, static line => Assert.False(line.NoNewLineAtEnd));
    }

    [Fact]
    public async Task File_with_trailing_newline_has_no_extra_empty_line()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("tail.txt"), "only\n", new UTF8Encoding(false));

        var file = await FromUntrackedAsync(temp.Path, "tail.txt", 1024);

        Assert.Equal(["only"], Assert.Single(file!.Hunks).Lines.Select(static line => line.Text));
    }

    [Fact]
    public async Task Null_byte_marks_the_file_as_binary_without_lines()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("blob.bin"), [0x50, 0x4B, 0x00, 0x03, 0x14]);

        var file = await FromUntrackedAsync(temp.Path, "blob.bin", 1024);

        Assert.NotNull(file);
        Assert.True(file.IsBinary);
        Assert.Empty(file.Hunks);
        Assert.Equal(FileDiffStatus.Added, file.Status);
    }

    [Fact]
    public async Task Empty_file_is_an_add_without_hunks()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(temp.Combine("empty.txt"), []);

        var file = await FromUntrackedAsync(temp.Path, "empty.txt", 1024);

        Assert.NotNull(file);
        Assert.Equal(FileDiffStatus.Added, file.Status);
        Assert.False(file.IsBinary);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public async Task Oversized_file_is_read_up_to_the_limit_and_flagged_truncated()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("big.txt"), "abcdefghij", new UTF8Encoding(false));

        var file = await FromUntrackedAsync(temp.Path, "big.txt", 4);

        Assert.NotNull(file);
        Assert.True(file.IsTruncated);
        var line = Assert.Single(Assert.Single(file.Hunks).Lines);
        Assert.Equal("abcd", line.Text);
        Assert.False(line.NoNewLineAtEnd);
    }

    [Fact]
    public async Task Missing_file_yields_null()
    {
        using var temp = new TempDirectory();

        Assert.Null(await FromUntrackedAsync(temp.Path, "absent.txt", 1024));
    }

    private static async Task<FileDiff?> FromUntrackedAsync(string root, string relativePath, int maxBytes)
    {
        var type = typeof(GitCli).Assembly.GetType("Forge.Workspace.FileDiffFactory", throwOnError: true)!;
        var method = type.GetMethod("FromUntrackedFileAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var task = (Task<FileDiff?>)method.Invoke(null, [root, relativePath, maxBytes, CancellationToken.None])!;
        return await task;
    }
}
