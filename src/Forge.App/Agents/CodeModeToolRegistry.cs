using Forge.Abstractions;

namespace Forge.App.Agents;

/// <summary>
/// The tool registry a <c>run_code</c> script resolves its inner calls against (wave W14). It is a snapshot of the
/// <b>candidates</b> — the tools the session may offer a script at all, decided once when the session's external tools were
/// collected — plus a live filter: the <c>tools</c> section's visible-tool list is read on every resolution, so narrowing
/// the list applies to the next inner call and to the next description build without republishing the external set (which
/// would drop the prompt cache for a change that touches no tool definition).
/// <para>
/// The candidate set is the security boundary and lives in code (<see cref="SessionRuntime"/>): only read-only tools of a
/// fixed family set are ever candidates, because a script's inner call reaches the same approval path as any other call but
/// must never be the way a write or an execute tool gets called in a fan-out. The filter can only remove from it.
/// </para>
/// <para>
/// Reads of <see cref="Tools"/> allocate a filtered list only when the list actually hides something; the blank list — the
/// default — returns the snapshot array itself, so the common case costs nothing beyond the loop that decides it.
/// </para>
/// </summary>
internal sealed class CodeModeToolRegistry(
    IReadOnlyList<ITool> candidates,
    Func<CodeModeSettings> settings) : IToolRegistry
{
    /// <summary>The callable tools right now, in the snapshot's order (the description sorts them by name itself).</summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            var current = settings();
            if (current.VisibleTools.Count == 0)
            {
                return candidates;
            }

            var visible = new List<ITool>(candidates.Count);
            foreach (var tool in candidates)
            {
                if (current.Allows(tool.Definition.Name))
                {
                    visible.Add(tool);
                }
            }

            return visible;
        }
    }

    /// <summary>
    /// Resolves one inner call. A name outside the snapshot, or one the current list hides, is "unknown" — the same answer
    /// the script gets for a tool that does not exist, so a narrowed list cannot be probed for what it hides.
    /// </summary>
    public bool TryGet(string name, out ITool tool)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Definition.Name, name, StringComparison.Ordinal))
                {
                    if (settings().Allows(name))
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
}
