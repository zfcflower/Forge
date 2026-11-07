using System.Collections;
using System.Reflection;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The per-path serialization gate: operations on one path never overlap, operations on different paths do, and the
/// table of entries does not keep the names it has finished with. The entry count is read through reflection because
/// the type exposes no internal observation point and the test project has no <c>InternalsVisibleTo</c> seam.
/// </summary>
public class PathGateTests
{
    [Fact]
    public async Task Operations_on_the_same_path_never_overlap()
    {
        var gate = new PathGate();
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("same.txt");
        var inside = 0;
        var maximum = 0;
        var completed = 0;

        async Task OperationAsync()
        {
            using var lease = await gate.AcquireAsync(path, CancellationToken.None);
            var current = Interlocked.Increment(ref inside);
            UpdateMaximum(ref maximum, current);
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            Interlocked.Increment(ref completed);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(OperationAsync))).WaitAsync(TestLimit);

        Assert.Equal(1, maximum);
        Assert.Equal(8, completed);
    }

    [Fact]
    public async Task Operations_on_different_paths_run_at_the_same_time()
    {
        // Each operation waits for the other to be inside, so a gate that serialized unrelated paths would deadlock and
        // fail on the timeout instead of passing slowly.
        var gate = new PathGate();
        using var workspace = new TemporaryWorkspace();
        var first = workspace.Full("first.txt");
        var second = workspace.Full("second.txt");
        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(first, CancellationToken.None);
            firstInside.SetResult();
            await secondInside.Task;
        });
        var b = Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(second, CancellationToken.None);
            secondInside.SetResult();
            await firstInside.Task;
        });

        await Task.WhenAll(a, b).WaitAsync(TestLimit);
    }

    [Fact]
    public async Task The_entry_table_does_not_keep_paths_after_their_last_lease()
    {
        var gate = new PathGate();
        using var workspace = new TemporaryWorkspace();
        const int PathCount = 5000;

        for (var index = 0; index < PathCount; index++)
        {
            using var lease = await gate.AcquireAsync(workspace.Full($"file-{index}.txt"), CancellationToken.None);
        }

        Assert.Equal(0, EntryCount(gate));
    }

    [Fact]
    public async Task Many_simultaneous_paths_all_leave_the_table()
    {
        var gate = new PathGate();
        using var workspace = new TemporaryWorkspace();
        const int PathCount = 500;

        await Task.WhenAll(Enumerable.Range(0, PathCount).Select(index => Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(workspace.Full($"file-{index}.txt"), CancellationToken.None);
            await Task.Yield();
        }))).WaitAsync(TestLimit);

        Assert.Equal(0, EntryCount(gate));
    }

    [Fact]
    public async Task A_cancelled_acquire_drops_the_reference_it_took()
    {
        var gate = new PathGate();
        using var workspace = new TemporaryWorkspace();
        var path = workspace.Full("contended.txt");

        var held = await gate.AcquireAsync(path, CancellationToken.None);
        Assert.Equal(1, EntryCount(gate));

        using var cancellation = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(path, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // The cancelled waiter must not keep the entry alive: one lease is still held, so the entry is listed once, and
        // releasing that lease takes it out of the table.
        Assert.Equal(1, EntryCount(gate));
        held.Dispose();
        Assert.Equal(0, EntryCount(gate));
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while ((observed = Volatile.Read(ref maximum)) < value
            && Interlocked.CompareExchange(ref maximum, value, observed) != observed)
        {
        }
    }

    /// <summary>The number of live entries, read from the private table the type reference counts.</summary>
    private static int EntryCount(PathGate gate)
    {
        var field = typeof(PathGate).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PathGate no longer has an 'entries' field; the observation point moved.");
        var table = (ICollection?)field.GetValue(gate)
            ?? throw new InvalidOperationException("PathGate.entries is null.");
        return table.Count;
    }
}
