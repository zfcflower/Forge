using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Cancelled waiters leave a saturated <see cref="AgentSlotPool"/> queue instead of piling up, the live ones keep their order,
/// and no permit is lost or invented by cancelling, detaching or moving the cap. Progress is gated on tasks and tokens only.
/// </summary>
public sealed class AgentSlotPoolCompactionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static AgentSlot TakeSlot(AgentSlotPool pool) => Assert.IsType<AgentSlot>(pool.TryAcquire());

    /// <summary>Takes permits until none is free; the count is the permits not owned by anyone.</summary>
    private static int FreePermits(AgentSlotPool pool)
    {
        var held = new List<AgentSlot>();
        while (pool.TryAcquire() is { } slot)
        {
            held.Add(slot);
            Assert.True(held.Count <= 64, "the pool handed out more permits than any test cap allows");
        }

        foreach (var slot in held)
        {
            slot.Dispose();
        }

        return held.Count;
    }

    [Fact]
    public async Task Set_limit_reports_whether_the_cap_moved()
    {
        var pool = new AgentSlotPool(3);
        Assert.Equal(3, pool.Limit);

        Assert.False(pool.SetLimit(3));
        Assert.True(pool.SetLimit(5));
        Assert.Equal(5, pool.Limit);
        Assert.False(pool.SetLimit(5));
        Assert.True(pool.SetLimit(2));
        Assert.Equal(2, pool.Limit);

        // Values below one clamp to one, so a second clamped call is not a move.
        Assert.True(pool.SetLimit(0));
        Assert.Equal(1, pool.Limit);
        Assert.False(pool.SetLimit(-4));
        Assert.Equal(1, pool.Limit);

        // A move that grants nothing still reports the move.
        var held = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(pool.SetLimit(2));
        Assert.Equal(2, pool.Limit);
        held.Dispose();
    }

    [Fact]
    public async Task Cancelling_many_queued_waiters_keeps_the_queue_bounded_and_a_release_still_grants_the_live_ones_in_order()
    {
        var pool = new AgentSlotPool(1);
        var held = TakeSlot(pool);
        using var cts = new CancellationTokenSource();

        var liveFirst = pool.AcquireAsync(CancellationToken.None).AsTask();
        var cancelled = new List<Task<AgentSlot>>();
        for (var i = 0; i < 200; i++)
        {
            cancelled.Add(pool.AcquireAsync(cts.Token).AsTask());
        }

        var liveSecond = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.Equal(202, pool.QueuedEntries);

        cts.Cancel();
        foreach (var task in cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
        }

        // 200 dead entries did not stay behind: compaction ran, so only the two live waiters plus a sub-threshold remainder do.
        Assert.InRange(pool.QueuedEntries, 2, 2 + 31);
        Assert.False(liveFirst.IsCompleted);
        Assert.False(liveSecond.IsCompleted);

        held.Dispose();
        var firstSlot = await liveFirst.WaitAsync(Timeout);
        Assert.False(liveSecond.IsCompleted);
        Assert.Null(pool.TryAcquire());

        firstSlot.Dispose();
        var secondSlot = await liveSecond.WaitAsync(Timeout);
        Assert.Null(pool.TryAcquire());

        secondSlot.Dispose();

        // No permit leaked and none was invented: exactly the cap is takeable again.
        Assert.Equal(1, FreePermits(pool));
        Assert.Equal(0, pool.QueuedEntries);
    }

    [Fact]
    public async Task Cancelling_every_queued_waiter_leaves_a_small_queue_and_a_pool_that_still_works()
    {
        var pool = new AgentSlotPool(1);
        var held = TakeSlot(pool);
        using var cts = new CancellationTokenSource();
        var queued = Enumerable.Range(0, 200).Select(_ => pool.AcquireAsync(cts.Token).AsTask()).ToArray();

        cts.Cancel();
        foreach (var task in queued)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
        }

        Assert.InRange(pool.QueuedEntries, 0, 31);
        Assert.Null(pool.TryAcquire());

        held.Dispose();
        Assert.Equal(1, FreePermits(pool));
        var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.Null(pool.TryAcquire());
        next.Dispose();
        Assert.Equal(1, FreePermits(pool));
    }

    [Fact]
    public async Task Cancelled_entries_below_the_threshold_are_skipped_by_grants()
    {
        var pool = new AgentSlotPool(1);
        var held = TakeSlot(pool);
        using var cancelFirst = new CancellationTokenSource();
        using var cancelThird = new CancellationTokenSource();

        var a = pool.AcquireAsync(cancelFirst.Token).AsTask();
        var b = pool.AcquireAsync(CancellationToken.None).AsTask();
        var c = pool.AcquireAsync(cancelThird.Token).AsTask();
        var d = pool.AcquireAsync(CancellationToken.None).AsTask();

        cancelFirst.Cancel();
        cancelThird.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c).WaitAsync(Timeout);

        // Two dead entries is far below the compaction threshold: they are still queued and the grant has to step over them.
        Assert.Equal(4, pool.QueuedEntries);

        held.Dispose();
        var bSlot = await b.WaitAsync(Timeout);
        Assert.False(d.IsCompleted);
        Assert.Null(pool.TryAcquire());

        bSlot.Dispose();
        var dSlot = await d.WaitAsync(Timeout);
        Assert.Null(pool.TryAcquire());
        Assert.Equal(0, pool.QueuedEntries);

        dSlot.Dispose();
        Assert.Equal(1, FreePermits(pool));
    }

    [Fact]
    public async Task Cancelled_entries_are_also_skipped_by_a_raise()
    {
        var pool = new AgentSlotPool(1);
        var held = TakeSlot(pool);
        using var cts = new CancellationTokenSource();
        var dead = pool.AcquireAsync(cts.Token).AsTask();
        var live = pool.AcquireAsync(CancellationToken.None).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dead).WaitAsync(Timeout);

        Assert.True(pool.SetLimit(2));

        var slot = await live.WaitAsync(Timeout);
        Assert.Null(pool.TryAcquire());
        slot.Dispose();
        held.Dispose();
        Assert.Equal(2, FreePermits(pool));
    }

    [Fact]
    public async Task Cancelling_a_queued_re_attach_leaves_the_permits_consistent()
    {
        var pool = new AgentSlotPool(1);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var detachment = parent.Detach(cts.Token);
        var taker = TakeSlot(pool);

        var attach = detachment.DisposeAsync().AsTask();
        Assert.False(attach.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach).WaitAsync(Timeout);

        // The parent never got its permit back, so disposing it must not release one it does not own.
        parent.Dispose();
        Assert.Null(pool.TryAcquire());

        taker.Dispose();
        Assert.Equal(1, FreePermits(pool));

        // The pool still serves new acquirers normally afterwards.
        var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.Null(pool.TryAcquire());
        next.Dispose();
        Assert.Equal(1, FreePermits(pool));
    }

    [Fact]
    public async Task A_re_attach_with_an_already_cancelled_token_fails_without_touching_the_permits()
    {
        var pool = new AgentSlotPool(1);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var detachment = parent.Detach(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await detachment.DisposeAsync());

        parent.Dispose();
        Assert.Equal(1, FreePermits(pool));
    }

    [Fact]
    public async Task A_raise_from_one_serves_new_acquirers_before_a_queued_re_attach_and_then_the_re_attach()
    {
        var pool = new AgentSlotPool(1);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        var detachment = parent.Detach(CancellationToken.None);

        // The detach freed the only permit: a new agent takes it, another queues, and the re-attach queues behind both.
        var running = TakeSlot(pool);
        var newcomer = pool.AcquireAsync(CancellationToken.None).AsTask();
        var attach = detachment.DisposeAsync().AsTask();
        Assert.False(newcomer.IsCompleted);
        Assert.False(attach.IsCompleted);

        pool.SetLimit(2);
        var newcomerSlot = await newcomer.WaitAsync(Timeout);
        Assert.False(attach.IsCompleted);
        Assert.Null(pool.TryAcquire());

        pool.SetLimit(3);
        await attach.WaitAsync(Timeout);

        // Parent, running and newcomer own the whole raised cap; nothing is left over and nothing was granted twice.
        Assert.Null(pool.TryAcquire());

        parent.Dispose();
        running.Dispose();
        newcomerSlot.Dispose();
        Assert.Equal(3, FreePermits(pool));
    }

    [Fact]
    public async Task Nested_spawns_with_a_moving_cap_finish_without_deadlock_or_permit_leak()
    {
        var pool = new AgentSlotPool(1);
        using var stop = new CancellationTokenSource();

        // Moves the cap between 1 and 3 for the whole run, so parents are detached while the cap shrinks and grows.
        var mover = Task.Run(async () =>
        {
            var flip = 0;
            while (!stop.IsCancellationRequested)
            {
                pool.SetLimit(flip++ % 2 == 0 ? 1 : 3);
                await Task.Yield();
            }
        });

        var leaves = 0;
        async Task Agent(int depth)
        {
            using var slot = await pool.AcquireAsync(CancellationToken.None);
            await Task.Yield();
            if (depth >= 3)
            {
                Interlocked.Increment(ref leaves);
                return;
            }

            var detachment = slot.Detach(CancellationToken.None);
            try
            {
                await Task.WhenAll(Agent(depth + 1), Agent(depth + 1));
            }
            finally
            {
                await detachment.DisposeAsync();
            }

            await Task.Yield();
        }

        var roots = Enumerable.Range(0, 3).Select(_ => Task.Run(() => Agent(0))).ToArray();
        try
        {
            await Task.WhenAll(roots).WaitAsync(Timeout);
        }
        finally
        {
            await stop.CancelAsync();
            await mover;
        }

        // 3 roots, each a binary tree of depth 3: 8 leaves apiece.
        Assert.Equal(24, leaves);
        pool.SetLimit(4);
        Assert.Equal(4, FreePermits(pool));
        Assert.Equal(0, pool.QueuedEntries);
    }
}
