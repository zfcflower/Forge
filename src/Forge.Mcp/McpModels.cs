using System.Text.Json;

namespace Forge.Mcp;

public sealed record McpServerInfo(string Name, string Version, string? Title = null);

public sealed record McpServerCapabilities(
    bool Tools = false,
    bool ToolsListChanged = false,
    bool Resources = false,
    bool ResourcesListChanged = false,
    bool ResourcesSubscribe = false,
    bool Prompts = false,
    bool PromptsListChanged = false,
    bool Logging = false,
    bool Completions = false)
{
    public static McpServerCapabilities None { get; } = new();

    internal static McpServerCapabilities Parse(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        static bool Present(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.True;

        static bool Flag(JsonElement parent, string name, string flag) =>
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(flag, out var set) &&
            set.ValueKind == JsonValueKind.True;

        return new McpServerCapabilities(
            Tools: Present(capabilities, "tools"),
            ToolsListChanged: Flag(capabilities, "tools", "listChanged"),
            Resources: Present(capabilities, "resources"),
            ResourcesListChanged: Flag(capabilities, "resources", "listChanged"),
            ResourcesSubscribe: Flag(capabilities, "resources", "subscribe"),
            Prompts: Present(capabilities, "prompts"),
            PromptsListChanged: Flag(capabilities, "prompts", "listChanged"),
            Logging: Present(capabilities, "logging"),
            Completions: Present(capabilities, "completions"));
    }
}

/// <summary>A flattened view of one MCP content block; <see cref="Text"/> is the display form.</summary>
public sealed record McpContent(string Type, string Text, string? MimeType = null, string? Uri = null, long? SizeBytes = null);

public sealed record McpToolInfo(string Name, string? Description, string InputSchemaJson, string? Title = null);

public sealed record McpToolCallResult(string Text, bool IsError = false, IReadOnlyList<McpContent>? Content = null);

public sealed record McpResourceInfo(string Uri, string? Name = null, string? Description = null, string? MimeType = null, long? Size = null);

public sealed record McpResourceResult(string Text, IReadOnlyList<McpContent>? Content = null);

public sealed record McpPromptArgument(string Name, string? Description = null, bool Required = false);

public sealed record McpPromptInfo(string Name, string? Description = null, IReadOnlyList<McpPromptArgument>? Arguments = null);

public sealed record McpPromptMessage(string Role, string Text);

public sealed record McpPromptResult(string Text, string? Description = null, IReadOnlyList<McpPromptMessage>? Messages = null);

public sealed record McpRoot(string Uri, string? Name = null);

public sealed record McpNotification(string Method, string ParamsJson);

public sealed class McpClientOptions
{
    public string ClientName { get; init; } = "forge";

    public string ClientVersion { get; init; } = McpProtocol.ClientVersion;

    public string ProtocolVersion { get; init; } = McpProtocol.Version;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public IReadOnlyList<McpRoot> Roots { get; init; } = [];

    public int MaxListPages { get; init; } = 100;

    /// <summary>False when the caller keeps the transport across client instances.</summary>
    public bool DisposeTransport { get; init; } = true;
}
