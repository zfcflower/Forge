namespace Forge.IO;

/// <summary>
/// Serializes the operations that must not interleave for one path (a read-modify-write of the same file) without
/// locking the file system as a whole. Entries are reference counted and dropped when the last holder leaves, so a long
/// session that touches millions of paths does not grow the table.
/// </summary>
public sealed class PathGate
{
    private readonly Dictionary<string, Entry> entries;
    private readonly Lock gate = new();

    public PathGate()
        : this(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
    }

    public PathGate(StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        entries = new Dictionary<string, Entry>(comparer);
    }

    /// <summary>The gate the file tools use; process wide, because two sessions can live in the same directory.</summary>
    public static PathGate Shared { get; } = new();

    /// <summary>Waits until this path is free and holds it until the returned lease is disposed.</summary>
    public async ValueTask<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = WorkspacePath.Normalize(path);

        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                entries.Add(key, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, releaseSemaphore: false);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
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
    }

    private sealed class Lease(PathGate owner, string key, Entry entry) : IDisposable
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
}
