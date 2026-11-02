using System.Diagnostics.CodeAnalysis;
using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>
/// The available agent kinds: built-ins plus externally loaded definitions. A loaded definition replaces
/// a built-in with the same name, and lookups are case-insensitive.
/// </summary>
public sealed class AgentCatalog
{
    private readonly Dictionary<string, AgentDefinition> _byName;
    private readonly AgentDefinition[] _all;

    public AgentCatalog()
        : this((IEnumerable<AgentDefinition>?)null)
    {
    }

    public AgentCatalog(IEnumerable<AgentDefinition>? definitions)
        : this(Merge(BuiltInAgents.All, definitions))
    {
    }

    private AgentCatalog(List<AgentDefinition> ordered)
    {
        _byName = new Dictionary<string, AgentDefinition>(ordered.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ordered)
        {
            _byName[definition.Name] = definition;
        }

        _all = [.. ordered];
    }

    public IReadOnlyList<AgentDefinition> All => _all;

    public int Count => _all.Length;

    public bool TryGet(string name, [NotNullWhen(true)] out AgentDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out definition);
    }

    public AgentDefinition? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    public AgentDefinition Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No agent named '{name}' is registered.");
    }

    /// <summary>New catalog with <paramref name="definitions"/> layered on top of this one.</summary>
    public AgentCatalog With(IEnumerable<AgentDefinition> definitions) =>
        new(Merge(_all, definitions));

    private static List<AgentDefinition> Merge(
        IReadOnlyList<AgentDefinition> baseline,
        IEnumerable<AgentDefinition>? overrides)
    {
        var order = new List<string>(baseline.Count);
        var map = new Dictionary<string, AgentDefinition>(baseline.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in baseline)
        {
            if (!map.TryAdd(definition.Name, definition))
            {
                continue;
            }

            order.Add(definition.Name);
        }

        if (overrides is not null)
        {
            foreach (var definition in overrides)
            {
                if (definition is null || string.IsNullOrWhiteSpace(definition.Name))
                {
                    continue;
                }

                if (!map.TryAdd(definition.Name, definition))
                {
                    map[definition.Name] = definition;
                    continue;
                }

                order.Add(definition.Name);
            }
        }

        var merged = new List<AgentDefinition>(order.Count);
        foreach (var name in order)
        {
            merged.Add(map[name]);
        }

        return merged;
    }
}
