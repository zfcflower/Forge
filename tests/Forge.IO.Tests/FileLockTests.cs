using System.Diagnostics;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class FileLockTests
{
    private const int ContendingTaskCount = 8;
    private const int SectionsPerTask = 25;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Eight_contending_tasks_never_overlap_in_the_critical_section()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        var inside = 0;
        var overlaps = 0;
        var counter = 0;

        var tasks = Enumerable.Range(0, ContendingTaskCount).Select(_ => Task.Run(() =>
        {
            for (var section = 0; section < SectionsPerTask; section++)
            {
                using var held = FileLock.Acquire(lockPath, TestLimit);
                if (Interlocked.Increment(ref inside) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                counter++;
                Interlocked.Decrement(ref inside);
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TestLimit);

        Assert.Equal(0, Volatile.Read(ref overlaps));
        Assert.Equal(ContendingTaskCount * SectionsPerTask, counter);
    }

    [Fact]
    public void A_contended_acquire_past_its_timeout_throws_naming_the_lock_path()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        using var holder = FileLock.Acquire(lockPath, TestLimit);

        var start = Stopwatch.GetTimestamp();
        var exception = Assert.Throws<TimeoutException>(() => FileLock.Acquire(lockPath, ShortTimeout));
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.Contains(Path.GetFullPath(lockPath), exception.Message);
        Assert.True(elapsed >= ShortTimeout, $"gave up after {elapsed.TotalMilliseconds:F0} ms, before the timeout");
        Assert.True(elapsed < ShortTimeout + TimeSpan.FromSeconds(2), $"gave up after {elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Cancelling_a_contended_acquire_throws_operation_canceled()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        using var holder = FileLock.Acquire(lockPath, TestLimit);
        using var cancellation = new CancellationTokenSource(ShortTimeout);

        var start = Stopwatch.GetTimestamp();
        Assert.ThrowsAny<OperationCanceledException>(() => FileLock.Acquire(lockPath, TestLimit, cancellation.Token));
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"the cancelled acquire returned after {elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void A_pre_cancelled_acquire_throws_and_leaves_the_lock_free()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FileLock.Acquire(lockPath, TestLimit, cancellation.Token));

        using var acquired = FileLock.Acquire(lockPath, ShortTimeout);
        Assert.NotNull(acquired);
    }

    [Fact]
    public void Dispose_releases_the_lock_and_keeps_the_file()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        var first = FileLock.Acquire(lockPath, TestLimit);

        first.Dispose();

        Assert.True(File.Exists(lockPath));
        using var second = FileLock.Acquire(lockPath, ShortTimeout);
        Assert.NotNull(second);
    }

    [Fact]
    public void A_repeated_dispose_does_not_release_a_lock_now_held_by_another_owner()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        var first = FileLock.Acquire(lockPath, TestLimit);
        first.Dispose();
        using var second = FileLock.Acquire(lockPath, ShortTimeout);

        first.Dispose();

        Assert.Throws<TimeoutException>(() => FileLock.Acquire(lockPath, ShortTimeout));
    }

    [Fact]
    public void Acquire_creates_a_missing_lock_directory()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "nested", "state.lock");

        using var held = FileLock.Acquire(lockPath, TestLimit);

        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void A_negative_timeout_is_rejected()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => FileLock.Acquire(lockPath, TimeSpan.FromMilliseconds(-1)));

        Assert.Equal("timeout", exception.ParamName);
    }

    [Fact]
    public async Task The_lock_becomes_available_when_the_process_holding_it_is_killed()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("state.lock");
        using var child = ChildProcess.Start(ChildProcess.HoldLockMode, lockPath);
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TestLimit);
            Assert.Equal(ChildProcess.ReadyLine, ready);
            Assert.Throws<TimeoutException>(() => FileLock.Acquire(lockPath, ShortTimeout));

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TestLimit);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }

        using var acquired = FileLock.Acquire(lockPath, TestLimit);
        Assert.NotNull(acquired);
    }
}
