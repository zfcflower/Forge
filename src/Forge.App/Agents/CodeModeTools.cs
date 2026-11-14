using Forge.Abstractions;
using Forge.Mcp;

namespace Forge.App.Agents;

/// <summary>
/// Which of a session's tools a <c>run_code</c> script may call (wave W14). Kept apart from
/// <see cref="SessionRuntime.CollectExternalTools"/> — which is the only caller — so the rule is readable and testable
/// without an extension host: the caller hands in the four sources it collected, and this decides.
/// <para>
/// The rule is one word: <b>read-only</b>. It is the same predicate the approval policy already uses
/// (<c>DefaultApprovalPolicy.AutoAllowReadOnly</c>), so a tool a script can reach is never one whose direct call would have
/// raised a card — except the two web tools, which stay read-only by declaration but are asked about per call by the
/// session policy. Nothing a script must not run (<c>write</c>, <c>edit</c>, <c>bash</c>, the git and PR write tools, the
/// extension authoring tools) is in the candidate set at all: it cannot be named, described or called.
/// </para>
/// <para>
/// The plugin tools are the exception to the read-only filter and are taken wholesale, which is the behaviour scripts
/// already had: a plugin declares the surface its callers may use, and a plugin's own write tool still goes to the approval
/// card when a script calls it.
/// </para>
/// </summary>
internal static class CodeModeTools
{
    /// <summary>
    /// The script-facing candidates, in source order and without duplicate names (the registry keys by name, so the first
    /// registration of a name wins — the same rule the session's own registry follows).
    /// </summary>
    internal static List<ITool> Collect(
        IReadOnlyList<ITool> pluginTools,
        IReadOnlyList<ITool> fileTools,
        IReadOnlyList<ITool> webTools,
        IReadOnlyList<ITool> mcpTools)
    {
        ArgumentNullException.ThrowIfNull(pluginTools);
        ArgumentNullException.ThrowIfNull(fileTools);
        ArgumentNullException.ThrowIfNull(webTools);
        ArgumentNullException.ThrowIfNull(mcpTools);

        var candidates = new List<ITool>(pluginTools.Count + fileTools.Count + webTools.Count + mcpTools.Count);
        var names = new HashSet<string>(candidates.Capacity, StringComparer.Ordinal);
        foreach (var tool in pluginTools)
        {
            Add(candidates, names, tool);
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
