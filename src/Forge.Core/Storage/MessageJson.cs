using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// Hand-written JSON projection of <see cref="ChatMessage"/> used for the message log, the SQLite
/// <c>content_json</c> column and the JSONL event log. Field order is fixed, so equal messages
/// always serialize to identical bytes.
/// </summary>
public static class MessageJson
{
    private const string TypeKey = "type";
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteMessage(writer, message);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("role", RoleName(message.Role));
        writer.WriteStartArray("content");
        foreach (var block in message.Content)
        {
            WriteBlock(writer, block);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static ChatMessage Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A serialized message must be a JSON object.");

        var role = ParseRole(RequiredString(root, "role"));
        var content = new List<ContentBlock>(4);
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                content.Add(ReadBlock(block));
            }
        }

        return new ChatMessage(role, content);
    }

    public static string? SerializeUsage(TokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(96);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("input", usage.InputTokens);
            writer.WriteNumber("output", usage.OutputTokens);
            writer.WriteNumber("cache_read", usage.CacheReadTokens);
            writer.WriteNumber("cache_write", usage.CacheWriteTokens);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static TokenUsage? ParseUsage(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new TokenUsage(
            OptionalInt(root, "input"),
            OptionalInt(root, "output"),
            OptionalInt(root, "cache_read"),
            OptionalInt(root, "cache_write"));
    }

    /// <summary>Message text indexed for full-text search: prose, reasoning and tool output.</summary>
    public static string ExtractSearchText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StringBuilder(256);
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    Append(builder, text.Text);
                    break;
                case ThinkingBlock thinking:
                    Append(builder, thinking.Text);
                    break;
                case ToolResultBlock result:
                    Append(builder, result.Content);
                    break;
                case ImageBlock image:
                    Append(builder, ContentPlaceholders.ImagePlaceholder(image));
                    break;
                case FileBlock file:
                    Append(builder, ContentPlaceholders.FilePlaceholder(file));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Everything a model would see for this message; used for token accounting.</summary>
    public static string ExtractFullText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StringBuilder(256);
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    Append(builder, text.Text);
                    break;
                case ThinkingBlock thinking:
                    Append(builder, thinking.Text);
                    break;
                case ToolCallBlock call:
                    Append(builder, call.Name);
                    Append(builder, call.ArgumentsJson);
                    break;
                case ToolResultBlock result:
                    Append(builder, result.Content);
                    break;
                case ArtifactRefBlock artifact:
                    Append(builder, artifact.Description);
                    break;
                case ImageBlock image:
                    Append(builder, ContentPlaceholders.ImagePlaceholder(image));
                    break;
                case FileBlock file:
                    Append(builder, ContentPlaceholders.FilePlaceholder(file));
                    break;
            }
        }

        return builder.ToString();
    }

    public static string RoleName(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role."),
    };

    public static MessageRole ParseRole(string name) => name switch
    {
        "system" => MessageRole.System,
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "tool" => MessageRole.Tool,
        _ => throw new JsonException($"Unknown message role '{name}'."),
    };

    private static void Append(StringBuilder builder, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(text);
    }

    private static void WriteBlock(Utf8JsonWriter writer, ContentBlock block)
    {
        writer.WriteStartObject();
        switch (block)
        {
            case TextBlock text:
                writer.WriteString(TypeKey, "text");
                writer.WriteString("text", text.Text);
                break;
            case ThinkingBlock thinking:
                writer.WriteString(TypeKey, "thinking");
                writer.WriteString("text", thinking.Text);
                if (thinking.Signature is not null)
                {
                    writer.WriteString("signature", thinking.Signature);
                }

                break;
            case ToolCallBlock call:
                writer.WriteString(TypeKey, "tool_call");
                writer.WriteString("id", call.Id);
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                break;
            case ToolResultBlock result:
                writer.WriteString(TypeKey, "tool_result");
                writer.WriteString("call_id", result.CallId);
                // "content" is always the flattened text: a reader that predates images, and the search index, read only that.
                // "blocks" is written only when the result is more than one plain text block.
                writer.WriteString("content", result.Content);
                if (result.Blocks is not [TextBlock])
                {
                    writer.WriteStartArray("blocks");
                    foreach (var inner in result.Blocks)
                    {
                        WriteBlock(writer, inner);
                    }

                    writer.WriteEndArray();
                }

                if (result.IsError)
                {
                    writer.WriteBoolean("is_error", true);
                }

                break;
            case ArtifactRefBlock artifact:
                writer.WriteString(TypeKey, "artifact");
                writer.WriteString("artifact_id", artifact.ArtifactId);
                writer.WriteString("description", artifact.Description);
                writer.WriteNumber("size_bytes", artifact.SizeBytes);
                break;
            case ImageBlock image:
                writer.WriteString(TypeKey, "image");
                writer.WriteString("media_type", image.MediaType);
                writer.WriteString("data", image.Data);
                break;
            case FileBlock file:
                writer.WriteString(TypeKey, "file");
                writer.WriteString("media_type", file.MediaType);
                writer.WriteString("data", file.Data);
                if (file.Name is not null)
                {
                    writer.WriteString("name", file.Name);
                }

                break;
            default:
                throw new JsonException($"Unsupported content block '{block?.GetType().Name ?? "null"}'.");
        }

        writer.WriteEndObject();
    }

    private static ContentBlock ReadBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            throw new JsonException("A content block must be a JSON object.");

        var type = RequiredString(block, TypeKey);
        return type switch
        {
            "text" => new TextBlock(RequiredString(block, "text")),
            "thinking" => new ThinkingBlock(RequiredString(block, "text"), OptionalString(block, "signature")),
            "tool_call" => new ToolCallBlock(
                RequiredString(block, "id"),
                RequiredString(block, "name"),
                RequiredString(block, "arguments")),
            "tool_result" => ReadToolResult(block),
            "artifact" => new ArtifactRefBlock(
                RequiredString(block, "artifact_id"),
                RequiredString(block, "description"),
                OptionalLong(block, "size_bytes")),
            "image" => new ImageBlock(RequiredString(block, "media_type"), RequiredString(block, "data")),
            "file" => new FileBlock(
                RequiredString(block, "media_type"),
                RequiredString(block, "data"),
                OptionalString(block, "name")),
            _ => throw new JsonException($"Unknown content block type '{type}'."),
        };
    }

    private static ToolResultBlock ReadToolResult(JsonElement block)
    {
        var callId = RequiredString(block, "call_id");
        var isError = OptionalBool(block, "is_error");
        if (!block.TryGetProperty("blocks", out var blocks))
        {
            return new ToolResultBlock(callId, RequiredString(block, "content"), isError);
        }

        if (blocks.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Property 'blocks' is not an array.");
        }

        var content = new List<ContentBlock>(blocks.GetArrayLength());
        foreach (var inner in blocks.EnumerateArray())
        {
            var read = ReadBlock(inner);
            if (read is not (TextBlock or ImageBlock))
            {
                throw new JsonException("A tool result holds text and image blocks only.");
            }

            content.Add(read);
        }

        return new ToolResultBlock(callId, content, isError);
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Property '{name}' is missing or not a string.");

        return value.GetString() ?? throw new JsonException($"Property '{name}' is null.");
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long OptionalLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static int OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
}
