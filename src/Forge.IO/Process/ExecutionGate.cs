namespace Forge.IO;

/// <summary>
/// One command at a time per workspace for everything that is not trivially cheap. Builds and test runs of agents sharing a
/// worktree fight over <c>obj/</c>, <c>bin/</c> and test hosts, and running them side by side buys nothing — they would
/// only fail each other — so they queue instead. Keyed by the workspace root (the same directory seen by two sessions
/// is one key), entries are reference counted and dropped with the last holder, and the current holder is remembered so a
/// waiter can say whom it waits for.
/// </summary>
public sealed class ExecutionGate
{
    private readonly Dictionary<string, Entry> entries;
    private readonly Lock gate = new();

    public ExecutionGate()
        : this(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
    }

    public ExecutionGate(StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        entries = new Dictionary<string, Entry>(comparer);
    }

    /// <summary>The gate the shell tool uses; process wide, because two sessions can live in the same directory.</summary>
    public static ExecutionGate Shared { get; } = new();

    /// <summary>Takes the workspace if nobody holds it; false (and no lease) when someone does.</summary>
    public bool TryAcquire(string workspace, string holder, out IDisposable lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var key = WorkspacePath.Normalize(workspace);
        var entry = Reference(key);
        if (!entry.Semaphore.Wait(0))
        {
            Release(key, entry, releaseSemaphore: false);
            lease = NoLease.Instance;
            return false;
        }

        lease = Take(key, entry, holder);
        return true;
    }

    /// <summary>
    /// Waits for the workspace for at most <paramref name="maxWait"/> and holds it until the lease is disposed; null when
    /// the wait ran out. Cancelling the token ends the wait with <see cref="OperationCanceledException"/>.
    /// </summary>
    public async ValueTask<IDisposable?> AcquireAsync(string workspace, string holder, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var key = WorkspacePath.Normalize(workspace);
        var entry = Reference(key);

        bool acquired;
        try
        {
            acquired = await entry.Semaphore.WaitAsync(maxWait, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, releaseSemaphore: false);
            throw;
        }

        if (!acquired)
        {
            Release(key, entry, releaseSemaphore: false);
            return null;
        }

        return Take(key, entry, holder);
    }

    /// <summary>Who holds the workspace right now, as they described themselves; null when nobody does.</summary>
    public string? HolderOf(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var key = WorkspacePath.Normalize(workspace);
        lock (gate)
        {
            return entries.TryGetValue(key, out var entry) ? entry.Holder : null;
        }
    }

    private Entry Reference(string key)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                entries.Add(key, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private Lease Take(string key, Entry entry, string holder)
    {
        lock (gate)
        {
            entry.Holder = holder;
        }

        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            // Cleared before the semaphore is released: the waiter this wakes sets its own name, and must not be overwritten.
            lock (gate)
            {
                entry.Holder = null;
            }

            entry.Semaphore.Release();
        }

        lock (gate)
        {
            if (--entry.References == 0)
            {
                entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }

        public string? Holder { get; set; }
    }

    private sealed class Lease(ExecutionGate owner, string key, Entry entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(key, entry, releaseSemaphore: true);
            }
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
