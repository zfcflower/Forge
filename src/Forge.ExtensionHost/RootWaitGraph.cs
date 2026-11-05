namespace Forge.ExtensionHost;

/// <summary>
/// The host's wait-for graph for <em>plugin execution slot</em> queues of one root call. It records only waits that are
/// actually queued, never every active call: an execution that gets a free slot immediately registers nothing, so an
/// ordinary fan-out of one plugin to one target is neither delayed nor refused.
/// <para>
/// A node is a plugin id and an edge <c>source → target</c> means "an execution of <c>source</c> is queued for an
/// execution slot of <c>target</c> that is currently held by another call". The source is the last link of the trusted call chain (the plugin whose code made the
/// call), the target is the slot's plugin, and the edge belongs to the main program's <c>rootRunId</c>. The root is part
/// of the edge identity because the approved policy is about <em>one</em> root: sibling calls of one script queue, and
/// only two different branches of the same root that wait for each other are refused. The reserved script identity
/// (<c>$run_code</c>) is not a plugin-held execution slot, so a top-level script call registers no edge at all.
/// </para>
/// <para>
/// A new edge that would close a cycle - a self edge, <c>P→Q</c> against an existing <c>Q→P</c>, or a longer path
/// <c>Q→…→P</c> of the same root - is rejected <em>before</em> the caller waits, so two saturated branches fail one call
/// fast with a clear reason instead of both lingering until their budgets run out. This is a deliberately conservative
/// potential-cycle policy: the edge is refused even though the queue could in principle drain, because proving the
/// absence of a cycle would require knowing every future holder. It does not claim exact multi-resource deadlock
/// detection, and it never refuses a duplicate edge: <see cref="TryEnter"/> counts identical queued edges, so several
/// siblings of one branch queueing behind the same target are still one edge and still queue.
/// </para>
/// <para>
/// Edges are reference counted and removed by <see cref="WaitLease.Dispose"/> on every way a wait can end - acquire,
/// cancellation, deadline, error, revoke, slot disposal and shutdown - so a failed wait cannot leave a stale edge behind
/// that would refuse a later legitimate call. The graph has one private lock, held only for the check/register and the
/// release; no caller awaits anything while holding it, and it is never taken while a worker or plugin gate is held, so
/// it cannot introduce a new lock order.
/// </para>
/// </summary>
internal sealed class RootWaitGraph
{
    private readonly Lock sync = new();

    private readonly Dictionary<WaitEdge, int> edges = [];

    /// <summary>Bumped by <see cref="Clear"/>: a lease from an earlier generation must not decrement an edge a newer wait
    /// registered under the same key.</summary>
    private int generation;

    /// <summary>
    /// Registers one queued wait if it cannot close a cycle. Returns <c>null</c> with a reason when the edge must be
    /// refused; otherwise the returned lease must be disposed when the wait ends (however it ends).
    /// </summary>
    public WaitLease? TryEnter(string root, string source, string target, out string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);

        lock (sync)
        {
            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                reason =
                    $"'{source}' would queue for an execution slot of itself (root '{root}'); a plugin cannot wait for "
                    + "the slot its own call occupies. The call was refused as a potential wait cycle.";
                return null;
            }

            var edge = new WaitEdge(root, source, target);
            if (ReachesTarget(edge, target, source))
            {
                reason =
                    $"'{source}' is queued for plugin '{target}' while another branch of the same root is already queued "
                    + $"for '{source}' (root '{root}'); one of the two would have to finish first, so the call was "
                    + "refused as a potential wait cycle instead of waiting for its whole budget.";
                return null;
            }

            edges[edge] = edges.TryGetValue(edge, out var count) ? count + 1 : 1;
            reason = string.Empty;
            return new WaitLease(this, edge, generation);
        }
    }

    /// <summary>Forgets every queued edge; the host calls this when it begins stopping so no wait outlives the fence.</summary>
    public void Clear()
    {
        lock (sync)
        {
            edges.Clear();
            generation++;
        }
    }

    /// <summary>Depth-first walk over the queued edges of <paramref name="closing"/>'s root until <paramref name="target"/>
    /// is reached. Must run under <see cref="sync"/>; no allocation of a task or an await happens here.</summary>
    private bool ReachesTarget(WaitEdge closing, string from, string target)
    {
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        pending.Push(from);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var edge in edges.Keys)
            {
                if (!string.Equals(edge.Root, closing.Root, StringComparison.Ordinal) ||
                    !string.Equals(edge.Source, node, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(edge.Target, target, StringComparison.Ordinal))
                {
                    return true;
                }

                if (seen.Add(edge.Target))
                {
                    pending.Push(edge.Target);
                }
            }
        }

        return false;
    }

    private void Release(WaitEdge edge, int leaseGeneration)
    {
        lock (sync)
        {
            if (leaseGeneration != generation)
            {
                // The whole graph was cleared after this wait registered; the clear already dropped its edge, and any
                // edge with the same key now belongs to a newer wait that this lease must not decrement.
                return;
            }

            if (!edges.TryGetValue(edge, out var count))
            {
                // Already dropped (for example by a shutdown clear); releasing twice must never underflow the count.
                return;
            }

            if (count <= 1)
            {
                edges.Remove(edge);
            }
            else
            {
                edges[edge] = count - 1;
            }
        }
    }

    /// <summary>One root, one waiting plugin and the slot it waits for. The root is what makes two branches of the same
    /// script comparable to each other.</summary>
    internal readonly record struct WaitEdge(string Root, string Source, string Target);

    /// <summary>One registered queued wait; dispose it on every path that ends the wait.</summary>
    internal sealed class WaitLease(RootWaitGraph owner, WaitEdge edge, int generation) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(edge, generation);
            }
        }
    }
}
