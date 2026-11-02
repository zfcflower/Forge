namespace Forge.Abstractions;

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string EntryAssembly,
    string EntryType,
    string? Description = null);

public sealed record CommandDefinition(string Name, string Description, string? ArgumentHint = null);

public sealed record CommandResult(string? Output = null, string? PromptToSend = null, bool IsError = false);

public interface ICommand
{
    CommandDefinition Definition { get; }
    ValueTask<CommandResult> ExecuteAsync(string arguments, CommandContext context, CancellationToken ct);
}

public sealed record CommandContext(string SessionId, string WorkingDirectory);

/// <summary>A workflow is a Markdown skill: frontmatter (name, description) plus step text that an agent reads and follows.</summary>
public sealed record WorkflowDefinition(string Name, string Description, string Body);

/// <summary>Contributes system/global/project prompt text from a plugin; null means no contribution.</summary>
public interface IPromptContributor
{
    string Id { get; }
    string? GetText(string workspacePath);
}

/// <summary>A UI panel contributed by a plugin. The view factory returns a UI-toolkit control; the app host interprets it.</summary>
public sealed record PanelDefinition(string Id, string Title, Func<object> CreateView);

public interface IPluginRegistry
{
    void AddTool(ITool tool);
    void AddCommand(ICommand command);
    void AddWorkflow(WorkflowDefinition workflow);
    void AddAgent(AgentDefinition agent);
    void AddPromptContributor(IPromptContributor contributor);
    void AddPanel(PanelDefinition panel);
}

public interface IForgePlugin
{
    void Register(IPluginRegistry registry, PluginContext context);
}

/// <summary>Host facilities a plugin may use at registration time.</summary>
public sealed record PluginContext(string PluginDirectory, string DataDirectory, IServiceProvider Services);
