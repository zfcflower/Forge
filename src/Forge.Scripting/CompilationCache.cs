using System.Diagnostics.CodeAnalysis;

namespace Forge.Scripting;

/// <summary>
/// Bounded LRU of <see cref="CompilationResult"/> values keyed by <see cref="CompilationRequest.CacheKey"/>, so a
/// re-sent or re-enabled plugin never pays Roslyn twice. A capacity of 0 disables caching. A cache is scoped to one
/// compiler configuration: the key covers the request, not the compiler's default reference set.
/// </summary>
public sealed class CompilationCache
{
    private readonly Dictionary<string, LinkedListNode<Entry>> byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new();
    private readonly Lock gate = new();
    private readonly int capacity;
    private long hits;
    private long misses;

    public CompilationCache(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 0);
        this.capacity = capacity;
    }

    public int Capacity => capacity;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return byKey.Count;
            }
        }
    }

    public long Hits => Interlocked.Read(ref hits);

    public long Misses => Interlocked.Read(ref misses);

    public bool TryGet(string cacheKey, [NotNullWhen(true)] out CompilationResult? result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        lock (gate)
        {
            if (byKey.TryGetValue(cacheKey, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                result = node.Value.Result;
                Interlocked.Increment(ref hits);
                return true;
            }
        }

        Interlocked.Increment(ref misses);
        result = null;
        return false;
    }

    public void Set(string cacheKey, CompilationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(result);
        if (capacity == 0)
        {
            return;
        }

        lock (gate)
        {
            if (byKey.TryGetValue(cacheKey, out var existing))
            {
                existing.Value = new Entry(cacheKey, result);
                order.Remove(existing);
                order.AddFirst(existing);
                return;
            }

            byKey[cacheKey] = order.AddFirst(new Entry(cacheKey, result));
            while (byKey.Count > capacity)
            {
                var last = order.Last!;
                order.RemoveLast();
                byKey.Remove(last.Value.Key);
            }
        }
    }

    public bool Evict(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        lock (gate)
        {
            if (!byKey.Remove(cacheKey, out var node))
            {
                return false;
            }

            order.Remove(node);
            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            byKey.Clear();
            order.Clear();
        }

        Interlocked.Exchange(ref hits, 0);
        Interlocked.Exchange(ref misses, 0);
    }

    /// <summary>Compiles every request that is not cached yet, in parallel, and stores the results in this cache.</summary>
    public Task WarmUpAsync(
        ScriptCompiler compiler,
        IEnumerable<CompilationRequest> requests,
        int maxParallelism = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(requests);

        var parallelOptions = new ParallelOptions { CancellationToken = ct };
        if (maxParallelism > 0)
        {
            parallelOptions.MaxDegreeOfParallelism = maxParallelism;
        }

        return Parallel.ForEachAsync(requests, parallelOptions, (request, token) =>
        {
            if (!TryGet(request.CacheKey, out _))
            {
                Set(request.CacheKey, compiler.CompileDirect(request, token));
            }

            return ValueTask.CompletedTask;
        });
    }

    private sealed class Entry(string key, CompilationResult result)
    {
        public string Key { get; } = key;

        public CompilationResult Result { get; set; } = result;
    }
}
