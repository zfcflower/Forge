using Microsoft.CodeAnalysis.Scripting;

namespace Forge.CodeMode;

internal sealed class CompiledScript
{
    private readonly Lock gate = new();
    private ScriptRunner<object?>? runner;

    private CompiledScript(Script<object?>? script, string? error)
    {
        Script = script;
        Error = error;
    }

    public Script<object?>? Script { get; }

    public string? Error { get; }

    public bool Ok => Script is not null;

    public static CompiledScript Success(Script<object?> script) => new(script, null);

    public static CompiledScript Failed(string error) => new(null, error);

    public ScriptRunner<object?> Runner(CancellationToken ct)
    {
        var current = Volatile.Read(ref runner);
        if (current is not null)
        {
            return current;
        }

        lock (gate)
        {
            return runner ??= Script!.CreateDelegate(ct);
        }
    }
}

/// <summary>Bounded LRU of compiled scripts keyed by a hash of the source, so a re-sent script skips Roslyn.</summary>
internal sealed class ScriptCache(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<Entry>> byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new();
    private readonly Lock gate = new();

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

    public void Set(string key, CompiledScript value)
    {
        if (capacity <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                order.Remove(existing);
                order.AddFirst(existing);
                return;
            }

            byKey[key] = order.AddFirst(new Entry(key, value));
            while (byKey.Count > capacity)
            {
                var last = order.Last!;
                order.RemoveLast();
                byKey.Remove(last.Value.Key);
            }
        }
    }

    private sealed class Entry(string key, CompiledScript script)
    {
        public string Key { get; } = key;

        public CompiledScript Script { get; set; } = script;
    }
}
