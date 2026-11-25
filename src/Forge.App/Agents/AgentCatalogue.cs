using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Prompts.Templates;

namespace Forge.App.Agents;

/// <summary>
/// The agent kinds a workspace offers, and what each may do in a given role. One place, because three callers need the same
/// answers: the session host (which runs a kind), the tree menu that creates an agent session (which lists them), and the
/// agent-session tab (which shows the kind it was created with).
/// </summary>
internal static class AgentCatalogue
{
    /// <summary>
    /// The model id a kind's template carries when it names no model of its own. <see cref="Resolve"/> substitutes the model
    /// <c>settings.json</c> configures for that kind - the same substitution <c>SessionRuntime.ResolveAgentDefaults</c> makes
    /// for the agent host - so the chip and the instance agree.
    /// </summary>
    public const string ConfiguredModelPlaceholder = "default";

    /// <summary>
    /// Tools only a root session may call: they change git (branch, sync) or publish a pull request. A kind that names one of
    /// them cannot drive a user-opened agent session, because the root session owns the branch and the remote.
    /// </summary>
    private static readonly string[] RootOnlyTools = ["git_branch", "git_sync"];

    public static bool IsRootOnlyTool(string tool) =>
        RootOnlyTools.Contains(tool, StringComparer.Ordinal) || tool.StartsWith("pr_", StringComparison.Ordinal);

    /// <summary>
    /// Every kind of the snapshot with the <c>settings.json</c> default for that kind in place of the <see cref="ConfiguredModelPlaceholder"/>:
    /// a kind whose file names a model keeps it, and the substitute carries the entry's provider id. Without a resolver (a
    /// standalone caller) the snapshot is used as it is. Reads the template files, so call it off a hot path.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> Resolve(PromptTemplateSet templates, Func<string, ModelSettings?>? defaults)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (defaults is not { } resolve)
        {
            return templates.Agents;
        }

        var resolved = new List<AgentDefinition>(templates.Agents.Count);
        foreach (var (id, definition, _) in templates.ResolvedAgents)
        {
            // The settings key is the template id (the file name), never the kind's display name: "Worker" has no entry.
            var configured = string.Equals(definition.Model.Model, ConfiguredModelPlaceholder, StringComparison.OrdinalIgnoreCase)
                ? resolve(id)
                : null;
            resolved.Add(configured is null ? definition : definition with { Model = configured });
        }

        return resolved;
    }

    /// <summary>The kind whose name is <paramref name="name"/>; null when no such kind exists any more.</summary>
    public static AgentDefinition? Find(IReadOnlyList<AgentDefinition> kinds, string? name)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var kind in kinds)
        {
            if (string.Equals(kind.Name, name, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a kind may drive a user-opened agent session: it has to name its tools (an unrestricted agent could touch git,
    /// and only the root session changes git), and that list may hold no root-only tool.
    /// </summary>
    public static bool CanDriveUserSession(AgentDefinition definition) =>
        definition.ToolNames is { Count: > 0 } tools && !tools.Any(IsRootOnlyTool);

    /// <summary>Why the kind cannot drive an agent session, in the user's language; null when it can.</summary>
    public static string? UnavailableReason(AgentDefinition definition) =>
        definition.ToolNames is not { Count: > 0 } ? AgentSessionStrings.AgentNoTools
        : definition.ToolNames.Any(IsRootOnlyTool) ? AgentSessionStrings.AgentRootOnlyTools
        : null;
}
