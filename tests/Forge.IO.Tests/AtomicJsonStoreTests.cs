using System.Diagnostics;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class AtomicJsonStoreTests
{
    private const int StoreInstanceCount = 8;
    private const int UpdatesPerInstance = 100;
    private const int ChildUpdates = 100;

    // The child process needs its runtime, the test assembly and JIT started before it can take the lock.
    private static readonly TimeSpan ChildStartAllowance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void A_missing_file_reads_as_a_new_value_and_has_no_stamp()
    {
        using var directory = new TestDirectory();
        var store = NewStore(directory.File("settings.json"));

        var state = store.Read();

        Assert.Equal(0, state.Count);
        Assert.Equal(string.Empty, state.Label);
        Assert.Null(store.Stamp());
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void A_zero_length_file_reads_as_a_new_value()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var state = NewStore(path).Read();

        Assert.Equal(0, state.Count);
        Assert.Equal(string.Empty, state.Label);
    }

    [Fact]
    public void Update_persists_the_value_that_a_fresh_instance_reads_back()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = NewStore(path);

        var updated = store.Update(state =>
        {
            state.Count = 5;
            state.Label = "alpha";
            return state;
        });

        Assert.Equal(5, updated.Count);
        var reread = NewStore(path).Read();
        Assert.Equal(5, reread.Count);
        Assert.Equal("alpha", reread.Label);
        Assert.Equal(5, Decode(File.ReadAllBytes(path)).Count);
        Assert.Empty(directory.TemporaryFiles());
    }

    [Fact]
    public void Stamp_changes_after_every_update()
    {
        using var directory = new TestDirectory();
        var store = NewStore(directory.File("settings.json"));
        Assert.Null(store.Stamp());

        store.Update(state =>
        {
            state.Count = 1;
            return state;
        });
        var first = store.Stamp();
        store.Update(state =>
        {
            state.Count = 100;
            state.Label = "changed";
            return state;
        });
        var second = store.Stamp();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(second.Value.Length > first.Value.Length, "the rewritten document is longer");
    }

    [Fact]
    public void Stamp_is_null_when_the_path_is_a_directory()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        Directory.CreateDirectory(path);

        Assert.Null(NewStore(path).Stamp());
    }

    [Fact]
    public async Task Eight_store_instances_on_one_file_lose_no_update()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var budget = UpdateSeriesLimit(directory.Path, StoreInstanceCount * UpdatesPerInstance);

        await RunUpdatesAsync(path, budget, StoreInstanceCount * UpdatesPerInstance);

        Assert.Equal(StoreInstanceCount * UpdatesPerInstance, NewStore(path).Read().Count);
    }

    [Fact]
    public async Task A_separate_process_and_eight_instances_update_one_file_without_loss()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var budget = UpdateSeriesLimit(
            directory.Path,
            StoreInstanceCount * UpdatesPerInstance + ChildUpdates,
            ChildStartAllowance);
        using var child = ChildProcess.Start(ChildProcess.IncrementMode, path, ChildUpdates);
        try
        {
            await RunUpdatesAsync(path, budget, StoreInstanceCount * UpdatesPerInstance);

            var output = await child.StandardOutput.ReadToEndAsync().WaitAsync(budget.Limit);
            await child.WaitForExitAsync().WaitAsync(budget.Limit);

            Assert.Equal(0, child.ExitCode);
            Assert.Equal(ChildProcess.DoneLine, output.Trim());
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }

        Assert.Equal(StoreInstanceCount * UpdatesPerInstance + ChildUpdates, NewStore(path).Read().Count);
        Assert.Empty(directory.TemporaryFiles());
    }

    [Fact]
    public void A_throwing_mutation_writes_nothing_and_releases_the_lock()
    {
        using var directory = new TestDirectory();
        var store = NewStore(directory.File("settings.json"));
        store.Update(state =>
        {
            state.Count = 7;
            return state;
        });
        var before = File.ReadAllBytes(store.Path);

        var exception = Assert.Throws<InvalidOperationException>(() => store.Update(_ => throw new InvalidOperationException("mutation failed")));

        Assert.Equal("mutation failed", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        using var acquired = FileLock.Acquire(store.Path + ".lock", ShortTimeout);
        Assert.NotNull(acquired);
    }

    [Fact]
    public void A_mutation_returning_null_is_an_invalid_operation_that_writes_nothing()
    {
        using var directory = new TestDirectory();
        var store = NewStore(directory.File("settings.json"));
        store.Update(state =>
        {
            state.Count = 7;
            return state;
        });
        var before = File.ReadAllBytes(store.Path);

        var exception = Assert.Throws<InvalidOperationException>(() => store.Update(_ => null!));

        Assert.Contains(store.Path, exception.Message);
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        using var acquired = FileLock.Acquire(store.Path + ".lock", ShortTimeout);
        Assert.NotNull(acquired);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Count\": \"many\"}")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("{\"Count\":")]
    [InlineData("   ")]
    public void Read_rejects_corrupt_content_and_names_the_file(string content)
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, content);
        var before = File.ReadAllBytes(path);
        var store = NewStore(path);

        var exception = Assert.Throws<InvalidDataException>(() => store.Read());

        Assert.Contains(store.Path, exception.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Count\": \"many\"}")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("{\"Count\":")]
    [InlineData("   ")]
    public void Update_rejects_corrupt_content_without_mutating_or_rewriting_the_file(string content)
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, content);
        var before = File.ReadAllBytes(path);
        var store = NewStore(path);
        var mutated = false;

        var exception = Assert.Throws<InvalidDataException>(() => store.Update(state =>
        {
            mutated = true;
            return state;
        }));

        Assert.Contains(store.Path, exception.Message);
        Assert.False(mutated);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(directory.TemporaryFiles());
        using var acquired = FileLock.Acquire(store.Path + ".lock", ShortTimeout);
        Assert.NotNull(acquired);
    }

    [Fact]
    public void A_lock_held_by_another_owner_times_out_with_a_timeout_exception()
    {
        using var directory = new TestDirectory();
        var store = NewStore(directory.File("settings.json"), ShortTimeout);
        using var holder = FileLock.Acquire(store.Path + ".lock", TestLimit);

        var exception = Assert.Throws<TimeoutException>(() => store.Update(Increment));

        Assert.Contains(store.Path + ".lock", exception.Message);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void A_pre_cancelled_read_or_update_throws_and_leaves_the_file_untouched()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllBytes(path, Encode(3));
        var before = File.ReadAllBytes(path);
        var store = NewStore(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => store.Read(cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Update(Increment, cancellation.Token));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Cancelling_while_waiting_for_the_lock_throws_and_leaves_the_file_untouched()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllBytes(path, Encode(3));
        var before = File.ReadAllBytes(path);
        var store = NewStore(path, TestLimit);
        using var holder = FileLock.Acquire(store.Path + ".lock", TestLimit);
        using var cancellation = new CancellationTokenSource(ShortTimeout);

        Assert.ThrowsAny<OperationCanceledException>(() => store.Update(Increment, cancellation.Token));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_non_positive_lock_timeout_is_rejected()
    {
        using var directory = new TestDirectory();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => NewStore(directory.File("settings.json"), TimeSpan.Zero));

        Assert.Equal("lockTimeout", exception.ParamName);
    }

    [Fact]
    public void A_non_positive_retry_budget_is_rejected()
    {
        using var directory = new TestDirectory();
        var options = new AtomicFileOptions { RetryBudget = TimeSpan.Zero };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => NewStore(directory.File("settings.json"), fileOptions: options));

        Assert.Equal("fileOptions", exception.ParamName);
    }

    [Fact]
    public void The_store_path_is_the_full_path()
    {
        var relative = Path.Combine("relative-dir", "settings.json");

        var store = NewStore(relative);

        Assert.Equal(Path.GetFullPath(relative), store.Path);
    }

    [Theory]
    [InlineData(1, 800, 0, 30)]       // a fast disk keeps the historical 30 s limit
    [InlineData(10, 800, 0, 64)]      // 10 ms per update buys 8 x 10 ms x 800 updates
    [InlineData(40, 900, 10, 120)]    // a slow disk with a child process never pushes the limit past two minutes
    public void The_update_series_limit_follows_the_measured_cost_but_stays_within_bounds(
        double perUpdateMs,
        int updates,
        double allowanceSeconds,
        double expectedSeconds)
    {
        var limit = SeriesLimit(
            TimeSpan.FromMilliseconds(perUpdateMs),
            updates,
            TimeSpan.FromSeconds(allowanceSeconds));

        Assert.Equal(expectedSeconds, limit.TotalSeconds, precision: 6);
    }

    private static TestState Increment(TestState state)
    {
        state.Count++;
        return state;
    }

    /// <summary>Runs the eight concurrent update series and fails with the measured per-update cost when they do not
    /// finish inside <paramref name="budget" />. The workers honour the cancellation token so a failed run stops
    /// writing instead of hammering the disk from behind the test.</summary>
    private static async Task RunUpdatesAsync(string path, (TimeSpan PerUpdate, TimeSpan Limit) budget, int updates)
    {
        using var stop = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, StoreInstanceCount).Select(_ => Task.Run(() =>
        {
            var store = NewStore(path, TestLimit);
            for (var update = 0; update < UpdatesPerInstance; update++)
            {
                store.Update(Increment, stop.Token);
            }
        })).ToArray();
        var all = Task.WhenAll(tasks);

        try
        {
            await all.WaitAsync(budget.Limit);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            await all.ContinueWith(_ => { }, TaskScheduler.Default);
            Assert.Fail(
                $"{updates} updates did not finish within {budget.Limit.TotalSeconds:F0} s; one update measured {budget.PerUpdate.TotalMilliseconds:F1} ms on this machine");
        }
    }
}
