using Forge.Abstractions;
using Forge.Plugin.Sdk;

namespace Forge.HostClient;

public sealed record PluginPromptReference(string PluginId, string ContributorId);

public sealed record RunCodeProxyOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromSeconds(600);
    public CodeExecutionOptions Execution { get; init; } = new();
}

/// <summary>Non-UI extension contributions and the main-program run_code proxy.</summary>
public interface IExtensionContributionClient : IExtensionHostClient
{
    IReadOnlyList<ICommand> Commands { get; }
    IReadOnlyList<WorkflowDefinition> Workflows { get; }
    IReadOnlyList<AgentDefinition> Agents { get; }
    IReadOnlyList<PluginPromptReference> PromptContributors { get; }
    event Action? ContributionsChanged;

    ITool CreateRunCodeTool(IToolRegistry tools, RunCodeProxyOptions? options = null);

    /// <summary>Queries the host without opening its approval database. Throws when disconnected or when
    /// the response is invalid; an unavailable store is never represented as an empty successful response.</summary>
    Task<IReadOnlyList<PluginStatusInfo>> GetPluginStatusesAsync(CancellationToken ct);

    /// <summary>Returns null when the contributor is missing or unreachable. Caller cancellation propagates
    /// as <see cref="OperationCanceledException"/>.</summary>
    Task<string?> GetPluginPromptAsync(
        PluginPromptReference contributor,
        ToolContext context,
        CancellationToken ct);
}
