using Forge.Abstractions;

namespace Forge.Plugins;

/// <summary>What the currently loaded plugins contribute. Immutable; the host replaces it atomically.</summary>
public sealed record PluginRegistrations(
    IReadOnlyList<ITool> Tools,
    IReadOnlyList<ICommand> Commands,
    IReadOnlyList<WorkflowDefinition> Workflows,
    IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyList<IPromptContributor> PromptContributors,
    IReadOnlyList<PanelDefinition> Panels)
{
    public static PluginRegistrations Empty { get; } = new([], [], [], [], [], []);
}

/// <summary>Accepted contributions of one plugin, with the registration name captured once at load time.</summary>
internal sealed class AcceptedRegistrations
{
    public List<NamedTool> Tools { get; } = [];

    public List<NamedCommand> Commands { get; } = [];

    public List<NamedWorkflow> Workflows { get; } = [];

    public List<NamedAgent> Agents { get; } = [];

    public List<NamedPromptContributor> PromptContributors { get; } = [];

    public List<NamedPanel> Panels { get; } = [];

    public bool IsEmpty =>
        Tools.Count == 0 && Commands.Count == 0 && Workflows.Count == 0 &&
        Agents.Count == 0 && PromptContributors.Count == 0 && Panels.Count == 0;
}

internal sealed record NamedTool(string Name, ITool Tool);

internal sealed record NamedCommand(string Name, ICommand Command);

internal sealed record NamedWorkflow(string Name, WorkflowDefinition Workflow);

internal sealed record NamedAgent(string Name, AgentDefinition Agent);

internal sealed record NamedPromptContributor(string Id, IPromptContributor Contributor);

internal sealed record NamedPanel(string Id, PanelDefinition Panel);

/// <summary>Per-plugin registry handed to <see cref="IForgePlugin.Register"/>; nothing reaches the host until the plugin returns cleanly.</summary>
internal sealed class RecordingPluginRegistry : IPluginRegistry
{
    public List<ITool> Tools { get; } = [];

    public List<ICommand> Commands { get; } = [];

    public List<WorkflowDefinition> Workflows { get; } = [];

    public List<AgentDefinition> Agents { get; } = [];

    public List<IPromptContributor> PromptContributors { get; } = [];

    public List<PanelDefinition> Panels { get; } = [];

    public void AddTool(ITool tool) => Tools.Add(tool ?? throw new ArgumentNullException(nameof(tool)));

    public void AddCommand(ICommand command) => Commands.Add(command ?? throw new ArgumentNullException(nameof(command)));

    public void AddWorkflow(WorkflowDefinition workflow) => Workflows.Add(workflow ?? throw new ArgumentNullException(nameof(workflow)));

    public void AddAgent(AgentDefinition agent) => Agents.Add(agent ?? throw new ArgumentNullException(nameof(agent)));

    public void AddPromptContributor(IPromptContributor contributor) => PromptContributors.Add(contributor ?? throw new ArgumentNullException(nameof(contributor)));

    public void AddPanel(PanelDefinition panel) => Panels.Add(panel ?? throw new ArgumentNullException(nameof(panel)));
}
