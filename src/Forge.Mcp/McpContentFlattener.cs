using System.Text;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>Turns MCP content blocks into the single text a model sees, keeping non-text blocks legible.</summary>
internal static class McpContentFlattener
{
    /// <summary>
    /// The largest image this side keeps as payload, in estimated decoded bytes. A safety boundary, not a quality
    /// one: an MCP server can hand back an unbounded <c>data</c> string, and everything kept rides through the
    /// storage, the agent context and the provider wire — five megabytes keeps screenshots and diagrams while
    /// capping how much one tool call can inflate a session. Larger images stay legible as their placeholder text.
    /// </summary>
    public const int MaxKeptImageBytes = 5 * 1024 * 1024;

    public static McpToolCallResult ToolResult(JsonElement result)
    {
        var content = ReadContent(result, "content");
        var text = Flatten(content);
        if (text.Length == 0 && result.TryGetProperty("structuredContent", out var structured) &&
            structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            text = structured.GetRawText();
        }

        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new McpToolCallResult(text, isError, content);
    }

    public static McpResourceResult ResourceResult(JsonElement result)
    {
        var content = ReadResourceContents(result, "contents");
        return new McpResourceResult(Flatten(content), content);
    }

    public static McpPromptResult PromptResult(JsonElement result)
    {
        var description = result.TryGetProperty("description", out var descriptionElement) &&
                          descriptionElement.ValueKind == JsonValueKind.String
            ? descriptionElement.GetString()
            : null;

        if (!result.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return new McpPromptResult(string.Empty, description);
        }

        var builder = new StringBuilder();
        var list = new List<McpPromptMessage>(messages.GetArrayLength());
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()!
                : "user";
            var text = message.TryGetProperty("content", out var contentElement)
                ? Flatten(ReadContent(contentElement))
                : string.Empty;
            list.Add(new McpPromptMessage(role, text));
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(role).Append(": ").Append(text);
        }

        return new McpPromptResult(builder.ToString(), description, list);
    }

    public static IReadOnlyList<McpContent> ReadContent(JsonElement result, string property)
    {
        if (!result.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<McpContent>(array.GetArrayLength());
        foreach (var block in array.EnumerateArray())
        {
            list.Add(ReadBlock(block));
        }

        return list;
    }

    private static IReadOnlyList<McpContent> ReadContent(JsonElement content) =>
        content.ValueKind == JsonValueKind.Array ? ReadContentFromArray(content) : [ReadBlock(content)];

    /// <summary><c>resources/read</c> returns bare resource contents, which carry no <c>type</c> discriminator.</summary>
    private static IReadOnlyList<McpContent> ReadResourceContents(JsonElement result, string property)
    {
        if (!result.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<McpContent>(array.GetArrayLength());
        foreach (var block in array.EnumerateArray())
        {
            list.Add(HasType(block) ? ReadBlock(block) : ReadResourceContent(block, "resource"));
        }

        return list;
    }

    private static bool HasType(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object &&
        block.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String;

    private static IReadOnlyList<McpContent> ReadContentFromArray(JsonElement array)
    {
        var list = new List<McpContent>(array.GetArrayLength());
        foreach (var block in array.EnumerateArray())
        {
            list.Add(ReadBlock(block));
        }

        return list;
    }

    private static McpContent ReadBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            return new McpContent("unknown", block.ValueKind == JsonValueKind.String ? block.GetString()! : block.GetRawText());
        }

        var type = block.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()!
            : "unknown";

        switch (type)
        {
            case "text":
                return new McpContent("text", block.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty);

            case "image":
            {
                var mimeType = ReadString(block, "mimeType");
                var size = EstimateBase64Bytes(ReadString(block, "data"));
                // Keep the payload only for a media type the app can display, within the size cap; everything
                // else (svg, oversized bitmaps, anything the server mislabels) stays placeholder-only.
                var data = mimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" && size <= MaxKeptImageBytes
                    ? ReadString(block, "data")
                    : null;
                return new McpContent(type, $"[{type}: {mimeType ?? "unknown type"}, {FormatSize(size)}]", mimeType, null, size, data);
            }

            case "audio":
            {
                var mimeType = ReadString(block, "mimeType");
                var size = EstimateBase64Bytes(ReadString(block, "data"));
                return new McpContent(type, $"[{type}: {mimeType ?? "unknown type"}, {FormatSize(size)}]", mimeType, null, size);
            }

            case "resource":
            {
                if (!block.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
                {
                    return new McpContent(type, "[resource: malformed]", null, ReadString(block, "uri"));
                }

                return ReadResourceContent(resource, type);
            }

            case "resource_link":
            {
                var uri = ReadString(block, "uri");
                var name = ReadString(block, "name");
                var mimeType = ReadString(block, "mimeType");
                var description = ReadString(block, "description");
                var label = name ?? uri ?? "unknown";
                var suffix = description is null ? string.Empty : $" — {description}";
                return new McpContent(type, $"[resource_link: {label} ({uri ?? "unknown"}{(mimeType is null ? string.Empty : ", " + mimeType)})]{suffix}", mimeType, uri);
            }

            default:
                return new McpContent(type, $"[{type}: {Truncate(block.GetRawText(), 512)}]");
        }
    }

    private static McpContent ReadResourceContent(JsonElement resource, string type)
    {
        if (resource.ValueKind != JsonValueKind.Object)
        {
            return new McpContent(type, $"[{type}: malformed]");
        }

        var uri = ReadString(resource, "uri");
        var mimeType = ReadString(resource, "mimeType");
        if (resource.TryGetProperty("text", out var resourceText) && resourceText.ValueKind == JsonValueKind.String)
        {
            var body = resourceText.GetString()!;
            var header = uri is null ? string.Empty : $"[resource {uri}]\n";
            return new McpContent(type, header + body, mimeType, uri, body.Length);
        }

        var blob = ReadString(resource, "blob");
        var size = EstimateBase64Bytes(blob);
        return new McpContent(type, $"[resource: {uri ?? "unknown"} ({mimeType ?? "unknown type"}, {FormatSize(size)})]", mimeType, uri, size);
    }

    private static string Flatten(IReadOnlyList<McpContent> content)
    {
        if (content.Count == 0)
        {
            return string.Empty;
        }

        if (content.Count == 1)
        {
            return content[0].Text;
        }

        var builder = new StringBuilder();
        foreach (var item in content)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(item.Text);
        }

        return builder.ToString();
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long EstimateBase64Bytes(string? data) => data is null ? 0 : data.Length / 4 * 3;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MiB",
        >= 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes} B",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
