using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Pins the agent slot pool's contract: the cap holds however many callers race for it, the limit can move while
/// the host is alive, and a cancelled or detached waiter never leaves a permit behind. Every step is gated on a
/// completion source or a cancellation token, so no test infers progress from a delay.
/// </summary>
public sealed class AgentSlotPoolTests
{
    /// <summary>Takes the slot <see cref="AgentSlotPool.TryAcquire"/> must hand out, failing the test otherwise.</summary>
    private static AgentSlot TakeSlot(AgentSlotPool pool) => Assert.IsType<AgentSlot>(pool.TryAcquire());

    [Fact]
    public async Task Bound_holds_when_more_callers_than_the_cap_race_for_slots()
    {
        const int capacity = 3;
        const int callers = 12;
        var pool = new AgentSlotPool(capacity);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;
        var maxInside = 0;
        var maxGate = new Lock();

        var running = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            using var slot = await pool.AcquireAsync(CancellationToken.None);
            var concurrent = Interlocked.Increment(ref inside);
            lock (maxGate)
            {
                maxInside = Math.Max(maxInside, concurrent);
            }

            if (concurrent == capacity)
            {
                filled.TrySetResult();
            }

            await release.Task;
            Interlocked.Decrement(ref inside);
        })).ToArray();

        await filled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // All permits are held, so the rest of the callers must still be queued: TryAcquire sees no free slot.
        Assert.Equal(capacity, Volatile.Read(ref inside));
        Assert.Null(pool.TryAcquire());

        release.SetResult();
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10));

        // No caller ever saw more than `capacity` agents inside at once, and the cap was reached (not idled).
        Assert.Equal(capacity, Volatile.Read(ref maxInside));
        Assert.Equal(0, Volatile.Read(ref inside));
    }

    [Fact]
    public async Task Raising_the_limit_wakes_queued_waiters_immediately()
    {
        var pool = new AgentSlotPool(1);
        var held = await pool.AcquireAsync(CancellationToken.None);
        var first = pool.AcquireAsync(CancellationToken.None).AsTask();
        var second = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        pool.SetLimit(2);

        // SetLimit commits the permit to the first waiter before it returns, so the pool already has no free
        // slot to hand out; the wait then completes without any further pool activity (no polling, no timer).
        Assert.Null(pool.TryAcquire());
        Assert.False(second.IsCompleted);
        var firstSlot = await first.WaitAsync(TimeSpan.FromSeconds(5));

        pool.SetLimit(3);
        Assert.Null(pool.TryAcquire());
        var secondSlot = await second.WaitAsync(TimeSpan.FromSeconds(5));

        firstSlot.Dispose();
        secondSlot.Dispose();
        held.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task Lowering_the_limit_keeps_handed_out_permits_and_blocks_new_grants()
    {
        var pool = new AgentSlotPool(2);
        var first = await pool.AcquireAsync(CancellationToken.None);
        var second = await pool.AcquireAsync(CancellationToken.None);

        pool.SetLimit(1);

        // Neither outstanding permit is revoked; only new grants stop.
        Assert.Null(pool.TryAcquire());
        var queued = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(queued.IsCompleted);

        // One permit comes back but the count still equals the lowered cap, so the queued caller keeps waiting.
        second.Dispose();
        Assert.False(queued.IsCompleted);
        Assert.Null(pool.TryAcquire());

        // The count drops below the cap and the released permit goes to the queued caller: it is no longer
        // free for TryAcquire, and waiting for it returns without another pool call.
        first.Dispose();
        Assert.Null(pool.TryAcquire());
        var next = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pool.TryAcquire());

        next.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task A_waiter_cancelled_while_queued_does_not_leak_a_permit()
    {
        var pool = new AgentSlotPool(1);
        var held = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var queued = pool.AcquireAsync(cts.Token).AsTask();
        Assert.False(queued.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // The cancelled waiter left the queue without taking the permit, so the pool still has exactly the permit
        // the held slot owns and the next caller queues behind it instead of finding a phantom free slot.
        Assert.Null(pool.TryAcquire());
        var next = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(next.IsCompleted);

        // The released permit is committed to the queued caller (not left free) and that wait now returns.
        held.Dispose();
        Assert.Null(pool.TryAcquire());
        var nextSlot = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pool.TryAcquire());

        nextSlot.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task Try_acquire_returns_null_at_the_cap_and_a_slot_after_a_release()
    {
        var pool = new AgentSlotPool(1);
        var slot = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());

        slot.Dispose();
        var again = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());
        again.Dispose();

        // A permit taken by TryAcquire counts against the same cap as one taken by waiting.
        var awaited = await pool.AcquireAsync(CancellationToken.None);
        Assert.Null(pool.TryAcquire());
        awaited.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_limit_below_one_still_grants_exactly_one_permit(int limit)
    {
        var pool = new AgentSlotPool(limit);
        TakeSlot(pool).Dispose();

        var slot = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());
        slot.Dispose();

        pool.SetLimit(limit);
        var clamped = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());
        clamped.Dispose();
    }

    [Fact]
    public async Task A_detached_slot_hands_its_permit_to_a_queued_waiter_and_takes_it_back()
    {
        var pool = new AgentSlotPool(1);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        var child = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(child.IsCompleted);

        // Detach returns the permit straight to the queued child, so it is not free for TryAcquire either.
        var detachment = parent.Detach(CancellationToken.None);
        Assert.Null(pool.TryAcquire());
        var childSlot = await child.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pool.TryAcquire());

        // The child still owns the only permit, so re-attaching waits instead of over-filling the cap.
        var attach = detachment.DisposeAsync().AsTask();
        Assert.False(attach.IsCompleted);

        childSlot.Dispose();
        await attach.WaitAsync(TimeSpan.FromSeconds(5));

        // The permit is back with the parent, which is the only owner under the cap again.
        Assert.Null(pool.TryAcquire());
        parent.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task A_slot_detached_twice_takes_its_permit_back_once()
    {
        var pool = new AgentSlotPool(1);
        var slot = await pool.AcquireAsync(CancellationToken.None);

        var first = slot.Detach(CancellationToken.None);
        var second = slot.Detach(CancellationToken.None);

        // Only the first detachment released the permit: exactly one is available, not two.
        TakeSlot(pool).Dispose();

        await first.DisposeAsync();

        // One detachment is still outstanding, so the slot has not re-attached and the permit stays free.
        TakeSlot(pool).Dispose();

        await second.DisposeAsync();
        Assert.Null(pool.TryAcquire());

        slot.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task Re_attaching_after_the_limit_dropped_waits_for_a_free_permit()
    {
        var pool = new AgentSlotPool(2);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        var other = await pool.AcquireAsync(CancellationToken.None);
        var detachment = parent.Detach(CancellationToken.None);

        pool.SetLimit(1);

        // The detached parent is not revoked, but one outstanding permit already fills the new cap.
        var attach = detachment.DisposeAsync().AsTask();
        Assert.False(attach.IsCompleted);

        other.Dispose();
        await attach.WaitAsync(TimeSpan.FromSeconds(5));

        // The re-attached parent owns the only permit under the new cap.
        Assert.Null(pool.TryAcquire());
        parent.Dispose();
        TakeSlot(pool).Dispose();
    }

    [Fact]
    public async Task A_raise_while_a_slot_is_detached_is_not_reserved_for_the_re_attach()
    {
        var pool = new AgentSlotPool(1);
        var parent = await pool.AcquireAsync(CancellationToken.None);
        var child = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(child.IsCompleted);

        // Detaching hands the only permit to the queued child, so the raise below starts with no detached slot
        // that still owns a permit.
        var detachment = parent.Detach(CancellationToken.None);
        var childSlot = await child.WaitAsync(TimeSpan.FromSeconds(5));

        pool.SetLimit(2);

        // The raise adds a second permit and an ordinary caller keeps it: the detached slot has no claim on it.
        var raised = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());

        // Re-attaching competes like any other caller instead of over-filling the raised cap.
        var attach = detachment.DisposeAsync().AsTask();
        Assert.False(attach.IsCompleted);

        raised.Dispose();
        await attach.WaitAsync(TimeSpan.FromSeconds(5));

        // The parent took the permit that was just given up; with the child's permit that is the whole raised cap.
        Assert.Null(pool.TryAcquire());

        parent.Dispose();
        childSlot.Dispose();
        var firstFree = TakeSlot(pool);
        var secondFree = TakeSlot(pool);
        Assert.Null(pool.TryAcquire());
        firstFree.Dispose();
        secondFree.Dispose();
    }

    [Fact]
    public async Task A_lowered_limit_raised_again_wakes_the_queued_waiters_in_order()
    {
        var pool = new AgentSlotPool(2);
        var first = await pool.AcquireAsync(CancellationToken.None);
        var second = await pool.AcquireAsync(CancellationToken.None);
        var queuedFirst = pool.AcquireAsync(CancellationToken.None).AsTask();
        var queuedSecond = pool.AcquireAsync(CancellationToken.None).AsTask();
        var queuedThird = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(queuedFirst.IsCompleted);
        Assert.False(queuedSecond.IsCompleted);
        Assert.False(queuedThird.IsCompleted);

        pool.SetLimit(1);

        // Lowering the cap below the outstanding count grants nothing, not even to the head of the queue.
        Assert.False(queuedFirst.IsCompleted);
        Assert.False(queuedSecond.IsCompleted);
        Assert.False(queuedThird.IsCompleted);

        // Raising it to three grants exactly the head: two permits are outstanding, so one waiter fits.
        pool.SetLimit(3);
        var firstSlot = await queuedFirst.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queuedSecond.IsCompleted);
        Assert.False(queuedThird.IsCompleted);
        Assert.Null(pool.TryAcquire());

        // Every release wakes the next waiter in queue order, never the last one first.
        first.Dispose();
        var secondSlot = await queuedSecond.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queuedThird.IsCompleted);
        Assert.Null(pool.TryAcquire());

        second.Dispose();
        var thirdSlot = await queuedThird.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pool.TryAcquire());

        firstSlot.Dispose();
        secondSlot.Dispose();
        thirdSlot.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task A_limit_below_one_grants_one_permit_and_makes_the_next_caller_wait(int limit)
    {
        var pool = new AgentSlotPool(4);
        pool.SetLimit(limit);

        var only = await pool.AcquireAsync(CancellationToken.None);
        var queued = pool.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.Null(pool.TryAcquire());

        only.Dispose();
        var next = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(pool.TryAcquire());

        next.Dispose();
        TakeSlot(pool).Dispose();
    }

    /// <summary>
    /// The permit of a grant that lands while its waiter is cancelling goes back to the pool: granting and cancelling race on
    /// purpose, on this thread or from another one (the settings are applied from the UI thread while the agent is elsewhere),
    /// and every outcome has to leave exactly the permits that were actually acquired.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancel_racing_a_grant_returns_the_permit_instead_of_losing_it(bool grantOffThisThread)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var pool = new AgentSlotPool(1);
            var held = TakeSlot(pool);
            using var cts = new CancellationTokenSource();
            var queued = pool.AcquireAsync(cts.Token).AsTask();
            Assert.False(queued.IsCompleted);

            // Either the acquire sees the token and hands the permit back, or it returns the slot to us. Both must leave the
            // pool with the permits that were really acquired, so a permit kept by a cancelled grant shows up as a missing
            // free slot and a release without a grant as a phantom one.
            if (grantOffThisThread)
            {
                var grant = Task.Run(() => pool.SetLimit(2));
                cts.Cancel();
                await grant.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                pool.SetLimit(2);
                cts.Cancel();
            }

            AgentSlot? granted = null;
            try
            {
                granted = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }

            granted?.Dispose();
            held.Dispose();

            // Back to a cap of one with nothing outstanding: exactly one permit must be available, no phantom.
            pool.SetLimit(1);
            var probe = TakeSlot(pool);
            Assert.Null(pool.TryAcquire());
            probe.Dispose();
        }
    }
}
