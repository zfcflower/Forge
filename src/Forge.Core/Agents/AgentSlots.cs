namespace Forge.Core.Agents;

/// <summary>
/// Bounds how many agents of one host are doing their own work at the same time, counted over the whole
/// spawn tree rather than per depth. A permit is held only while the agent streams a model turn or runs
/// its tools, never while it waits for the results of its own spawns.
/// </summary>
/// <remarks>
/// The cap is mutable so a settings change reaches a live host. <see cref="SemaphoreSlim"/> cannot shrink
/// without stealing a permit or grow without inventing one, so the count lives here: one lock over an
/// outstanding counter and a FIFO of waiters. The uncontended acquire and release paths are a lock, a counter
/// and no allocation beyond the returned <see cref="AgentSlot"/>; the completion sources exist only for callers
/// that actually had to queue.
/// </remarks>
internal sealed class AgentSlotPool
{
    private readonly Lock gate = new();
    private readonly Queue<Waiter> waiters = new();
    private int limit;

    /// <summary>
    /// Permits currently owned by <see cref="AgentSlot"/> instances, including a permit already handed to a
    /// waiter that has not resumed yet. Detached slots are not counted, which is the point of detaching.
    /// </summary>
    private int outstanding;

    internal AgentSlotPool(int limit) => this.limit = Math.Max(1, limit);

    /// <summary>
    /// Takes a permit, waiting while <see cref="limit"/> agents already hold one. Cancelling while queued
    /// removes the waiter instead of handing it a permit nobody will ever release.
    /// </summary>
    public ValueTask<AgentSlot> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return TryTakePermit()
            ? new ValueTask<AgentSlot>(new AgentSlot(this))
            : AcquireSlotSlowAsync(ct);
    }

    /// <summary>Takes a permit without waiting; null when every slot is busy.</summary>
    public AgentSlot? TryAcquire()
    {
        if (!TryTakePermit())
        {
            return null;
        }

        return new AgentSlot(this);
    }

    /// <summary>
    /// Changes the cap while the host is alive. Raising it wakes the queued waiters at once; lowering it never
    /// revokes a permit already handed out, because an agent in the middle of a turn must not be interrupted.
    /// A lower cap therefore only stops new grants until the outstanding count drops below it.
    /// </summary>
    public void SetLimit(int limit)
    {
        var clamped = Math.Max(1, limit);
        lock (gate)
        {
            this.limit = clamped;
        }

        // Drain outside that first lock one waiter at a time: each grant is completed after the lock is
        // released, so a woken continuation can acquire, detach or release without ever running under it.
        while (true)
        {
            Waiter? granted;
            lock (gate)
            {
                granted = outstanding < this.limit ? TakeNextWaiter() : null;
                if (granted is not null)
                {
                    outstanding++;
                }
            }

            if (granted is null)
            {
                return;
            }

            granted.Completion.TrySetResult(true);
        }
    }

    /// <summary>
    /// Gives a permit back. When a waiter is queued and the returned permit keeps the count under the cap, the
    /// permit is transferred to it instead of being counted down and granted again, so the cap is never
    /// momentarily exceeded and the waiter wakes without a second lock on the release path.
    /// </summary>
    internal void Release()
    {
        Waiter? granted;
        lock (gate)
        {
            // The slot logic releases at most once per acquired permit; a lower count would silently raise the
            // effective cap, which is worse than the exception SemaphoreSlim raised in the same situation.
            if (outstanding == 0)
            {
                throw new InvalidOperationException("An agent slot permit was released more often than acquired.");
            }

            outstanding--;
            granted = outstanding < limit ? TakeNextWaiter() : null;
            if (granted is not null)
            {
                outstanding++;
            }
        }

        if (granted is not null)
        {
            granted.Completion.TrySetResult(true);
        }
    }

    /// <summary>
    /// Takes one permit for a slot that is re-attaching after a spawn wait: the caller already owns its
    /// <see cref="AgentSlot"/>, so this path moves the count without allocating another one.
    /// </summary>
    internal ValueTask AcquirePermitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return TryTakePermit() ? ValueTask.CompletedTask : AcquirePermitSlowAsync(ct);
    }

    private bool TryTakePermit()
    {
        lock (gate)
        {
            if (outstanding >= limit)
            {
                return false;
            }

            outstanding++;
            return true;
        }
    }

    /// <summary>
    /// Removes the next live waiter. Cancelled entries are dropped here rather than unlinked from the middle of
    /// the queue, which keeps the cancel path to one lock and no traversal. Called under <see cref="gate"/>.
    /// </summary>
    private Waiter? TakeNextWaiter()
    {
        while (waiters.Count > 0)
        {
            var waiter = waiters.Dequeue();
            if (waiter.State != WaiterState.Queued)
            {
                continue;
            }

            waiter.State = WaiterState.Granted;
            return waiter;
        }

        return null;
    }

    private async ValueTask<AgentSlot> AcquireSlotSlowAsync(CancellationToken ct)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return new AgentSlot(this);
    }

    private async ValueTask AcquirePermitSlowAsync(CancellationToken ct)
    {
        await WaitAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask WaitAsync(CancellationToken ct)
    {
        var waiter = new Waiter(this, ct);
        using (ct.Register(static state => ((Waiter)state!).Cancel(), waiter))
        {
            lock (gate)
            {
                // The token can be cancelled between the caller's fast-path check and the registration above.
                // The callback then found the waiter not yet queued and did nothing, so cancellation is
                // settled here, under the same lock that orders the queue; a permit released in the same
                // window is still taken instead of queueing behind nobody.
                ct.ThrowIfCancellationRequested();
                if (outstanding < limit)
                {
                    outstanding++;
                    return;
                }

                waiter.State = WaiterState.Queued;
                waiters.Enqueue(waiter);
            }

            await waiter.Completion.Task.ConfigureAwait(false);
        }

        // The permit was granted to this waiter while its caller was cancelling: the callback could not
        // complete the task with both results, so the permit is handed on here instead of being returned
        // inside a slot that the caller will never dispose.
        if (ct.IsCancellationRequested)
        {
            Release();
            ct.ThrowIfCancellationRequested();
        }
    }

    private enum WaiterState
    {
        New,
        Queued,
        Granted,
        Cancelled,
    }

    /// <summary>
    /// One queued acquire. The completion source publishes its result asynchronously so a granted waiter never
    /// resumes inside the granting thread's lock, and so the whole grant path runs without holding
    /// <see cref="gate"/> while completing.
    /// </summary>
    private sealed class Waiter(AgentSlotPool pool, CancellationToken ct)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WaiterState State { get; set; }

        /// <summary>
        /// Cancellation callback: a waiter still in the queue leaves its place, so the next grant skips it and no
        /// permit is reserved for it. A waiter that was already granted is left alone, because the granting side
        /// has transferred the permit to the awaiting acquire, which returns it when it sees the cancelled token.
        /// </summary>
        public void Cancel()
        {
            lock (pool.gate)
            {
                if (State != WaiterState.Queued)
                {
                    return;
                }

                State = WaiterState.Cancelled;
            }

            Completion.TrySetCanceled(ct);
        }
    }
}

/// <summary>
/// The one permit a running agent owns. <see cref="Detach"/> hands the permit back for the duration of a
/// spawn wait and the returned <see cref="Detachment"/> takes it back afterwards, so a parent awaiting
/// children is not counted as active and cannot starve them, whatever the tree looks like.
/// </summary>
internal sealed class AgentSlot : IDisposable
{
    private readonly AgentSlotPool pool;
    private readonly Lock gate = new();
    private int waits;
    private bool owned = true;

    internal AgentSlot(AgentSlotPool pool) => this.pool = pool;

    public Detachment Detach(CancellationToken ct)
    {
        var release = false;
        lock (gate)
        {
            waits++;
            if (owned)
            {
                owned = false;
                release = true;
            }
        }

        if (release)
        {
            pool.Release();
        }

        return new Detachment(this, ct);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!owned)
            {
                return;
            }

            owned = false;
        }

        pool.Release();
    }

    /// <summary>
    /// Takes the permit back once the spawn wait is over. Only the last outstanding detachment re-acquires, so a
    /// slot detached twice and attached twice ends up holding exactly one permit; the acquire can wait when the
    /// cap was lowered while the slot was detached.
    /// </summary>
    private async ValueTask AttachAsync(CancellationToken ct)
    {
        lock (gate)
        {
            waits--;
            if (waits > 0)
            {
                return;
            }
        }

        await pool.AcquirePermitAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            if (waits == 0)
            {
                owned = true;
                return;
            }
        }

        // Another detachment started while this one was re-acquiring: it owns the wait now, so the permit
        // goes back to the pool instead of being held by a slot that no one is waiting for.
        pool.Release();
    }

    internal readonly struct Detachment(AgentSlot slot, CancellationToken ct) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => slot.AttachAsync(ct);
    }
}
