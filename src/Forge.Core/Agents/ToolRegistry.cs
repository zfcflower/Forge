using Forge.Abstractions;
using Forge.Core.Tools;

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
    /// tools once the depth limit is reached. Only the root agent (<paramref name="isRoot"/>) may see the extension tools:
    /// for any sub-agent they are dropped even when the allow-list is null or names them.
    /// </summary>
    public ToolRegistry Select(IReadOnlyList<string>? toolNames, bool canSpawn, IReadOnlyList<string> subAgentTools, bool isRoot = true)
    {
        var selected = new List<ITool>(Tools.Count);
        if (toolNames is null)
        {
            foreach (var tool in Tools)
            {
                if ((canSpawn || !IsSubAgentTool(tool, subAgentTools)) && (isRoot || !IsExtensionTool(tool.Definition.Name)))
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
                if (allowed.Contains(tool.Definition.Name) && (canSpawn || !IsSubAgentTool(tool, subAgentTools))
                    && (isRoot || !IsExtensionTool(tool.Definition.Name)))
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

    private static bool IsExtensionTool(string name) =>
        name is ExtensionToolNames.List or ExtensionToolNames.Create or ExtensionToolNames.Update
            or ExtensionToolNames.Disable or ExtensionToolNames.Delete;

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

/// <summary>
/// An immutable pair of a published tool registry and the version it was published under; agents read both together so
/// they can never mix a new version with an old registry.
/// </summary>
internal sealed record ToolSet(int Version, ToolRegistry Tools);

/// <summary>
/// The live tool set of one host: the built-in tools, which never change, plus the externally supplied tools, which
/// <see cref="AgentHost.ReplaceExternalTools"/> swaps at any time, minus the names the user switched off
/// (<see cref="SetDisabled"/>). Readers take one immutable snapshot, so no lock is needed on the hot path;
/// <see cref="Version"/> changes exactly when <see cref="Tools"/> is replaced, which lets each run reuse its selected
/// registry (and its byte-identical definitions) until the set really changed. Built-in tools always win: an external tool
/// with the name of a built-in tool is dropped, never registered next to it. A disabled name is filtered from both halves,
/// so a switched-off tool is neither offered to the model nor callable through the host router.
/// </summary>
public sealed class ToolSurface
{
    private readonly ToolRegistry builtIns;
    private readonly HashSet<string> builtInNames;
    private readonly Lock publishGate = new();
    private ToolSet current;
    private ITool[] external = [];
    private HashSet<string> disabled = new(StringComparer.Ordinal);

    public ToolSurface(IEnumerable<ITool> builtIns)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        builtInNames = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ITool>();
        foreach (var tool in builtIns)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (builtInNames.Add(tool.Definition.Name))
            {
                list.Add(tool);
            }
        }

        this.builtIns = new ToolRegistry(list);
        current = new ToolSet(0, this.builtIns);
    }

    /// <summary>Current tool set: the built-ins plus the external tools, sorted by name and deduplicated, without disabled names.</summary>
    public ToolRegistry Tools => Volatile.Read(ref current).Tools;

    /// <summary>Version of the current tool set, starting at 0; compare it to detect that <see cref="Tools"/> was replaced.</summary>
    public int Version => Volatile.Read(ref current).Version;

    internal ToolSet Current => Volatile.Read(ref current);

    /// <summary>The Core built-in tools of this host, including disabled ones; the catalog page lists them with their switch.</summary>
    public IReadOnlyList<ITool> BuiltIns => builtIns.Tools;

    /// <summary>The external tools last published (built-in names already dropped), including disabled ones.</summary>
    public IReadOnlyList<ITool> External
    {
        get
        {
            lock (publishGate)
            {
                return external;
            }
        }
    }

    /// <summary>
    /// Publishes a new external tool set; safe to call from any thread. Built-ins stay and shadow an external tool of the
    /// same name. A replacement always publishes a new version, so the next model request re-selects even when the names
    /// happen to be unchanged.
    /// </summary>
    public void ReplaceExternal(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var list = new List<ITool>();
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!builtInNames.Contains(tool.Definition.Name))
            {
                list.Add(tool);
            }
        }

        lock (publishGate)
        {
            external = [.. list];
            Publish();
        }
    }

    /// <summary>
    /// Switches the named tools off (and every other name back on). Publishes a new version only when the effective set
    /// changed, so re-applying the same setting before every turn costs nothing and never breaks the prompt cache. Unknown
    /// names are kept: a tool registered later under that name starts disabled.
    /// </summary>
    public void SetDisabled(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var next = new HashSet<string>(names, StringComparer.Ordinal);
        lock (publishGate)
        {
            if (next.SetEquals(disabled))
            {
                return;
            }

            disabled = next;
            Publish();
        }
    }

    // Caller holds publishGate.
    private void Publish()
    {
        var builtInTools = builtIns.Tools;
        var merged = new List<ITool>(builtInTools.Count + external.Length);
        foreach (var tool in builtInTools)
        {
            if (!disabled.Contains(tool.Definition.Name))
            {
                merged.Add(tool);
            }
        }

        foreach (var tool in external)
        {
            if (!disabled.Contains(tool.Definition.Name))
            {
                merged.Add(tool);
            }
        }

        Volatile.Write(ref current, new ToolSet(unchecked(current.Version + 1), new ToolRegistry(merged)));
    }
}
