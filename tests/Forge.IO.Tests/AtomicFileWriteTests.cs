using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class AtomicFileWriteTests
{
    private const int PayloadBytes = 100 * 1024;
    private const int RewriteCount = 150;
    private const int ReaderCount = 4;

    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CancelDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMilliseconds(300);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Written_bytes_round_trip_and_leave_no_temporary_file(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.bin");
        byte[] payload = [0, 1, 2, 255, 10, 13];

        await WriteAsync(useAsync, path, payload);

        Assert.Equal(payload, AtomicFile.ReadAllBytesShared(path));
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_shorter_rewrite_replaces_the_longer_previous_content_completely(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        await WriteAsync(useAsync, path, Encoding.UTF8.GetBytes(new string('x', 4096)));
        var shorter = Encoding.UTF8.GetBytes("{}");

        await WriteAsync(useAsync, path, shorter);

        Assert.Equal(shorter, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_parent_directories_are_created(bool useAsync)
    {
        using var directory = new TestDirectory();
        var nested = Path.Combine(directory.Path, "nested", "deeper");
        var path = Path.Combine(nested, "state.json");

        await WriteAsync(useAsync, path, Encoding.UTF8.GetBytes("{}"));

        Assert.True(Directory.Exists(nested));
        Assert.Equal(Encoding.UTF8.GetBytes("{}"), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rewrites_never_expose_a_missing_partial_or_mixed_file_to_concurrent_readers(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.bin");
        AtomicFile.WriteAllBytes(path, Payload(0));
        var failures = new ConcurrentBag<string>();
        var writeErrors = new ConcurrentBag<string>();
        using var stop = new ManualResetEventSlim(false);
        var readers = Enumerable.Range(0, ReaderCount)
            .Select(_ => Task.Run(() => ReadUntilStopped(path, stop, failures)))
            .ToArray();

        try
        {
            for (var version = 1; version <= RewriteCount; version++)
            {
                try
                {
                    await WriteAsync(useAsync, path, Payload(version));
                }
                catch (Exception exception)
                {
                    writeErrors.Add($"rewrite {version}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        finally
        {
            stop.Set();
        }

        var completedReads = (await Task.WhenAll(readers).WaitAsync(TestLimit)).Sum();

        Assert.Empty(writeErrors);
        Assert.Empty(failures);
        Assert.True(completedReads >= ReaderCount, $"only {completedReads} reads completed");
        Assert.Equal(Payload(RewriteCount), File.ReadAllBytes(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reader_holding_the_file_beyond_the_budget_fails_with_the_original_exception_and_keeps_the_old_bytes(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encode(1);
        File.WriteAllBytes(path, original);
        var options = new AtomicFileOptions { RetryBudget = ShortBudget };

        Exception? exception;
        TimeSpan elapsed;
        using (OpenLikeReader(path))
        {
            var start = Stopwatch.GetTimestamp();
            exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(2), options));
            elapsed = Stopwatch.GetElapsedTime(start);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.True(elapsed >= ShortBudget, $"the write gave up after {elapsed.TotalMilliseconds:F0} ms, before its budget");
            Assert.True(elapsed < TestLimit, $"the write took {elapsed.TotalMilliseconds:F0} ms");
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(1, Decode(File.ReadAllBytes(path)).Count);
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(Encode(2), File.ReadAllBytes(path));
        }

        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reader_that_releases_during_the_retry_lets_the_write_succeed(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        File.WriteAllBytes(path, Encode(1));
        var options = new AtomicFileOptions { RetryBudget = TestLimit };
        var held = OpenLikeReader(path);
        long finished = 0;

        var write = Task.Run(async () =>
        {
            await WriteAsync(useAsync, path, Encode(2), options);
            finished = Stopwatch.GetTimestamp();
        });
        await Task.Delay(ReleaseAfter);
        var released = Stopwatch.GetTimestamp();
        held.Dispose();

        await write.WaitAsync(TestLimit);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(finished >= released, "the write completed while the reader still held the file");
        }

        Assert.Equal(2, Decode(File.ReadAllBytes(path)).Count);
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_during_the_replace_retry_throws_and_keeps_the_old_bytes(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encode(1);
        File.WriteAllBytes(path, original);
        var options = new AtomicFileOptions { RetryBudget = TimeSpan.FromSeconds(10) };
        using var cancellation = new CancellationTokenSource(CancelDelay);

        Exception? exception;
        TimeSpan elapsed;
        using (OpenLikeReader(path))
        {
            var start = Stopwatch.GetTimestamp();
            exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(2), options, cancellation.Token));
            elapsed = Stopwatch.GetElapsedTime(start);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"the cancelled write returned after {elapsed.TotalMilliseconds:F0} ms");
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(Encode(2), File.ReadAllBytes(path));
        }

        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pre_cancelled_write_throws_without_touching_the_destination(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encode(1);
        File.WriteAllBytes(path, original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(2), token: cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pre_cancelled_first_write_creates_nothing(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "nested", "state.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(1), token: cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_only_destination_fails_immediately_without_retrying(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var original = Encode(1);
        File.WriteAllBytes(path, original);
        var budget = TimeSpan.FromSeconds(5);
        var options = new AtomicFileOptions { RetryBudget = budget };
        MakeReadOnly(path);
        var mode = UnixMode(path);

        Exception? exception;
        TimeSpan elapsed;
        try
        {
            var start = Stopwatch.GetTimestamp();
            exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(2), options));
            elapsed = Stopwatch.GetElapsedTime(start);

            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.True(elapsed < budget / 10, $"the write gave up after {elapsed.TotalMilliseconds:F0} ms, so it was retried");
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(1, Decode(File.ReadAllBytes(path)).Count);
            AssertStillReadOnly(path);
            Assert.Equal(mode, UnixMode(path));
        }
        finally
        {
            MakeWritable(path);
        }

        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_rewrite_keeps_the_destination_unix_mode(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        File.WriteAllBytes(path, Encode(1));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        var mode = UnixMode(path);

        await WriteAsync(useAsync, path, Encode(2));

        Assert.Equal(Encode(2), File.ReadAllBytes(path));
        Assert.Equal(mode, UnixMode(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_destination_that_is_a_directory_fails_fast_and_is_not_retried(bool useAsync)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        Directory.CreateDirectory(path);
        var budget = TimeSpan.FromSeconds(5);
        var options = new AtomicFileOptions { RetryBudget = budget };

        var start = Stopwatch.GetTimestamp();
        var exception = await Record.ExceptionAsync(() => WriteAsync(useAsync, path, Encode(1), options));
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.GetType().FullName);
        Assert.True(elapsed < budget / 10, $"the failure took {elapsed.TotalMilliseconds:F0} ms, so it was retried");
        Assert.True(Directory.Exists(path));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_retry_budgets_are_rejected_before_any_file_is_touched(int milliseconds)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        var options = new AtomicFileOptions { RetryBudget = TimeSpan.FromMilliseconds(milliseconds) };

        var writeException = Assert.Throws<ArgumentOutOfRangeException>(() => AtomicFile.WriteAllBytes(path, [1], options));
        var asyncException = Record.Exception(() => { _ = AtomicFile.WriteAllBytesAsync(path, new byte[] { 1 }, options); });

        Assert.Equal("options", writeException.ParamName);
        Assert.IsType<ArgumentOutOfRangeException>(asyncException);
        Assert.Equal("options", ((ArgumentOutOfRangeException)asyncException).ParamName);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Default_options_use_a_five_second_retry_budget()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new AtomicFileOptions().RetryBudget);
    }

    private static async Task WriteAsync(bool useAsync, string path, byte[] data, AtomicFileOptions? options = null, CancellationToken token = default)
    {
        if (useAsync)
        {
            await AtomicFile.WriteAllBytesAsync(path, data, options, token);
        }
        else
        {
            AtomicFile.WriteAllBytes(path, data, options, token);
        }
    }

    private static FileStream OpenLikeReader(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] Payload(int version)
    {
        var bytes = new byte[PayloadBytes];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, sizeof(int)), version);
        bytes.AsSpan(sizeof(int)).Fill((byte)version);
        return bytes;
    }

    private static bool IsWholePayload(byte[] bytes)
    {
        if (bytes.Length != PayloadBytes)
        {
            return false;
        }

        var version = BitConverter.ToInt32(bytes, 0);
        return version is >= 0 and <= RewriteCount && bytes.AsSpan(sizeof(int)).IndexOfAnyExcept((byte)version) < 0;
    }

    private static int ReadUntilStopped(string path, ManualResetEventSlim stop, ConcurrentBag<string> failures)
    {
        var completed = 0;
        do
        {
            try
            {
                var bytes = AtomicFile.ReadAllBytesShared(path);
                if (bytes is null)
                {
                    failures.Add("the file was reported missing during a rewrite");
                }
                else if (!IsWholePayload(bytes))
                {
                    failures.Add($"read {bytes.Length} bytes that are not one whole payload");
                }
                else
                {
                    completed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{exception.GetType().Name}: {exception.Message}");
            }
        }
        while (!stop.IsSet);

        return completed;
    }
}
