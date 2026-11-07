using System.Text;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The whole-file and append writer: a whole write is atomic and leaves no temporary file, the parent directory is
/// created, a read-only destination is refused without touching its bytes, and two writers of the same path leave one
/// of the two payloads, never a mixture.
/// </summary>
public class TextFileWriterTests
{
    [Fact]
    public async Task A_whole_write_replaces_the_file_and_leaves_no_temporary_file()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("state.txt", "old content\n");
        var content = "new content\nwith a second line\n";

        var written = await TextFileWriter.WriteAsync(path, content, CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetByteCount(content), written);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(workspace.TemporaryFiles());
    }

    [Fact]
    public async Task A_write_creates_the_missing_parent_directories()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("a/b/c/file.txt");

        await TextFileWriter.WriteAsync(path, "deep", CancellationToken.None);

        Assert.True(Directory.Exists(workspace.Full("a/b/c")), "the parent directory was not created");
        Assert.Equal("deep", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_append_adds_to_the_end_and_reports_the_appended_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("log.txt", "first\n");

        var written = await TextFileWriter.AppendAsync(path, "second\n", CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetByteCount("second\n"), written);
        Assert.Equal("first\nsecond\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_append_creates_a_missing_file()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("nested/created.txt");

        await TextFileWriter.AppendAsync(path, "created\n", CancellationToken.None);

        Assert.Equal("created\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Empty_content_writes_an_empty_file()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("emptied.txt", "not empty yet");

        var written = await TextFileWriter.WriteAsync(path, string.Empty, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Empty(File.ReadAllBytes(path));
        Assert.Empty(workspace.TemporaryFiles());
    }

    [Fact]
    public async Task The_reported_byte_count_is_utf8_not_character_count()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("utf8.txt");

        var written = await TextFileWriter.WriteAsync(path, "héllo", CancellationToken.None);

        Assert.Equal(6, written);
        Assert.Equal(new byte[] { (byte)'h', 0xC3, 0xA9, (byte)'l', (byte)'l', (byte)'o' }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_read_only_target_is_refused_and_keeps_its_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("readonly.txt", "original\n");
        var original = File.ReadAllBytes(path);
        MakeReadOnly(path);
        try
        {
            var exception = await Record.ExceptionAsync(
                async () => await TextFileWriter.WriteAsync(path, "replacement\n", CancellationToken.None));

            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.Equal(original, File.ReadAllBytes(path));
            AssertStillReadOnly(path);
            Assert.Empty(workspace.TemporaryFiles());
        }
        finally
        {
            MakeWritable(path);
        }
    }

    [Fact]
    public async Task Two_writers_of_the_same_path_leave_one_complete_payload()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("contended.txt", "initial\n");
        var first = new string('a', 200_000) + "\n";
        var second = new string('b', 200_000) + "\n";

        await Task.WhenAll(
            Task.Run(() => TextFileWriter.WriteAsync(path, first, CancellationToken.None).AsTask()),
            Task.Run(() => TextFileWriter.WriteAsync(path, second, CancellationToken.None).AsTask())).WaitAsync(TestLimit);

        var content = File.ReadAllText(path);
        Assert.Contains(content, new[] { first, second });
        Assert.Empty(workspace.TemporaryFiles());
    }

    [Fact]
    public async Task A_cancelled_write_throws_and_leaves_the_previous_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Write("state.txt", "previous\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(
            async () => await TextFileWriter.WriteAsync(path, "next\n", cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal("previous\n", File.ReadAllText(path));
    }
}
