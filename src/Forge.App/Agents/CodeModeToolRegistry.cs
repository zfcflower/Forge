using Forge.Abstractions;

namespace Forge.App.Agents;

/// <summary>
/// The tool registry a <c>run_code</c> script resolves its inner calls against (wave W14). It is a snapshot of the
/// <b>candidates</b> — the tools the session may offer a script at all, decided once when the session's external tools were
/// collected — plus a live filter: the <c>tools</c> section's visible-tool list is read on every resolution, so narrowing
/// the list applies to the next inner call and to the next description build without republishing the external set (which
/// would drop the prompt cache for a change that touches no tool definition). The user's own tool switches
/// (<c>toolsDisabled</c>, <see cref="ToolCatalog.EffectiveDisabled"/>) are read live the same way: a tool the user switched
/// off is neither listed to a script nor callable by one.
/// <para>
/// The candidate set is the security boundary and lives in code (<see cref="SessionRuntime"/>, <see cref="CodeModeTools"/>):
/// the plugin tools the extension host registered plus the read-only tools of the workspace, web and MCP families. Built-in
/// write and execute tools (<c>write</c>, <c>edit</c>, <c>bash</c>, git and PR tools, extension authoring) are not in it.
/// A script's inner call still goes through the session's approval path, so a plugin's own write or execute tool asks per
/// call. The filters can only remove from the candidates.
/// </para>
/// <para>
/// Reads of <see cref="Tools"/> allocate a filtered list only when something is hidden; with a blank list and nothing
/// switched off — the default — it returns the snapshot array itself, so the common case costs nothing beyond the loops
/// that decide it.
/// </para>
/// </summary>
internal sealed class CodeModeToolRegistry(
    IReadOnlyList<ITool> candidates,
    Func<CodeModeSettings> settings,
    Func<IReadOnlyList<string>>? disabledTools = null) : IToolRegistry
{
    /// <summary>The callable tools right now, in the snapshot's order (the description sorts them by name itself).</summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            var current = settings();
            var disabled = disabledTools?.Invoke() ?? [];
            if (current.VisibleTools.Count == 0 && disabled.Count == 0)
            {
                return candidates;
            }

            var visible = new List<ITool>(candidates.Count);
            foreach (var tool in candidates)
            {
                var name = tool.Definition.Name;
                if (current.Allows(name) && !IsSwitchedOff(disabled, name))
                {
                    visible.Add(tool);
                }
            }

            return visible;
        }
    }

    /// <summary>
    /// Resolves one inner call. A name outside the snapshot, one the current list hides and one the user switched off are
    /// all "unknown" — the same answer the script gets for a tool that does not exist, so a narrowed list cannot be probed
    /// for what it hides.
    /// </summary>
    public bool TryGet(string name, out ITool tool)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Definition.Name, name, StringComparison.Ordinal))
                {
                    if (settings().Allows(name) && !IsSwitchedOff(disabledTools?.Invoke() ?? [], name))
                    {
                        tool = candidate;
                        return true;
                    }

                    break;
                }
            }
        }

        tool = null!;
        return false;
    }

    /// <summary>Whether the user's switches turn <paramref name="name"/> off; names that may not be switched off are ignored,
    /// exactly as for the session's own registry. Ordinal, like <c>ToolSurface</c>.</summary>
    private static bool IsSwitchedOff(IReadOnlyList<string> stored, string name)
    {
        if (!ToolCatalog.CanDisable(name))
        {
            return false;
        }

        foreach (var entry in stored)
        {
            if (string.Equals(entry, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
