using System.Diagnostics;
using System.Text;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class AtomicFileReplaceTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMilliseconds(300);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_written_temp_file_is_published_onto_the_destination(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("old content that is longer than the new one"));
        var temporary = directory.File("state.json.5f2c.tmp");
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes("{}"));

        await ReplaceAsync(useAsync, temporary, path);

        Assert.Equal(Encoding.UTF8.GetBytes("{}"), File.ReadAllBytes(path));
        Assert.False(File.Exists(temporary));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_destination_that_does_not_exist_yet_is_created(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("fresh.json");
        var temporary = directory.File("fresh.json.ed2a.tmp");
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes("{}"));

        await ReplaceAsync(useAsync, temporary, path);

        Assert.Equal(Encoding.UTF8.GetBytes("{}"), File.ReadAllBytes(path));
        Assert.False(File.Exists(temporary));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_only_destination_fails_immediately_and_removes_the_temp_file(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encoding.UTF8.GetBytes("original");
        File.WriteAllBytes(path, original);
        var temporary = directory.File("state.json.bbbb.tmp");
        var replacement = Encoding.UTF8.GetBytes("replacement");
        File.WriteAllBytes(temporary, replacement);
        var budget = TimeSpan.FromSeconds(5);
        var options = new AtomicFileOptions { RetryBudget = budget };
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Exception? exception;
        TimeSpan elapsed;
        try
        {
            var start = Stopwatch.GetTimestamp();
            exception = await Record.ExceptionAsync(() => ReplaceAsync(useAsync, temporary, path, options));
            elapsed = Stopwatch.GetElapsedTime(start);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.True(elapsed < budget / 10, $"the replace gave up after {elapsed.TotalMilliseconds:F0} ms, so it was retried");
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(replacement, File.ReadAllBytes(path));
        }

        Assert.False(File.Exists(temporary));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_directory_destination_fails_immediately_and_removes_the_temp_file(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        Directory.CreateDirectory(path);
        var temporary = directory.File("state.json.cccc.tmp");
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes("replacement"));
        var budget = TimeSpan.FromSeconds(5);
        var options = new AtomicFileOptions { RetryBudget = budget };

        var start = Stopwatch.GetTimestamp();
        var exception = await Record.ExceptionAsync(() => ReplaceAsync(useAsync, temporary, path, options));
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.GetType().FullName);
        Assert.True(elapsed < budget / 10, $"the failure took {elapsed.TotalMilliseconds:F0} ms, so it was retried");
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(temporary));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_the_publish_throws_and_removes_the_temp_file(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encoding.UTF8.GetBytes("original");
        File.WriteAllBytes(path, original);
        var temporary = directory.File("state.json.dddd.tmp");
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes("replacement"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Record.ExceptionAsync(() => ReplaceAsync(useAsync, temporary, path, token: cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(temporary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_destination_held_beyond_the_budget_fails_with_the_original_exception(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encoding.UTF8.GetBytes("original");
        File.WriteAllBytes(path, original);
        var temporary = directory.File("state.json.eeee.tmp");
        var replacement = Encoding.UTF8.GetBytes("replacement");
        File.WriteAllBytes(temporary, replacement);
        var options = new AtomicFileOptions { RetryBudget = ShortBudget };

        Exception? exception;
        TimeSpan elapsed;
        using (OpenLikeReader(path))
        {
            var start = Stopwatch.GetTimestamp();
            exception = await Record.ExceptionAsync(() => ReplaceAsync(useAsync, temporary, path, options));
            elapsed = Stopwatch.GetElapsedTime(start);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.True(elapsed >= ShortBudget, $"the replace gave up after {elapsed.TotalMilliseconds:F0} ms, before its budget");
            Assert.True(elapsed < TestLimit, $"the replace took {elapsed.TotalMilliseconds:F0} ms");
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(replacement, File.ReadAllBytes(path));
        }

        Assert.False(File.Exists(temporary));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_holder_that_releases_within_the_budget_lets_the_publish_succeed(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("original"));
        var temporary = directory.File("state.json.ffff.tmp");
        var replacement = Encoding.UTF8.GetBytes("replacement");
        File.WriteAllBytes(temporary, replacement);
        var options = new AtomicFileOptions { RetryBudget = TestLimit };
        var held = OpenLikeReader(path);
        long finished = 0;

        var publish = Task.Run(async () =>
        {
            await ReplaceAsync(useAsync, temporary, path, options);
            finished = Stopwatch.GetTimestamp();
        });
        await Task.Delay(ReleaseAfter);
        var released = Stopwatch.GetTimestamp();
        held.Dispose();

        await publish.WaitAsync(TestLimit);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(finished >= released, "the replace completed while the reader still held the file");
        }

        Assert.Equal(replacement, File.ReadAllBytes(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_non_positive_budget_is_rejected_before_any_file_is_touched(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var temporary = directory.File("state.json.1234.tmp");
        File.WriteAllBytes(temporary, [1]);
        var options = new AtomicFileOptions { RetryBudget = TimeSpan.Zero };

        var exception = useAsync
            ? Record.Exception(() => { _ = AtomicFile.ReplaceFileAsync(temporary, path, options); })
            : Record.Exception(() => AtomicFile.ReplaceFile(temporary, path, options));

        Assert.Equal("options", Assert.IsType<ArgumentOutOfRangeException>(exception).ParamName);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(temporary));
    }

    private static async Task ReplaceAsync(bool useAsync, string temporary, string path, AtomicFileOptions? options = null, CancellationToken token = default)
    {
        if (useAsync)
        {
            await AtomicFile.ReplaceFileAsync(temporary, path, options, token);
        }
        else
        {
            AtomicFile.ReplaceFile(temporary, path, options, token);
        }
    }

    private static FileStream OpenLikeReader(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
