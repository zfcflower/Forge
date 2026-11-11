using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class SingleInstanceLockTests
{
    [Fact]
    public void A_second_acquire_returns_null_while_the_first_is_held()
    {
        using var directory = new TestDirectory();

        using var first = SingleInstanceLock.TryAcquire(directory.Path);
        var second = SingleInstanceLock.TryAcquire(directory.Path);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void After_the_first_is_disposed_the_lock_can_be_acquired_again()
    {
        using var directory = new TestDirectory();
        var first = SingleInstanceLock.TryAcquire(directory.Path);
        Assert.NotNull(first);

        first.Dispose();

        using var again = SingleInstanceLock.TryAcquire(directory.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public void The_lock_file_lives_in_the_data_root_and_is_created_on_demand()
    {
        using var directory = new TestDirectory();
        var dataRoot = directory.File("not-yet-there");

        using var held = SingleInstanceLock.TryAcquire(dataRoot);

        Assert.NotNull(held);
        Assert.True(File.Exists(Path.Combine(dataRoot, SingleInstanceLock.LockFileName)));
    }

    [Fact]
    public void Different_data_roots_do_not_exclude_each_other()
    {
        using var directory = new TestDirectory();

        using var one = SingleInstanceLock.TryAcquire(directory.File("one"));
        using var two = SingleInstanceLock.TryAcquire(directory.File("two"));

        Assert.NotNull(one);
        Assert.NotNull(two);
    }

    [Fact]
    public async Task Another_process_holding_the_lock_makes_acquire_return_null_until_it_dies()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, SingleInstanceLock.LockFileName);
        using var child = ChildProcess.Start(ChildProcess.HoldLockMode, lockPath);
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TestLimit);
            Assert.Equal(ChildProcess.ReadyLine, ready);
            Assert.Null(SingleInstanceLock.TryAcquire(directory.Path));

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

        using var acquired = SingleInstanceLock.TryAcquire(directory.Path);
        Assert.NotNull(acquired);
    }

    [Fact]
    public void A_blank_data_root_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceLock.TryAcquire(" "));
    }
}
