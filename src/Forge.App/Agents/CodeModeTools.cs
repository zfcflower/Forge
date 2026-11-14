using Forge.Abstractions;
using Forge.Mcp;

namespace Forge.App.Agents;

/// <summary>
/// Which of a session's tools a <c>run_code</c> script may call (wave W14). Kept apart from
/// <see cref="SessionRuntime.CollectExternalTools"/> — which is the only caller — so the rule is readable and testable
/// without an extension host: the caller hands in the four sources it collected, and this decides.
/// <para>
/// Built-in tools: only <b>read-only</b> ones, by the predicate the approval policy already uses
/// (<c>DefaultApprovalPolicy.AutoAllowReadOnly</c>); the two web tools stay read-only by declaration but are asked about per
/// call by the session policy. The built-in write and execute tools (<c>write</c>, <c>edit</c>, <c>bash</c>, the git and PR
/// tools, the extension authoring tools) are not in the candidate set: a script cannot name, describe or call them. That is
/// not an approval bypass (an inner call goes through <c>SessionToolCallRouter.ApproveAsync</c> like any other and would
/// raise its card); it keeps a fan-out from raising a burst of cards.
/// </para>
/// <para>
/// The plugin tools are taken wholesale, write and execute ones included, which is the behaviour scripts already had: a
/// plugin declares the surface its callers may use, and a plugin's write tool still goes to the approval card per call. A
/// plugin tool whose name is <i>reserved</i> (a Core built-in, a name the approval policy has a rule for, or the name of
/// another session tool) is dropped: the approval and the execution of one inner call must be about the same tool.
/// </para>
/// </summary>
internal static class CodeModeTools
{
    /// <summary>
    /// The script-facing candidates, in source order and without duplicate names (the registry keys by name, so the first
    /// registration of a name wins: plugin tools, then file, web and MCP tools). That is the opposite of the session's own
    /// registry, where a later registration of a name replaces an earlier one and Core built-ins come before plugins;
    /// <paramref name="reservedNames"/> is what keeps the two from disagreeing about a name.
    /// </summary>
    internal static List<ITool> Collect(
        IReadOnlyList<ITool> pluginTools,
        IReadOnlyList<ITool> fileTools,
        IReadOnlyList<ITool> webTools,
        IReadOnlyList<ITool> mcpTools,
        IReadOnlySet<string>? reservedNames = null)
    {
        ArgumentNullException.ThrowIfNull(pluginTools);
        ArgumentNullException.ThrowIfNull(fileTools);
        ArgumentNullException.ThrowIfNull(webTools);
        ArgumentNullException.ThrowIfNull(mcpTools);

        var candidates = new List<ITool>(pluginTools.Count + fileTools.Count + webTools.Count + mcpTools.Count);
        var names = new HashSet<string>(candidates.Capacity, StringComparer.Ordinal);
        foreach (var tool in pluginTools)
        {
            if (reservedNames is null || !reservedNames.Contains(tool.Definition.Name))
            {
                Add(candidates, names, tool);
            }
        }

        AppendReadOnly(candidates, names, fileTools);
        AppendReadOnly(candidates, names, webTools);
        AppendReadOnly(candidates, names, mcpTools);
        return candidates;
    }

    private static void AppendReadOnly(List<ITool> into, HashSet<string> names, IReadOnlyList<ITool> tools)
    {
        foreach (var tool in tools)
        {
            if (tool.SideEffect == ToolSideEffect.ReadOnly)
            {
                Add(into, names, tool);
            }
        }
    }

    private static void Add(List<ITool> into, HashSet<string> names, ITool tool)
    {
        if (names.Add(tool.Definition.Name))
        {
            into.Add(tool);
        }
    }
}
