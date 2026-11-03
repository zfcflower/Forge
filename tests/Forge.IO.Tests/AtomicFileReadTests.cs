using System.Collections.Concurrent;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class AtomicFileReadTests
{
    private const int ReplaceCount = 500;
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void A_missing_file_reads_as_null()
    {
        using var directory = new TestDirectory();

        Assert.Null(AtomicFile.ReadAllBytesShared(directory.File("absent.json")));
    }

    [Fact]
    public void An_empty_file_reads_as_an_empty_array()
    {
        using var directory = new TestDirectory();
        var path = directory.File("empty.json");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var bytes = AtomicFile.ReadAllBytesShared(path);

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }

    [Fact]
    public void A_read_returns_the_whole_file_across_buffer_boundaries()
    {
        using var directory = new TestDirectory();
        var path = directory.File("large.bin");
        var content = new byte[100 * 1024 + 7];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index * 31);
        }

        File.WriteAllBytes(path, content);

        Assert.Equal(content, AtomicFile.ReadAllBytesShared(path));
    }

    [Fact]
    public void A_completed_read_releases_its_handle_so_the_next_replace_is_immediate()
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        AtomicFile.WriteAllBytes(path, Encode(1));
        Assert.NotNull(AtomicFile.ReadAllBytesShared(path));
        var options = new AtomicFileOptions { RetryBudget = ShortBudget };

        AtomicFile.WriteAllBytes(path, Encode(2), options);

        Assert.Equal(2, Decode(File.ReadAllBytes(path)).Count);
        Assert.Empty(directory.TemporaryFiles());
    }

    [Fact]
    public async Task A_reader_racing_rapid_replaces_never_reports_missing_data()
    {
        using var directory = new TestDirectory();
        var path = directory.File("counter.bin");
        AtomicFile.WriteAllBytes(path, BitConverter.GetBytes(0));
        var failures = new ConcurrentBag<string>();
        using var stop = new ManualResetEventSlim(false);
        var reader = Task.Run(() =>
        {
            var reads = 0;
            do
            {
                try
                {
                    var bytes = AtomicFile.ReadAllBytesShared(path);
                    if (bytes is null)
                    {
                        failures.Add("the file was reported missing while it was being replaced");
                    }
                    else if (bytes.Length != sizeof(int) || BitConverter.ToInt32(bytes) is < 0 or > ReplaceCount)
                    {
                        failures.Add($"read {bytes.Length} bytes that are not a counter value");
                    }
                    else
                    {
                        reads++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{exception.GetType().Name}: {exception.Message}");
                }
            }
            while (!stop.IsSet);

            return reads;
        });

        try
        {
            for (var value = 1; value <= ReplaceCount; value++)
            {
                AtomicFile.WriteAllBytes(path, BitConverter.GetBytes(value));
            }
        }
        finally
        {
            stop.Set();
        }

        var reads = await reader.WaitAsync(TestLimit);

        Assert.Empty(failures);
        Assert.True(reads > 0, "the reader completed no reads");
        Assert.Equal(ReplaceCount, BitConverter.ToInt32(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pre_cancelled_read_throws_whether_or_not_the_file_exists(bool exists)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        if (exists)
        {
            File.WriteAllBytes(path, Encode(1));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AtomicFile.ReadAllBytesShared(path, cancellationToken: cancellation.Token));
        Assert.Equal(exists, File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_retry_budgets_are_rejected_for_reads(int milliseconds)
    {
        using var directory = new TestDirectory();
        var path = directory.File("state.json");
        File.WriteAllBytes(path, Encode(1));
        var options = new AtomicFileOptions { RetryBudget = TimeSpan.FromMilliseconds(milliseconds) };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => AtomicFile.ReadAllBytesShared(path, options));

        Assert.Equal("options", exception.ParamName);
    }
}
