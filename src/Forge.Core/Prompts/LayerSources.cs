using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>Text of the global and project agent layers; either is null when the file is absent or blank.</summary>
public sealed record AgentLayerText(string? GlobalAgent, string? ProjectAgent)
{
    public static AgentLayerText Empty { get; } = new(null, null);
}

/// <summary>
/// Resolves the global agent layer from <c>&lt;userDir&gt;/AGENTS.md</c> and the project agent layer from
/// <c>&lt;workspace&gt;/AGENTS.md</c>, falling back to <c>&lt;workspace&gt;/.forge/AGENTS.md</c>.
/// </summary>
public static class LayerSources
{
    public const string AgentsFileName = "AGENTS.md";
    public const string ForgeDirectoryName = ".forge";

    public static AgentLayerText Load(string? userDirectory, string? workspaceDirectory) =>
        new(LoadGlobalAgent(userDirectory), LoadProjectAgent(workspaceDirectory));

    public static async Task<AgentLayerText> LoadAsync(
        string? userDirectory,
        string? workspaceDirectory,
        CancellationToken ct = default)
    {
        var global = LoadGlobalAgentAsync(userDirectory, ct);
        var project = LoadProjectAgentAsync(workspaceDirectory, ct);
        return new AgentLayerText(await global.ConfigureAwait(false), await project.ConfigureAwait(false));
    }

    public static PromptLayers Resolve(string systemPrompt, string? userDirectory, string? workspaceDirectory)
    {
        var layers = Load(userDirectory, workspaceDirectory);
        return new PromptLayers(systemPrompt, layers.GlobalAgent, layers.ProjectAgent);
    }

    public static async Task<PromptLayers> ResolveAsync(
        string systemPrompt,
        string? userDirectory,
        string? workspaceDirectory,
        CancellationToken ct = default)
    {
        var layers = await LoadAsync(userDirectory, workspaceDirectory, ct).ConfigureAwait(false);
        return new PromptLayers(systemPrompt, layers.GlobalAgent, layers.ProjectAgent);
    }

    /// <summary>Global agent layer: <c>&lt;userDir&gt;/AGENTS.md</c>.</summary>
    public static string? LoadGlobalAgent(string? userDirectory) =>
        userDirectory is null ? null : ReadLayer(Path.Combine(userDirectory, AgentsFileName));

    public static Task<string?> LoadGlobalAgentAsync(string? userDirectory, CancellationToken ct = default) =>
        userDirectory is null ? Task.FromResult<string?>(null) : ReadLayerAsync(Path.Combine(userDirectory, AgentsFileName), ct);

    /// <summary>Project agent layer: <c>&lt;workspace&gt;/AGENTS.md</c>, then <c>&lt;workspace&gt;/.forge/AGENTS.md</c>.</summary>
    public static string? LoadProjectAgent(string? workspaceDirectory)
    {
        if (workspaceDirectory is null)
        {
            return null;
        }

        return ReadLayer(Path.Combine(workspaceDirectory, AgentsFileName))
            ?? ReadLayer(Path.Combine(workspaceDirectory, ForgeDirectoryName, AgentsFileName));
    }

    public static async Task<string?> LoadProjectAgentAsync(string? workspaceDirectory, CancellationToken ct = default)
    {
        if (workspaceDirectory is null)
        {
            return null;
        }

        var primary = await ReadLayerAsync(Path.Combine(workspaceDirectory, AgentsFileName), ct).ConfigureAwait(false);
        return primary
            ?? await ReadLayerAsync(Path.Combine(workspaceDirectory, ForgeDirectoryName, AgentsFileName), ct).ConfigureAwait(false);
    }

    private static string? ReadLayer(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static async Task<string?> ReadLayerAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
