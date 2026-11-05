using Forge.Abstractions;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>A plugin's non-tool contribution set, validated and normalized once when it arrives. Immutable: a
/// newer <c>contributions.register</c> replaces the whole instance, so a second registration can neither duplicate
/// nor leave a stale entry behind.</summary>
internal sealed record ValidatedContributions(
    IReadOnlyList<Sdk.CommandDeclaration> Commands,
    IReadOnlyList<Sdk.WorkflowDeclaration> Workflows,
    IReadOnlyList<Sdk.AgentDeclaration> Agents,
    IReadOnlyList<Sdk.PromptContributorDeclaration> Prompts)
{
    internal static ValidatedContributions Empty { get; } = new([], [], [], []);

    internal bool IsEmpty =>
        Commands.Count == 0 && Workflows.Count == 0 && Agents.Count == 0 && Prompts.Count == 0;
}

/// <summary>One plugin's contribution snapshot plus the connection generation that registered it.</summary>
internal sealed record PluginContributions(long Generation, ValidatedContributions Contributions);

/// <summary>Converts validated SDK declarations to the Abstractions contracts the main program consumes.
/// Every conversion is strict: an unknown enum value or a malformed declaration is dropped with a diagnostic
/// instead of being coerced into a runnable approximation.</summary>
internal static class ContributionSnapshots
{
    internal static ValidatedContributions Validate(
        string pluginId, Sdk.PluginContributionSet? set, List<string> diagnostics)
    {
        if (set is null)
        {
            return ValidatedContributions.Empty;
        }

        var commands = new List<Sdk.CommandDeclaration>(set.Commands?.Count ?? 0);
        var commandNames = new HashSet<string>(StringComparer.Ordinal);
        if (set.Commands is { } reportedCommands)
        {
            foreach (var command in reportedCommands)
            {
                if (command is null || string.IsNullOrWhiteSpace(command.Name))
                {
                    diagnostics.Add($"Plugin '{pluginId}' contributed a command without a name; it was ignored.");
                    continue;
                }

                if (!commandNames.Add(command.Name))
                {
                    diagnostics.Add($"Plugin '{pluginId}' contributed command '{command.Name}' twice; the duplicate was ignored.");
                    continue;
                }

                commands.Add(new Sdk.CommandDeclaration(
                    command.Name,
                    command.Description ?? string.Empty,
                    command.ArgumentHint ?? string.Empty));
            }
        }

        var workflows = new List<Sdk.WorkflowDeclaration>(set.Workflows?.Count ?? 0);
        var workflowNames = new HashSet<string>(StringComparer.Ordinal);
        if (set.Workflows is { } reportedWorkflows)
        {
            foreach (var workflow in reportedWorkflows)
            {
                if (workflow is null || string.IsNullOrWhiteSpace(workflow.Name))
                {
                    diagnostics.Add($"Plugin '{pluginId}' contributed a workflow without a name; it was ignored.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(workflow.Body))
                {
                    diagnostics.Add(
                        $"Plugin '{pluginId}' contributed workflow '{workflow.Name}' without a body; it was ignored.");
                    continue;
                }

                if (!workflowNames.Add(workflow.Name))
                {
                    diagnostics.Add(
                        $"Plugin '{pluginId}' contributed workflow '{workflow.Name}' twice; the duplicate was ignored.");
                    continue;
                }

                workflows.Add(new Sdk.WorkflowDeclaration(
                    workflow.Name, workflow.Description ?? string.Empty, workflow.Body));
            }
        }

        var agents = new List<Sdk.AgentDeclaration>(set.Agents?.Count ?? 0);
        var agentNames = new HashSet<string>(StringComparer.Ordinal);
        if (set.Agents is { } reportedAgents)
        {
            foreach (var agent in reportedAgents)
            {
                if (!TryValidateAgent(pluginId, agent, diagnostics, out var validated))
                {
                    continue;
                }

                if (!agentNames.Add(validated.Name))
                {
                    diagnostics.Add(
                        $"Plugin '{pluginId}' contributed agent '{validated.Name}' twice; the duplicate was ignored.");
                    continue;
                }

                agents.Add(validated);
            }
        }

        var prompts = new List<Sdk.PromptContributorDeclaration>(set.PromptContributors?.Count ?? 0);
        var promptIds = new HashSet<string>(StringComparer.Ordinal);
        if (set.PromptContributors is { } reportedPrompts)
        {
            foreach (var prompt in reportedPrompts)
            {
                if (prompt is null || string.IsNullOrWhiteSpace(prompt.Id))
                {
                    diagnostics.Add(
                        $"Plugin '{pluginId}' contributed a prompt contributor without an id; it was ignored.");
                    continue;
                }

                if (!promptIds.Add(prompt.Id))
                {
                    diagnostics.Add(
                        $"Plugin '{pluginId}' contributed prompt contributor '{prompt.Id}' twice; the duplicate was ignored.");
                    continue;
                }

                prompts.Add(new Sdk.PromptContributorDeclaration(prompt.Id));
            }
        }

        if (set.Panels is { Count: > 0 })
        {
            diagnostics.Add(
                $"Plugin '{pluginId}' declared {set.Panels.Count} panel(s); declarative panel rendering is not part of this stage, so they were not registered.");
        }

        return new ValidatedContributions(commands, workflows, agents, prompts);
    }

    private static bool TryValidateAgent(
        string pluginId,
        Sdk.AgentDeclaration? agent,
        List<string> diagnostics,
        out Sdk.AgentDeclaration validated)
    {
        validated = null!;
        if (agent is null)
        {
            diagnostics.Add($"Plugin '{pluginId}' contributed an empty agent declaration; it was ignored.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            diagnostics.Add($"Plugin '{pluginId}' contributed an agent without a name; it was ignored.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(agent.Prompt))
        {
            diagnostics.Add($"Plugin '{pluginId}' contributed agent '{agent.Name}' without a prompt; it was ignored.");
            return false;
        }

        if (!TryConvertModel(agent.Model, out _, out var problem))
        {
            diagnostics.Add($"Plugin '{pluginId}' agent '{agent.Name}' was ignored: {problem}");
            return false;
        }

        if (agent.Model.MaxOutputTokens is <= 0)
        {
            diagnostics.Add(
                $"Plugin '{pluginId}' agent '{agent.Name}' was ignored: maxOutputTokens must be positive when set.");
            return false;
        }

        validated = agent;
        return true;
    }

    private static bool TryConvertModel(Sdk.AgentModelSettings? settings, out ModelSettings model, out string problem)
    {
        model = null!;
        problem = string.Empty;
        if (settings is null || string.IsNullOrWhiteSpace(settings.Model))
        {
            problem = "the agent declares no model.";
            return false;
        }

        if (!IsDefined(settings.Thinking))
        {
            problem = $"thinking effort '{(int)settings.Thinking}' is not a defined value.";
            return false;
        }

        model = new ModelSettings(
            settings.Model,
            settings.Thinking switch
            {
                Sdk.AgentThinkingEffort.Low => ThinkingEffort.Low,
                Sdk.AgentThinkingEffort.Medium => ThinkingEffort.Medium,
                Sdk.AgentThinkingEffort.High => ThinkingEffort.High,
                _ => ThinkingEffort.Off,
            },
            settings.MaxOutputTokens);
        return true;
    }

    /// <summary>Only the four declared values are accepted; an out-of-range ordinal from the wire is a protocol
    /// error, not something to clamp.</summary>
    internal static bool IsDefined(Sdk.AgentThinkingEffort effort) =>
        effort is Sdk.AgentThinkingEffort.Off
            or Sdk.AgentThinkingEffort.Low
            or Sdk.AgentThinkingEffort.Medium
            or Sdk.AgentThinkingEffort.High;

    internal static AgentDefinition ToAgent(Sdk.AgentDeclaration agent)
    {
        TryConvertModel(agent.Model, out var model, out _);
        IReadOnlyList<string>? toolNames = null;
        if (agent.ToolNames is { Count: > 0 } declared)
        {
            var names = new List<string>(declared.Count);
            foreach (var name in declared)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            toolNames = names.Count == 0 ? null : names;
        }

        return new AgentDefinition(agent.Name, agent.Description ?? string.Empty, agent.Prompt, model, toolNames);
    }

    internal static WorkflowDefinition ToWorkflow(Sdk.WorkflowDeclaration workflow) =>
        new(workflow.Name, workflow.Description ?? string.Empty, workflow.Body);

    internal static CommandDefinition ToCommandDefinition(Sdk.CommandDeclaration command) =>
        new(command.Name, command.Description ?? string.Empty,
            string.IsNullOrWhiteSpace(command.ArgumentHint) ? null : command.ArgumentHint);

    /// <summary>Structural comparison, so a re-registration of the same declarations keeps the existing proxies
    /// (and raises no change) instead of replacing them with equal instances.</summary>
    internal static bool Same(ValidatedContributions left, ValidatedContributions right) =>
        SameCommands(left.Commands, right.Commands) &&
        SameWorkflows(left.Workflows, right.Workflows) &&
        SameAgents(left.Agents, right.Agents) &&
        SamePrompts(left.Prompts, right.Prompts);

    private static bool SameCommands(
        IReadOnlyList<Sdk.CommandDeclaration> left, IReadOnlyList<Sdk.CommandDeclaration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal) ||
                !string.Equals(left[index].Description, right[index].Description, StringComparison.Ordinal) ||
                !string.Equals(left[index].ArgumentHint, right[index].ArgumentHint, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameWorkflows(
        IReadOnlyList<Sdk.WorkflowDeclaration> left, IReadOnlyList<Sdk.WorkflowDeclaration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal) ||
                !string.Equals(left[index].Description, right[index].Description, StringComparison.Ordinal) ||
                !string.Equals(left[index].Body, right[index].Body, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAgents(
        IReadOnlyList<Sdk.AgentDeclaration> left, IReadOnlyList<Sdk.AgentDeclaration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var one = left[index];
            var other = right[index];
            if (!string.Equals(one.Name, other.Name, StringComparison.Ordinal) ||
                !string.Equals(one.Description, other.Description, StringComparison.Ordinal) ||
                !string.Equals(one.Prompt, other.Prompt, StringComparison.Ordinal) ||
                one.Model.Thinking != other.Model.Thinking ||
                !string.Equals(one.Model.Model, other.Model.Model, StringComparison.Ordinal) ||
                one.Model.MaxOutputTokens != other.Model.MaxOutputTokens ||
                !SameNames(one.ToolNames, other.ToolNames))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SamePrompts(
        IReadOnlyList<Sdk.PromptContributorDeclaration> left, IReadOnlyList<Sdk.PromptContributorDeclaration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameNames(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
