namespace Forge.Core.Agents;

/// <summary>
/// Bounds how many agents of one host are doing their own work at the same time, counted over the whole
/// spawn tree rather than per depth. A permit is held only while the agent streams a model turn or runs
/// its tools, never while it waits for the results of its own spawns.
/// </summary>
internal sealed class AgentSlotPool(int limit)
{
    private readonly SemaphoreSlim permits = new(Math.Max(1, limit), Math.Max(1, limit));

    public async ValueTask<AgentSlot> AcquireAsync(CancellationToken ct)
    {
        await permits.WaitAsync(ct).ConfigureAwait(false);
        return new AgentSlot(permits);
    }
}

/// <summary>
/// The one permit a running agent owns. <see cref="Detach"/> hands the permit back for the duration of a
/// spawn wait and the returned <see cref="Detachment"/> takes it back afterwards, so a parent awaiting
/// children is not counted as active and cannot starve them, whatever the tree looks like.
/// </summary>
internal sealed class AgentSlot : IDisposable
{
    private readonly SemaphoreSlim permits;
    private readonly Lock gate = new();
    private int waits;
    private bool owned = true;

    internal AgentSlot(SemaphoreSlim permits) => this.permits = permits;

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
            permits.Release();
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

        permits.Release();
    }

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

        await permits.WaitAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            if (waits == 0)
            {
                owned = true;
                return;
            }
        }

        permits.Release();
    }

    internal readonly struct Detachment(AgentSlot slot, CancellationToken ct) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => slot.AttachAsync(ct);
    }
}
