using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;

namespace Forge.Scripting;

/// <summary>
/// One compiled script image and its descriptor, success or failure. The bytes are produced once per cache key; the
/// image is loaded fresh into its own collectible assembly-load context for every run.
/// </summary>
internal sealed class CompiledScript
{
    private CompiledScript(ScriptCompilation compilation) => Compilation = compilation;

    public ScriptCompilation Compilation { get; }

    public bool Ok => Compilation.Ok;

    /// <summary>Bounded, already formatted compile/whitelist error; <c>null</c> on success.</summary>
    public string? Error => Compilation.Error;

    public static CompiledScript Success(ScriptCompilation compilation) => new(compilation);

    public static CompiledScript Failed(ScriptCompilation compilation) => new(compilation);
}

/// <summary>
/// Bounded LRU of <see cref="CompiledScript"/> values, including failed compilations so a malformed script does not
/// re-enter Roslyn. Keyed by <see cref="ScriptCodeCompiler.CacheKey"/>, which covers the whole compile surface, not just
/// the source. The capacity follows the newest request's validated
/// <see cref="CodeExecutionOptions.CompilationCacheCapacity"/>.
/// </summary>
internal sealed class ScriptCompilationCache
{
    private readonly Dictionary<string, LinkedListNode<Entry>> byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new();
    private readonly Lock gate = new();
    private int capacity;

    public bool TryGet(string key, out CompiledScript value)
    {
        lock (gate)
        {
            if (byKey.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                value = node.Value.Script;
                return true;
            }
        }

        value = null!;
        return false;
    }

    public void Set(string key, CompiledScript value, int capacityLimit)
    {
        lock (gate)
        {
            if (capacityLimit != capacity)
            {
                capacity = capacityLimit;
                Trim();
            }

            if (capacity == 0)
            {
                return;
            }

            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                order.Remove(existing);
                order.AddFirst(existing);
                return;
            }

            byKey[key] = order.AddFirst(new Entry(key, value));
            Trim();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            byKey.Clear();
            order.Clear();
        }
    }

    private void Trim()
    {
        while (byKey.Count > capacity)
        {
            var last = order.Last!;
            order.RemoveLast();
            byKey.Remove(last.Value.Key);
        }
    }

    private sealed class Entry(string key, CompiledScript script)
    {
        public string Key { get; } = key;

        public CompiledScript Script { get; set; } = script;
    }
}
