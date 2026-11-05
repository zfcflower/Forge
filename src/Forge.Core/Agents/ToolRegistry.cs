using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>A tool whose approval side effect depends on the arguments of the call, such as a checklist that reads or writes.</summary>
public interface IToolSideEffectResolver
{
    ToolSideEffect Resolve(ToolCall call);
}

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

    /// <summary>Approval side effect of one call: the tool's own, unless it decides per call.</summary>
    public static ToolSideEffect SideEffectOf(ITool tool, ToolCall call) =>
        tool is IToolSideEffectResolver resolver ? resolver.Resolve(call) : tool.SideEffect;

    public bool TryGet(string name, out ITool tool) => byName.TryGetValue(name, out tool!);

    /// <summary>
    /// Tools visible to one agent: its definition's allow-list (null means everything) minus the sub-agent
    /// tools once the depth limit is reached.
    /// </summary>
    public ToolRegistry Select(IReadOnlyList<string>? toolNames, bool canSpawn, IReadOnlyList<string> subAgentTools)
    {
        var selected = new List<ITool>(Tools.Count);
        if (toolNames is null)
        {
            foreach (var tool in Tools)
            {
                if (canSpawn || !IsSubAgentTool(tool, subAgentTools))
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
                if (allowed.Contains(tool.Definition.Name) && (canSpawn || !IsSubAgentTool(tool, subAgentTools)))
                {
                    selected.Add(tool);
                }
            }

            if (canSpawn)
            {
                foreach (var name in subAgentTools)
                {
                    if (!allowed.Contains(name) && byName.TryGetValue(name, out var tool))
                    {
                        selected.Add(tool);
                    }
                }
            }
        }

        return new ToolRegistry(selected);
    }

    private static bool IsSubAgentTool(ITool tool, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (string.Equals(tool.Definition.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
