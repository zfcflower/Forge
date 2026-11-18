using Forge.Abstractions;
using Forge.Core.Agents;

namespace Forge.App.Agents;

/// <summary>How much of the session's context something takes: its length in characters and the estimated tokens.</summary>
internal readonly record struct UsageSize(int Chars, int Tokens)
{
    private static readonly ApproximateTokenCounter Counter = new();

    /// <summary>The size of one text, counted with the same estimate the compaction budget uses.</summary>
    public static UsageSize Of(string text) => new(text.Length, Counter.Count(text));

    public static UsageSize operator +(UsageSize left, UsageSize right) => new(left.Chars + right.Chars, left.Tokens + right.Tokens);
}

/// <summary>The built-in and plugin tools of one group (<see cref="ToolGroups"/>) as the model is sent their definitions.</summary>
internal sealed record ToolUsageRow(string Group, int Tools, UsageSize Size);

/// <summary>The tools one MCP server contributes to the request.</summary>
internal sealed record McpUsageRow(string Server, int Tools, UsageSize Size);

/// <summary>Which snapshot source a <see cref="PromptUsageRow"/> comes from.</summary>
internal enum PromptUsageKind
{
    /// <summary>The system prompt slot (the built-in text or the user's override).</summary>
    System,

    /// <summary>The user-level <c>AGENTS.md</c>.</summary>
    UserAgents,

    /// <summary>The workspace's <c>AGENTS.md</c>.</summary>
    WorkspaceAgents,

    /// <summary>The routing rule and the skill index the root agent is given.</summary>
    Skills,

    /// <summary>What the chat can draw (diagrams, images, file links).</summary>
    Rendering,
}

internal sealed record PromptUsageRow(PromptUsageKind Kind, UsageSize Size);

/// <summary>What a session's tools, MCP servers and prompt sources cost in context, taken from the host's own snapshot.</summary>
internal sealed record ResourceUsage(
    IReadOnlyList<ToolUsageRow> Tools,
    IReadOnlyList<McpUsageRow> McpServers,
    IReadOnlyList<PromptUsageRow> Prompts)
{
    public static ResourceUsage Empty { get; } = new([], [], []);
}

/// <summary>Pure sizing of what a built host holds; the session only supplies the inputs.</summary>
internal static class ResourceUsageBuilder
{
    /// <summary>
    /// Sizes every tool the host registered. <paramref name="descriptors"/> says where each one belongs (a built-in group, a
    /// plugin, or an MCP server); a registered tool with no descriptor is not counted, because nothing says what it is.
    /// MCP tools are returned apart so the server table owns them and the group table never counts them twice.
    /// </summary>
    public static (IReadOnlyList<ToolUsageRow> Groups, IReadOnlyList<McpUsageRow> Servers) Tools(
        IReadOnlyList<ToolDescriptor> descriptors,
        IEnumerable<ITool> registered)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(registered);
        var byName = new Dictionary<string, ToolDescriptor>(descriptors.Count, StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            byName[descriptor.Name] = descriptor;
        }

        var groups = new Dictionary<string, (int Tools, UsageSize Size)>(StringComparer.Ordinal);
        var servers = new Dictionary<string, (int Tools, UsageSize Size)>(StringComparer.Ordinal);
        foreach (var tool in registered)
        {
            var definition = tool.Definition;
            if (!byName.TryGetValue(definition.Name, out var descriptor))
            {
                continue;
            }

            var size = UsageSize.Of(string.Concat(definition.Name, definition.Description, definition.InputSchemaJson));
            var (target, key) = descriptor.Origin == ToolOrigin.Mcp
                ? (servers, descriptor.Source ?? descriptor.Group)
                : (groups, descriptor.Origin == ToolOrigin.Extension ? ToolGroups.Extensions : descriptor.Group);
            target.TryGetValue(key, out var current);
            target[key] = (current.Tools + 1, current.Size + size);
        }

        return (
            [.. groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ToolUsageRow(pair.Key, pair.Value.Tools, pair.Value.Size))],
            [.. servers.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new McpUsageRow(pair.Key, pair.Value.Tools, pair.Value.Size))]);
    }

    /// <summary>The prompt sources the host's snapshot holds; empty ones are left out.</summary>
    public static IReadOnlyList<PromptUsageRow> Prompts(PromptLayers? layers, string orchestration, string chatRendering)
    {
        var rows = new List<PromptUsageRow>(5);
        Add(rows, PromptUsageKind.System, layers?.System);
        Add(rows, PromptUsageKind.UserAgents, layers?.GlobalAgent);
        Add(rows, PromptUsageKind.WorkspaceAgents, layers?.ProjectAgent);
        Add(rows, PromptUsageKind.Skills, orchestration);
        Add(rows, PromptUsageKind.Rendering, chatRendering);
        return rows;
    }

    private static void Add(List<PromptUsageRow> rows, PromptUsageKind kind, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            rows.Add(new PromptUsageRow(kind, UsageSize.Of(text)));
        }
    }
}
