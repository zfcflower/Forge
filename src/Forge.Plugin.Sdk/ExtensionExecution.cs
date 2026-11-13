using MessagePack;

namespace Forge.Plugin.Sdk;

/// <summary>Assigned by the main program; a worker cannot replace these values in a callback.</summary>
[MessagePackObject]
public sealed partial record ExtensionCallContext(
    [property: Key(0)] string RunId,
    [property: Key(1)] string SessionId,
    [property: Key(2)] string WorkingDirectory,
    [property: Key(3)] int Depth,
    [property: Key(4)] IReadOnlyList<string> PluginChain)
{
    /// <summary>Identity of the top-level invocation, preserved across nested calls; never a plugin name.</summary>
    [Key(5)]
    public string? RootRunId { get; init; }
}

[MessagePackObject]
public sealed partial record CommandOutput([property: Key(0)] string? Output = null, [property: Key(1)] string? PromptToSend = null, [property: Key(2)] bool IsError = false);

public sealed record CommandInvocation(string Arguments, ExtensionCallContext Context, IHostFacade Host);

public interface IPluginCommand
{
    CommandDeclaration Declaration { get; }
    Task<CommandOutput> ExecuteAsync(CommandInvocation invocation, CancellationToken ct);
}

public enum AgentThinkingEffort { Off, Low, Medium, High }

[MessagePackObject]
public sealed partial record AgentModelSettings(
    [property: Key(0)] string Model,
    [property: Key(1)] AgentThinkingEffort Thinking = AgentThinkingEffort.Off,
    [property: Key(2)] int? MaxOutputTokens = null);

[MessagePackObject]
public sealed partial record AgentDeclaration(
    [property: Key(0)] string Name,
    [property: Key(1)] string Description,
    [property: Key(2)] string Prompt,
    [property: Key(3)] AgentModelSettings Model,
    [property: Key(4)] IReadOnlyList<string>? ToolNames = null);

[MessagePackObject]
public sealed partial record PromptContributorDeclaration([property: Key(0)] string Id);

public sealed record PromptInvocation(ExtensionCallContext Context, IHostFacade Host);

public interface IPluginPromptContributor
{
    PromptContributorDeclaration Declaration { get; }
    Task<string?> GetTextAsync(PromptInvocation invocation, CancellationToken ct);
}

[MessagePackObject]
public sealed partial record PluginContributionSet
{
    [Key(0)]
    public IReadOnlyList<ToolDeclaration> Tools { get; init; } = [];
    [Key(1)]
    public IReadOnlyList<CommandDeclaration> Commands { get; init; } = [];
    [Key(2)]
    public IReadOnlyList<WorkflowDeclaration> Workflows { get; init; } = [];
    [Key(3)]
    public IReadOnlyList<AgentDeclaration> Agents { get; init; } = [];
    [Key(4)]
    public IReadOnlyList<PromptContributorDeclaration> PromptContributors { get; init; } = [];
    [Key(5)]
    public IReadOnlyList<PanelDeclaration> Panels { get; init; } = [];
    [Key(6)]
    public IReadOnlyList<Permission> Permissions { get; init; } = [];
}

[MessagePackObject]
public sealed partial record PluginStatusInfo(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] string Status,
    [property: Key(3)] string SourceHash,
    [property: Key(4)] string DependencyHash,
    [property: Key(5)] string SdkVersion,
    [property: Key(6)] IReadOnlyList<Permission> Permissions,
    [property: Key(7)] string? Error = null);

[MessagePackObject]
public sealed partial record PluginStatusResponse([property: Key(0)] IReadOnlyList<PluginStatusInfo> Plugins);

[MessagePackObject]
public sealed partial record PluginContributionsNotification([property: Key(0)] string PluginId, [property: Key(1)] PluginContributionSet Contributions);

/// <summary>Execution limits are supplied by trusted host configuration, not by script source.</summary>
[MessagePackObject]
public sealed partial record CodeExecutionOptions
{
    [Key(0)]
    public int MaxConcurrentToolCalls { get; init; } = 8;
    [Key(1)]
    public int MaxScriptChars { get; init; } = 100_000;
    [Key(2)]
    public int MaxResultChars { get; init; } = 40_000;
    [Key(3)]
    public int MaxInnerResultChars { get; init; } = 20_000;
    [Key(4)]
    public int MaxLogChars { get; init; } = 4_000;
    [Key(5)]
    public int MaxDiagnosticChars { get; init; } = 4_000;
    [Key(6)]
    public int MaxDiagnosticCount { get; init; } = 12;
    [Key(7)]
    public int CompilationCacheCapacity { get; init; } = 64;
}

[MessagePackObject]
public sealed partial record CodeExecutionRequest(
    [property: Key(0)] string Code,
    [property: Key(1)] ExtensionCallContext Context,
    [property: Key(2)] int TimeoutMilliseconds,
    [property: Key(3)] IReadOnlyList<ToolDeclaration> Tools,
    [property: Key(4)] CodeExecutionOptions Options);

/// <summary>A worker with an abandoned script must be terminated before its slot can be reused.</summary>
[MessagePackObject]
public sealed partial record CodeExecutionResult([property: Key(0)] string Content, [property: Key(1)] bool IsError = false, [property: Key(2)] bool RequiresTermination = false);

[MessagePackObject]
public sealed partial record CommandExecutionRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Command,
    [property: Key(2)] string Arguments,
    [property: Key(3)] ExtensionCallContext Context,
    [property: Key(4)] int TimeoutMilliseconds);

[MessagePackObject]
public sealed partial record PromptExecutionRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string ContributorId,
    [property: Key(2)] ExtensionCallContext Context,
    [property: Key(3)] int TimeoutMilliseconds);

[MessagePackObject]
public sealed partial record PromptOutput([property: Key(0)] string? Text);
