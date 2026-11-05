using System.Text.Json.Serialization;

namespace Forge.Plugin.Sdk;

/// <summary>Assigned by the main program; a worker cannot replace these values in a callback.</summary>
public sealed record ExtensionCallContext(
    string RunId,
    string SessionId,
    string WorkingDirectory,
    int Depth,
    IReadOnlyList<string> PluginChain)
{
    /// <summary>Identity of the top-level invocation, preserved across nested calls; never a plugin name.</summary>
    public string? RootRunId { get; init; }
}

public sealed record CommandOutput(string? Output = null, string? PromptToSend = null, bool IsError = false);

public sealed record CommandInvocation(string Arguments, ExtensionCallContext Context, IHostFacade Host);

public interface IPluginCommand
{
    CommandDeclaration Declaration { get; }
    Task<CommandOutput> ExecuteAsync(CommandInvocation invocation, CancellationToken ct);
}

public enum AgentThinkingEffort { Off, Low, Medium, High }

public sealed record AgentModelSettings(
    string Model,
    AgentThinkingEffort Thinking = AgentThinkingEffort.Off,
    int? MaxOutputTokens = null);

public sealed record AgentDeclaration(
    string Name,
    string Description,
    string Prompt,
    AgentModelSettings Model,
    IReadOnlyList<string>? ToolNames = null);

public sealed record PromptContributorDeclaration(string Id);

public sealed record PromptInvocation(ExtensionCallContext Context, IHostFacade Host);

public interface IPluginPromptContributor
{
    PromptContributorDeclaration Declaration { get; }
    Task<string?> GetTextAsync(PromptInvocation invocation, CancellationToken ct);
}

public sealed record PluginContributionSet
{
    public IReadOnlyList<ToolDeclaration> Tools { get; init; } = [];
    public IReadOnlyList<CommandDeclaration> Commands { get; init; } = [];
    public IReadOnlyList<WorkflowDeclaration> Workflows { get; init; } = [];
    public IReadOnlyList<AgentDeclaration> Agents { get; init; } = [];
    public IReadOnlyList<PromptContributorDeclaration> PromptContributors { get; init; } = [];
    public IReadOnlyList<PanelDeclaration> Panels { get; init; } = [];
    public IReadOnlyList<Permission> Permissions { get; init; } = [];
}

public sealed record PluginStatusInfo(
    string PluginId,
    string Version,
    string Status,
    string SourceHash,
    string DependencyHash,
    string SdkVersion,
    IReadOnlyList<Permission> Permissions,
    string? Error = null);

public sealed record PluginStatusResponse(IReadOnlyList<PluginStatusInfo> Plugins);

public sealed record PluginContributionsNotification(string PluginId, PluginContributionSet Contributions);

/// <summary>Execution limits are supplied by trusted host configuration, not by script source.</summary>
public sealed record CodeExecutionOptions
{
    public int MaxConcurrentToolCalls { get; init; } = 8;
    public int MaxScriptChars { get; init; } = 100_000;
    public int MaxResultChars { get; init; } = 40_000;
    public int MaxInnerResultChars { get; init; } = 20_000;
    public int MaxLogChars { get; init; } = 4_000;
    public int MaxDiagnosticChars { get; init; } = 4_000;
    public int MaxDiagnosticCount { get; init; } = 12;
    public int CompilationCacheCapacity { get; init; } = 64;
}

public sealed record CodeExecutionRequest(
    string Code,
    ExtensionCallContext Context,
    int TimeoutMilliseconds,
    IReadOnlyList<ToolDeclaration> Tools,
    CodeExecutionOptions Options);

/// <summary>A worker with an abandoned script must be terminated before its slot can be reused.</summary>
public sealed record CodeExecutionResult(string Content, bool IsError = false, bool RequiresTermination = false);

public sealed record CommandExecutionRequest(
    string PluginId,
    string Command,
    string Arguments,
    ExtensionCallContext Context,
    int TimeoutMilliseconds);

public sealed record PromptExecutionRequest(
    string PluginId,
    string ContributorId,
    ExtensionCallContext Context,
    int TimeoutMilliseconds);

public sealed record PromptOutput(string? Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionCallContext))]
[JsonSerializable(typeof(PluginContributionSet))]
[JsonSerializable(typeof(PluginStatusResponse))]
[JsonSerializable(typeof(PluginContributionsNotification))]
[JsonSerializable(typeof(CodeExecutionRequest))]
[JsonSerializable(typeof(CodeExecutionResult))]
[JsonSerializable(typeof(CommandExecutionRequest))]
[JsonSerializable(typeof(CommandOutput))]
[JsonSerializable(typeof(PromptExecutionRequest))]
[JsonSerializable(typeof(PromptOutput))]
public partial class ExtensionExecutionJsonContext : JsonSerializerContext
{
}
