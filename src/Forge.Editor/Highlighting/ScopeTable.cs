// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.TextMate;

namespace Forge.Editor.Highlighting;

// Interns scope stacks so tokens carry an id instead of a scope list. Ids are stable for the lifetime of a table, which
// a tokenizer keeps for as long as it highlights one document. A published table is immutable, so a UI thread can read
// it while the tokenizer adds stacks.
public sealed class ScopeTable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string[], int> _ids = new(ScopeComparer.Instance);
    private string[][] _stacks = [];

    public int Count => Volatile.Read(ref _stacks).Length;

    internal int Intern(AttributedScopeStack scopes)
    {
        var names = scopes.GetScopeNames();
        lock (_lock)
        {
            if (_ids.TryGetValue(names, out var id))
            {
                return id;
            }

            var current = _stacks;
            id = current.Length;
            var updated = new string[current.Length + 1][];
            Array.Copy(current, updated, current.Length);
            updated[id] = names;
            _ids.Add(names, id);
            Volatile.Write(ref _stacks, updated);
            return id;
        }
    }

    // The scope names of an id, root first.
    public ReadOnlySpan<string> GetScopes(int id)
    {
        var stacks = Volatile.Read(ref _stacks);
        return (uint)id < (uint)stacks.Length ? stacks[id] : [];
    }

    private sealed class ScopeComparer : IEqualityComparer<string[]>
    {
        public static readonly ScopeComparer Instance = new();

        public bool Equals(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(string[] obj)
        {
            var hash = new HashCode();
            foreach (var scope in obj)
            {
                hash.Add(scope, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }
}
