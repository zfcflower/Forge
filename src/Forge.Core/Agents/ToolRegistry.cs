using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The tools the host owns, keyed by name and sorted ordinally so two agents with the same definition
/// send byte-identical tool definitions and keep hitting the same prompt cache entry.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> byName;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        byName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            byName[tool.Definition.Name] = tool;
        }

        var ordered = byName.Values.ToArray();
        Array.Sort(ordered, static (left, right) => string.CompareOrdinal(left.Definition.Name, right.Definition.Name));
        Tools = ordered;
        Definitions = Array.ConvertAll(ordered, static tool => tool.Definition);
    }

    public IReadOnlyList<ITool> Tools { get; }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public bool TryGet(string name, out ITool tool) => byName.TryGetValue(name, out tool!);

    /// <summary>
    /// Tools visible to one agent: its definition's allow-list (null means everything) minus the spawn
    /// tool once the depth limit is reached.
    /// </summary>
    public ToolRegistry Select(IReadOnlyList<string>? toolNames, bool canSpawn, string spawnToolName)
    {
        var selected = new List<ITool>(Tools.Count);
        if (toolNames is null)
        {
            foreach (var tool in Tools)
            {
                if (canSpawn || !IsSpawn(tool, spawnToolName))
                {
                    selected.Add(tool);
                }
            }
        }
        else
        {
            var allowed = new HashSet<string>(toolNames, StringComparer.Ordinal);
            foreach (var tool in Tools)
            {
                if (allowed.Contains(tool.Definition.Name) && (canSpawn || !IsSpawn(tool, spawnToolName)))
                {
                    selected.Add(tool);
                }
            }

            if (canSpawn && !allowed.Contains(spawnToolName) && byName.TryGetValue(spawnToolName, out var spawn))
            {
                selected.Add(spawn);
            }
        }

        return new ToolRegistry(selected);
    }

    private static bool IsSpawn(ITool tool, string spawnToolName) =>
        string.Equals(tool.Definition.Name, spawnToolName, StringComparison.Ordinal);
}
