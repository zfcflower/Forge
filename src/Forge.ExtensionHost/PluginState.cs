namespace Forge.ExtensionHost;

/// <summary>
/// Per-plugin state that outlives any single worker process and every refresh of the plugin's entry: it is keyed by
/// plugin id and never discarded, so a request that waits on its gate always waits on the gate that every approval
/// change and every refresh takes for that id. The gate serializes approval changes (approve/revoke/refresh) against
/// the approval-check-through-dispatch section of an execution, so a revoke can never overlap the moment a call decides
/// to run and start a worker. Executions register a lease before they leave that section, which is what lets a revoke
/// wait until nothing is in flight any more.
/// <para>
/// The leases also carry the root execution each call belongs to. The root is the main program's <c>rootRunId</c> call
/// token, never a plugin name: two independent scripts, or two independent calls of the same plugin, are two roots and may
/// run side by side. A nested call whose target is already running for a <em>different</em> root is refused as
/// <c>plugin_busy</c> instead of waiting: two root chains that call into each other (A→B and B→A) would otherwise be able
/// to block on one another indefinitely. Calls of the same root - sibling sub-calls of one script or one tool execution -
/// queue for a slot as before.
/// </para>
/// <para>
/// The gate is therefore root-aware, not a plain mutex. A nested call whose own root already owns the gate is admitted
/// under that owner (the owner is the root token, counted), so siblings of one root never fail against each other while
/// their shared root is hashing, loading or publishing; a nested call of a different root - and any nested call while an
/// approval change, a refresh or a queued exclusive caller holds or waits for the gate - is refused at once with
/// <c>plugin_busy</c>. Admission and refusal are decided in one step under this object's lock, never as a "look, then
/// wait" pair, so a nested call cannot end up queued behind a different root. Approval changes and top-level executions
/// take the gate exclusively and wait (cancellably) in arrival order; because no nested caller ever queues, an exclusive
/// waiter can only be delayed by the sections that already ran, never by a stream of re-entering siblings.
/// </para>
/// </summary>
internal sealed class PluginState
{
    /// <summary>Abnormal worker events a plugin may accumulate before it is refused until <c>plugins.refresh</c>.</summary>
    public const int FailureBudget = 4;

    private readonly Lock sync = new();

    private readonly Dictionary<string, int> activeByRoot = new(StringComparer.Ordinal);

    private readonly LinkedList<Waiter> waiters = new();

    private Holder holder = Holder.Free;

    private string holderRoot = string.Empty;

    private int depth;

    private int inFlight;

    private int failures;

    private long loadGeneration;

    private TaskCompletionSource? idle;

    /// <summary>Abnormal worker events charged since the last refresh or completed tool call.</summary>
    public int Failures => Volatile.Read(ref failures);

    public bool IsExhausted => Volatile.Read(ref failures) >= FailureBudget;

    /// <summary>Who holds the plugin gate: nobody, one root's dispatch section, or an exclusive caller.</summary>
    private enum Holder
    {
        Free = 0,
        Root = 1,
        Exclusive = 2,
    }

    /// <summary>
    /// Takes the plugin gate exclusively: used by <c>plugin.approve</c>/<c>plugin.revoke</c>/<c>plugins.refresh</c> and by
    /// a top-level execution. Waiters are served in arrival order and can cancel while they wait.
    /// </summary>
    public Task EnterAsync(CancellationToken ct)
    {
        lock (sync)
        {
            if (holder == Holder.Free && waiters.Count == 0)
            {
                holder = Holder.Exclusive;
                depth = 1;
                return Task.CompletedTask;
            }
        }

        return EnterQueuedAsync(ct);
    }

    /// <summary>
    /// Admits a nested execution whose root already owns the gate; refuses (returns <c>false</c>) when the gate is held by
    /// an approval change, a load of another root, a queued exclusive caller, or when it is free but an exclusive caller is
    /// already waiting. The decision and the ownership update happen together under this object's lock, so a nested call
    /// is never handed a "wait for it" answer that could put it behind a different root.
    /// </summary>
    public bool TryEnterNested(string root)
    {
        lock (sync)
        {
            if (holder == Holder.Root &&
                depth > 0 &&
                waiters.Count == 0 &&
                string.Equals(holderRoot, root, StringComparison.Ordinal))
            {
                depth++;
                return true;
            }

            if (holder == Holder.Free && waiters.Count == 0)
            {
                holder = Holder.Root;
                holderRoot = root;
                depth = 1;
                return true;
            }

            return false;
        }
    }

    /// <summary>Leaves the gate; the last holder out hands it to the next exclusive waiter, if any.</summary>
    public void Exit()
    {
        Waiter? next = null;
        lock (sync)
        {
            if (depth <= 0)
            {
                throw new InvalidOperationException("The plugin gate was released more often than it was taken.");
            }

            if (--depth > 0)
            {
                return;
            }

            holder = Holder.Free;
            holderRoot = string.Empty;
            next = GrantNext();
        }

        next?.Completion.TrySetResult();
    }

    /// <summary>
    /// Allocates the next content generation of this plugin: a number that grows every time a worker actually loads a
    /// plugin. A publication carries the generation of the load it describes, so a delayed notification from an older load
    /// can be recognized and dropped instead of overwriting a newer snapshot.
    /// </summary>
    public long NextLoadGeneration() => Interlocked.Increment(ref loadGeneration);

    /// <summary>The generation of the newest load of this plugin, or 0 when no worker has loaded it yet.</summary>
    public long CurrentLoadGeneration => Interlocked.Read(ref loadGeneration);

    /// <summary>Charges one abnormal worker event and returns the new total.</summary>
    public int ChargeFailure() => Interlocked.Increment(ref failures);

    /// <summary>A tool call that actually completed clears the budget; a successful start or load does not.</summary>
    public void RecordSuccess() => Interlocked.Exchange(ref failures, 0);

    public void ResetFailures() => Interlocked.Exchange(ref failures, 0);

    /// <summary>
    /// Marks one dispatched execution; dispose the result when the call ends. The root is the root run token of the call.
    /// A nested call whose target is already running for another root is refused with <c>plugin_busy</c> before it is
    /// registered; a sibling of the same root is registered and queues for a worker slot like any other call.
    /// </summary>
    public Lease BeginExecution(string pluginId, string root, bool nested)
    {
        lock (sync)
        {
            if (nested)
            {
                foreach (var (otherRoot, count) in activeByRoot)
                {
                    if (count > 0 && !string.Equals(otherRoot, root, StringComparison.Ordinal))
                    {
                        throw new HostOperationException(
                            "plugin_busy",
                            $"Plugin '{pluginId}' is already running for root '{otherRoot}'; a nested call does not wait for it "
                            + "(that would let two call chains block on each other). Retry after the other call finishes.");
                    }
                }
            }

            inFlight++;
            activeByRoot[root] = activeByRoot.TryGetValue(root, out var active) ? active + 1 : 1;
            return new Lease(this, root);
        }
    }

    /// <summary>Waits until no execution registered by <see cref="BeginExecution"/> is running, or the bound elapses.</summary>
    public async Task WaitForIdleAsync(TimeSpan bound, CancellationToken ct)
    {
        Task pending;
        lock (sync)
        {
            if (inFlight == 0)
            {
                return;
            }

            idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = idle.Task;
        }

        await Task.WhenAny(pending, Task.Delay(bound, ct)).ConfigureAwait(false);
    }

    private async Task EnterQueuedAsync(CancellationToken ct)
    {
        var waiter = new Waiter(this);
        CancellationTokenRegistration registration = default;
        lock (sync)
        {
            // Rechecked under the lock: the gate may have become free between the first check and here, and queueing
            // behind nobody would waste a wakeup.
            if (holder == Holder.Free && waiters.Count == 0)
            {
                holder = Holder.Exclusive;
                depth = 1;
                return;
            }

            waiter.Node = waiters.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            registration = ct.Register(static state => ((Cancellation)state!).Cancel(), new Cancellation(waiter, ct));
        }

        try
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Hands the free gate to the oldest waiter that is still waiting; cancelled waiters are skipped. Must run
    /// under <see cref="sync"/>.</summary>
    private Waiter? GrantNext()
    {
        while (waiters.First is { } first)
        {
            waiters.RemoveFirst();
            var candidate = first.Value;
            candidate.Node = null;
            if (candidate.Completion.Task.IsCompleted)
            {
                continue;
            }

            candidate.Granted = true;
            holder = Holder.Exclusive;
            depth = 1;
            return candidate;
        }

        return null;
    }

    /// <summary>Withdraws a waiter that has not been granted yet; a grant that already happened is not undone.</summary>
    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        lock (sync)
        {
            if (waiter.Granted)
            {
                return;
            }

            if (waiter.Node is { } node)
            {
                waiters.Remove(node);
                waiter.Node = null;
            }
        }

        waiter.Completion.TrySetCanceled(ct);
    }

    private void EndExecution(string root)
    {
        TaskCompletionSource? complete = null;
        lock (sync)
        {
            inFlight--;
            if (activeByRoot.TryGetValue(root, out var count))
            {
                if (count <= 1)
                {
                    activeByRoot.Remove(root);
                }
                else
                {
                    activeByRoot[root] = count - 1;
                }
            }

            if (inFlight == 0 && idle is { } waiting)
            {
                complete = waiting;
                idle = null;
            }
        }

        complete?.TrySetResult();
    }

    /// <summary>One exclusive slot in the arrival queue of the plugin gate.</summary>
    private sealed class Waiter(PluginState owner)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public bool Granted { get; set; }

        public void Cancel(CancellationToken ct) => owner.Cancel(this, ct);
    }

    /// <summary>Cancellation callback state: the waiter plus the token it registered with.</summary>
    private sealed class Cancellation(Waiter waiter, CancellationToken ct)
    {
        public void Cancel() => waiter.Cancel(ct);
    }

    /// <summary>One dispatched execution; disposing it takes the execution out of the in-flight set.</summary>
    internal readonly struct Lease(PluginState state, string root) : IDisposable
    {
        public void Dispose() => state.EndExecution(root);
    }
}
