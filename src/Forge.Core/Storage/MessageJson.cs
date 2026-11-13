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
                    Append(builder, ImagePlaceholder(image));
                    break;
                case FileBlock file:
                    Append(builder, FilePlaceholder(file));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The text that stands in for an image anywhere text is wanted (search, summaries, retrieval):
    /// <c>[image: image/png, 123 KB]</c>. Never the base64 itself.
    /// </summary>
    public static string ImagePlaceholder(ImageBlock image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var kilobytes = Math.Max(1, (DecodedLength(image.Data) + 1023) / 1024);
        return string.Concat("[image: ", image.MediaType, ", ", kilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture), " KB]");
    }

    /// <summary>
    /// The text that stands in for an attached file anywhere text is wanted (search, summaries, retrieval):
    /// <c>[file: report.pdf, 123 KB]</c>, or the media type when the file has no name. Never the base64 itself.
    /// </summary>
    public static string FilePlaceholder(FileBlock file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var kilobytes = Math.Max(1, (DecodedLength(file.Data) + 1023) / 1024);
        var label = string.IsNullOrWhiteSpace(file.Name) ? file.MediaType : file.Name;
        return string.Concat("[file: ", label, ", ", kilobytes.ToString(System.Globalization.CultureInfo.InvariantCulture), " KB]");
    }

    /// <summary>Decoded byte count of a base64 string, from its length and padding alone (nothing is decoded).</summary>
    public static long DecodedLength(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        var length = base64.Length;
        if (length == 0)
        {
            return 0;
        }

        var padding = base64[length - 1] == '=' ? (length > 1 && base64[length - 2] == '=' ? 2 : 1) : 0;
        return Math.Max(0, (long)length / 4 * 3 + (length % 4 * 3 / 4) - padding);
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
                    Append(builder, ImagePlaceholder(image));
                    break;
                case FileBlock file:
                    Append(builder, FilePlaceholder(file));
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
                writer.WriteString("content", result.Content);
                if (result.IsError)
                {
                    writer.WriteBoolean("is_error", true);
                }

                // A text-only result is a single TextBlock, and its JSON is exactly the old shape — no "blocks"
                // property — so everything written before this change stays byte-identical. The array appears only
                // when the result carries more than one block or a non-text block, and reuses the same block shapes
                // a message body uses.
                if (result.Blocks.Count > 1 || (result.Blocks.Count == 1 && result.Blocks[0] is not TextBlock))
                {
                    writer.WriteStartArray("blocks");
                    foreach (var nested in result.Blocks)
                    {
                        WriteBlock(writer, nested);
                    }

                    writer.WriteEndArray();
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

    private static ContentBlock ReadToolResult(JsonElement block)
    {
        var callId = RequiredString(block, "call_id");
        var isError = OptionalBool(block, "is_error");
        if (block.TryGetProperty("blocks", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            // The same shapes a message body uses; an unknown nested type throws the existing JsonException
            // instead of being dropped.
            var blocks = new List<ContentBlock>(nested.GetArrayLength());
            foreach (var item in nested.EnumerateArray())
            {
                blocks.Add(ReadBlock(item));
            }

            return new ToolResultBlock(callId, blocks, isError);
        }

        // Old shape: the flat text form written before "blocks" existed.
        return new ToolResultBlock(callId, RequiredString(block, "content"), isError);
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
