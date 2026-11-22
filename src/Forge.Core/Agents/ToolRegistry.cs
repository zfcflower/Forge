using Forge.Abstractions;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>A tool whose approval side effect depends on the arguments of the call, such as a checklist that reads or writes.</summary>
public interface IToolSideEffectResolver
{
    ToolSideEffect Resolve(ToolCall call);
}

/// <summary>
/// The tools the host owns, keyed by name. By default sorted ordinally so two agents with the same definition send
/// byte-identical tool definitions and keep hitting the same prompt cache entry; <see cref="ToolSurface"/> builds its own
/// order (three segments, each sorted) and hands it over with <c>preserveOrder</c>, which keeps the given order.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> byName;

    public ToolRegistry(IEnumerable<ITool> tools)
        : this(tools, preserveOrder: false)
    {
    }

    /// <param name="tools">The tools; for a repeated name the last one wins.</param>
    /// <param name="preserveOrder">True keeps the order of <paramref name="tools"/> (a repeated name stays at its first position); false sorts by name.</param>
    public ToolRegistry(IEnumerable<ITool> tools, bool preserveOrder)
    {
        ArgumentNullException.ThrowIfNull(tools);
        byName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        var firstSeen = new List<string>();
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var name = tool.Definition.Name;
            if (byName.ContainsKey(name) is false)
            {
                firstSeen.Add(name);
            }

            byName[name] = tool;
        }

        var ordered = new ITool[firstSeen.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index] = byName[firstSeen[index]];
        }

        if (!preserveOrder)
        {
            Array.Sort(ordered, static (left, right) => string.CompareOrdinal(left.Definition.Name, right.Definition.Name));
        }

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
    /// <para><c>message_session</c> is the other way round: only the root and the sessions the user drives
    /// (<paramref name="canMessage"/>) may see it, and a model-spawned child drops it even when its allow-list names it - the
    /// model's only channel to a user session is the user's own <c>@</c> mention.</para>
    /// </summary>
    public ToolRegistry Select(
        IReadOnlyList<string>? toolNames,
        bool canSpawn,
        IReadOnlyList<string> subAgentTools,
        bool isRoot = true,
        bool canMessage = false)
    {
        var selected = new List<ITool>(Tools.Count);
        if (toolNames is null)
        {
            foreach (var tool in Tools)
            {
                if ((canSpawn || !IsSubAgentTool(tool, subAgentTools))
                    && (isRoot || !IsExtensionTool(tool.Definition.Name))
                    && (canMessage || !IsSessionMessageTool(tool.Definition.Name)))
                {
                    selected.Add(tool);
                }
            }
        }
        else
        {
            var allowed = new HashSet<string>(toolNames, StringComparer.Ordinal);
            if (canSpawn)
            {
                // The spawn and control tools ride along with any allow-list while spawning is possible; adding them to the
                // set (rather than appending afterwards) keeps every tool at its position in this registry's order.
                allowed.UnionWith(subAgentTools);
            }

            if (canMessage)
            {
                // The same rule for the delivery tool: a session kind's whitelist is about its own work, not about the one
                // constant channel the root and the sessions the user drives always hold.
                allowed.Add(SessionReplies.ToolName);
            }

            foreach (var tool in Tools)
            {
                var name = tool.Definition.Name;
                if (allowed.Contains(name) && (canSpawn || !IsSubAgentTool(tool, subAgentTools))
                    && (isRoot || !IsExtensionTool(name))
                    && (canMessage || !IsSessionMessageTool(name)))
                {
                    selected.Add(tool);
                }
            }
        }

        return new ToolRegistry(selected, preserveOrder: true);
    }

    private static bool IsExtensionTool(string name) =>
        name is ExtensionToolNames.List or ExtensionToolNames.Create or ExtensionToolNames.Update
            or ExtensionToolNames.Disable or ExtensionToolNames.Delete;

    private static bool IsSessionMessageTool(string name) =>
        string.Equals(name, SessionReplies.ToolName, StringComparison.Ordinal);

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
/// The tools a host supplies besides Core's own, grouped by how stable their presence is, because the group decides the
/// position in the tool array (and so how much of the prompt cache a change costs): <see cref="Fixed"/> tools are always
/// there, <see cref="Conditional"/> built-ins exist only while a condition holds, <see cref="Outside"/> tools come from
/// outside the product (plugins, MCP servers). The surface keeps <see cref="Fixed"/> in the order it is given (an authored
/// list, appended after Core's own) and sorts the other two groups by name; the groups are never merged.
/// </summary>
public sealed record ExternalTools(IReadOnlyList<ITool> Fixed, IReadOnlyList<ITool> Conditional, IReadOnlyList<ITool> Outside)
{
    /// <summary>Every tool, the groups one after the other.</summary>
    public IReadOnlyList<ITool> All => [.. Fixed, .. Conditional, .. Outside];
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
    private ITool[] externalFixed = [];
    private ITool[] externalConditional = [];
    private ITool[] externalOther = [];
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

        this.builtIns = new ToolRegistry(list, preserveOrder: true);
        current = new ToolSet(0, this.builtIns);
    }

    /// <summary>
    /// Current tool set, deduplicated and without disabled names, in three segments that are never re-sorted together: the
    /// fixed tools (Core's built-ins plus the host's always-present ones, in declaration order), then the conditional
    /// built-ins (present only while their condition holds, by name), then the external tools (plugins, MCP, by name). A change
    /// in a later segment leaves every earlier tool definition where it was, so it only breaks the prompt cache from its own
    /// position on. The first segment is an authored list rather than an assembled one, so its declaration order is what the
    /// model reads — and it never changes at runtime, which is what keeps the prefix byte-stable just the same.
    /// </summary>
    public ToolRegistry Tools => Volatile.Read(ref current).Tools;

    /// <summary>Version of the current tool set, starting at 0; compare it to detect that <see cref="Tools"/> was replaced.</summary>
    public int Version => Volatile.Read(ref current).Version;

    internal ToolSet Current => Volatile.Read(ref current);

    /// <summary>The Core built-in tools of this host, including disabled ones; the catalog page lists them with their switch.</summary>
    public IReadOnlyList<ITool> BuiltIns => builtIns.Tools;

    /// <summary>The tools last published through <c>ReplaceExternal</c> (built-in names already dropped), including disabled ones.</summary>
    public IReadOnlyList<ITool> External
    {
        get
        {
            lock (publishGate)
            {
                return [.. externalFixed, .. externalConditional, .. externalOther];
            }
        }
    }

    /// <summary>
    /// Publishes a new external tool set, all of it in the conditional segment; safe to call from any thread. Built-ins stay
    /// and shadow an external tool of the same name. A replacement always publishes a new version, so the next model request
    /// re-selects even when the names happen to be unchanged.
    /// </summary>
    public void ReplaceExternal(IEnumerable<ITool> tools) => ReplaceExternal([], tools, []);

    /// <summary>Publishes a new external tool set split into its three segments.</summary>
    public void ReplaceExternal(ExternalTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ReplaceExternal(tools.Fixed, tools.Conditional, tools.Outside);
    }

    /// <summary>
    /// Publishes a new external tool set split by segment: <paramref name="fixedTools"/> are always present (appended after the
    /// Core built-ins in the order given), <paramref name="conditionalTools"/> exist only while a condition holds, and
    /// <paramref name="externalTools"/> come from outside the product (plugins, MCP). The last two segments are sorted by name:
    /// they are assembled from data, so only their set is meaningful.
    /// </summary>
    public void ReplaceExternal(IEnumerable<ITool> fixedTools, IEnumerable<ITool> conditionalTools, IEnumerable<ITool> externalTools)
    {
        ArgumentNullException.ThrowIfNull(fixedTools);
        ArgumentNullException.ThrowIfNull(conditionalTools);
        ArgumentNullException.ThrowIfNull(externalTools);
        var first = Filter(fixedTools);
        var second = Filter(conditionalTools);
        var third = Filter(externalTools);
        lock (publishGate)
        {
            externalFixed = first;
            externalConditional = second;
            externalOther = third;
            Publish();
        }
    }

    private ITool[] Filter(IEnumerable<ITool> tools)
    {
        var list = new List<ITool>();
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!builtInNames.Contains(tool.Definition.Name))
            {
                list.Add(tool);
            }
        }

        return [.. list];
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
        var merged = new List<ITool>(builtIns.Tools.Count + externalFixed.Length + externalConditional.Length + externalOther.Length);
        AddEnabled(merged, sortedByName: false, builtIns.Tools, externalFixed);
        AddEnabled(merged, sortedByName: true, externalConditional);
        AddEnabled(merged, sortedByName: true, externalOther);
        Volatile.Write(ref current, new ToolSet(unchecked(current.Version + 1), new ToolRegistry(merged, preserveOrder: true)));
    }

    /// <summary>
    /// Appends the enabled tools of the given sources to <paramref name="into"/> as one segment. <paramref name="sortedByName"/>
    /// makes a segment's bytes independent of the order its tools arrived in (those segments are assembled from data, so only
    /// the set is meaningful); the first segment keeps the declaration order of an authored list.
    /// </summary>
    private void AddEnabled(List<ITool> into, bool sortedByName, params IReadOnlyList<ITool>[] sources)
    {
        var start = into.Count;
        foreach (var source in sources)
        {
            foreach (var tool in source)
            {
                if (!disabled.Contains(tool.Definition.Name))
                {
                    into.Add(tool);
                }
            }
        }

        if (sortedByName)
        {
            into.Sort(start, into.Count - start, SegmentOrder.Instance);
        }
    }

    private sealed class SegmentOrder : IComparer<ITool>
    {
        public static readonly SegmentOrder Instance = new();

        public int Compare(ITool? left, ITool? right) =>
            string.CompareOrdinal(left!.Definition.Name, right!.Definition.Name);
    }
}
