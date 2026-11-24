using System.Text;
using Forge.Abstractions;
using Forge.App.Services;

namespace Forge.App.Agents;

/// <summary>
/// The default orchestration's prompt text: the index of the skills this session can load and the per-task routing rule
/// that sends new work through them. It is appended to the <b>root</b> agent's role text, never to the system layer: the
/// system layer is shared by the whole agent tree, and a sub-agent handed one scoped task by its parent must not stop to
/// choose a workflow or interview the user about it.
/// <para>
/// The text is a pure function of the discovered skills, so the same catalog always produces the same bytes and the
/// cached prompt prefix stays stable. Only names and descriptions go in: a skill's body is loaded on demand with the
/// <c>skill</c> tool, so the prefix does not carry every playbook.
/// </para>
/// </summary>
internal static class OrchestrationPrompt
{
    /// <summary>The skill that routes one piece of work; without it there is nothing to route with.</summary>
    public const string RouterSkillName = "choosing-a-workflow";

    private const string Header =
        """
        <skills>
        Skills are playbooks, not knowledge you already have. Load one with the skill tool ({"name": "<name>"}) before following it, and follow the text you read instead of a skill from memory. Available here:
        """;

    private const string Routing =
        """
        </skills>

        <workflow-routing>
        At the start of each new piece of work the user gives you in this session, load the choosing-a-workflow skill and follow its routing before any other tool call. It decides whether the work needs a workflow at all: a feature, a behavior change or a bug fix goes through brainstorming first, work on the tests themselves (hardening a module, pinning behavior through a refactor, an audit of test quality) goes through hardening-tests, finished work shipping as a pull request goes through shipping-a-pr, and a question, a check or an edit that changes no behavior needs no workflow.
        Work already routed resumes its workflow instead of routing again. One piece of work gets one route. The plan panel and the step gates are part of the workflow, not optional extras: read the todos skill before you plan or mark a step done.
        </workflow-routing>
        """;

    /// <summary>
    /// The block to append to the root agent's role, or an empty string when there is nothing to inject (no skills
    /// discovered, or no router among them). Skill names are listed in the catalog's own order, which is ordinal.
    /// </summary>
    public static string Build(IReadOnlyList<SlashEntry> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var router = false;
        var index = new StringBuilder(256);
        var count = 0;
        foreach (var skill in skills)
        {
            if (!skill.IsSkill)
            {
                continue;
            }

            count++;
            router |= string.Equals(skill.Name, RouterSkillName, StringComparison.OrdinalIgnoreCase);
            index.Append("- ").Append(skill.Name);
            if (skill.Description.Length > 0)
            {
                index.Append(" — ").Append(skill.Description);
            }

            index.Append('\n');
        }

        return count == 0 || !router
            ? string.Empty
            : Header + "\n\n" + index + Routing;
    }

    /// <summary>
    /// Whether <paramref name="definition"/> can load skills at all: an agent kind with an explicit tool whitelist that
    /// leaves the <c>skill</c> tool out is told nothing about skills, so it is never pointed at a tool it cannot call.
    /// </summary>
    public static bool CanLoadSkills(AgentDefinition? definition) =>
        definition?.ToolNames is not { } names || names.Contains(SkillTool.Name, StringComparer.Ordinal);
}