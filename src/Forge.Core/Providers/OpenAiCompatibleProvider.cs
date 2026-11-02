using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>OpenAI Chat Completions wire format; also covers the compatible third-party servers.</summary>
public sealed class OpenAiCompatibleProvider : HttpChatProvider
{
    private const string StopReasonProperty = "finish_reason";

    public OpenAiCompatibleProvider(ProviderConfig config, HttpClient http, TimeSpan? idleTimeout = null)
        : base(config, http, idleTimeout)
    {
    }

    protected override string Endpoint => "chat/completions";

    protected override void ApplyAuth(HttpRequestMessage request)
    {
        var apiKey = Config.ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    protected override byte[] BuildBody(ChatRequest request) => ProviderJson.Write(writer =>
    {
        writer.WriteString("model", request.Model.Model);

        if (request.Model.MaxOutputTokens is { } maxOutputTokens)
        {
            writer.WriteNumber("max_completion_tokens", maxOutputTokens);
        }

        if (MapReasoningEffort(request.Model.Thinking) is { } effort)
        {
            writer.WriteString("reasoning_effort", effort);
        }

        writer.WriteBoolean("stream", true);
        writer.WriteStartObject("stream_options");
        writer.WriteBoolean("include_usage", true);
        writer.WriteEndObject();

        writer.WriteStartArray("messages");
        WriteMessages(writer, request);
        writer.WriteEndArray();

        if (request.Tools.Count > 0)
        {
            WriteTools(writer, request.Tools);
        }
    });

    private static void WriteMessages(Utf8JsonWriter writer, ChatRequest request)
    {
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.SystemPrompt);
            writer.WriteEndObject();
        }

        foreach (var message in request.Messages)
        {
            WriteMessage(writer, message);
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        switch (message.Role)
        {
            case MessageRole.System:
                WritePlain(writer, "system", ProviderJson.Text(message.Content) ?? string.Empty);
                break;

            case MessageRole.Assistant:
                WriteAssistant(writer, message.Content);
                break;

            case MessageRole.User:
                WriteToolResults(writer, message.Content);
                if (ProviderJson.Text(message.Content) is { } userText)
                {
                    WritePlain(writer, "user", userText);
                }

                break;

            default:
                WriteToolResults(writer, message.Content);
                break;
        }
    }

    private static void WritePlain(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static void WriteAssistant(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> blocks)
    {
        var text = ProviderJson.Text(blocks);
        var calls = 0;
        foreach (var block in blocks)
        {
            if (block is ToolCallBlock)
            {
                calls++;
            }
        }

        if (text is null && calls == 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        if (text is null)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", text);
        }

        if (calls > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (var block in blocks)
            {
                if (block is not ToolCallBlock call)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteToolResults(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is not ToolResultBlock result)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            writer.WriteString("tool_call_id", result.CallId);
            writer.WriteString("content", result.Content);
            writer.WriteEndObject();
        }
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            ProviderJson.WriteRaw(writer, tool.InputSchemaJson);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string? MapReasoningEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        _ => null,
    };

    protected override async IAsyncEnumerable<ChatStreamEvent> ParseAsync(
        SseReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<ToolCallState?>();
        var usage = TokenUsage.Empty;
        string? finishReason = null;
        var completed = false;

        while (true)
        {
            var evt = await ReadEventAsync(reader, ct).ConfigureAwait(false);
            if (evt is not { } sse)
            {
                break;
            }

            var data = sse.Data;
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                completed = true;
                break;
            }

            using var document = ParseChunk(data);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                throw StreamError(error);
            }

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = ReadUsage(usageElement, usage);
            }

            if (!root.TryGetProperty("choices", out var choices))
            {
                continue;
            }

            if (choices.ValueKind != JsonValueKind.Array)
            {
                throw Malformed(data);
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed(data);
                }

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (ProviderJson.Str(delta, "content") is { Length: > 0 } content)
                    {
                        text.Append(content);
                        yield return new TextDelta(content);
                    }

                    if ((ProviderJson.Str(delta, "reasoning_content") ?? ProviderJson.Str(delta, "reasoning")) is { Length: > 0 } reasoning)
                    {
                        thinking.Append(reasoning);
                        yield return new ThinkingDelta(reasoning);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            foreach (var emitted in ApplyToolCall(toolCall, calls))
                            {
                                yield return emitted;
                            }
                        }
                    }
                }

                if (ProviderJson.Str(choice, StopReasonProperty) is { Length: > 0 } reason)
                {
                    finishReason = reason;
                    completed = true;
                }
            }
        }

        if (!completed)
        {
            throw Truncated();
        }

        var blocks = new List<ContentBlock>(calls.Count + 2);
        if (thinking.Length > 0)
        {
            blocks.Add(new ThinkingBlock(thinking.ToString()));
        }

        if (text.Length > 0)
        {
            blocks.Add(new TextBlock(text.ToString()));
        }

        foreach (var call in calls)
        {
            if (call is null || call.Name.Length == 0)
            {
                continue;
            }

            blocks.Add(new ToolCallBlock(call.Id, call.Name, call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()));
        }

        var stop = MapStopReason(finishReason, blocks);
        yield return new MessageCompleted(new ChatMessage(MessageRole.Assistant, blocks), usage, stop);
    }

    private JsonDocument ParseChunk(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw Malformed(data, ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Malformed(data);
        }

        return document;
    }

    private static IEnumerable<ChatStreamEvent> ApplyToolCall(JsonElement element, List<ToolCallState?> calls)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var index = element.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsed)
            ? parsed
            : calls.Count;

        while (calls.Count <= index)
        {
            calls.Add(null);
        }

        var state = calls[index] ??= new ToolCallState();
        if (ProviderJson.Str(element, "id") is { Length: > 0 } id)
        {
            state.Id = id;
        }

        if (element.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (ProviderJson.Str(function, "name") is { Length: > 0 } name)
            {
                state.Name = name;
            }

            if (ProviderJson.Str(function, "arguments") is { Length: > 0 } arguments)
            {
                state.Arguments.Append(arguments);
                if (state.Name.Length > 0)
                {
                    if (!state.Started)
                    {
                        state.Started = true;
                        yield return new ToolCallStarted(state.Id, state.Name);
                    }

                    yield return new ToolCallArgumentsDelta(state.Id, arguments);
                }
            }
        }

        if (!state.Started && state.Name.Length > 0)
        {
            state.Started = true;
            yield return new ToolCallStarted(state.Id, state.Name);
        }
    }

    private static TokenUsage ReadUsage(JsonElement usage, TokenUsage previous)
    {
        var input = ProviderJson.Int(usage, "prompt_tokens") ?? previous.InputTokens;
        var output = ProviderJson.Int(usage, "completion_tokens") ?? previous.OutputTokens;

        var cacheRead = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            cacheRead = ProviderJson.Int(details, "cached_tokens") ?? 0;
        }

        if (cacheRead == 0)
        {
            cacheRead = ProviderJson.Int(usage, "cached_tokens") ?? previous.CacheReadTokens;
        }

        return new TokenUsage(input, output, cacheRead, previous.CacheWriteTokens);
    }

    private static StopReason MapStopReason(string? finishReason, List<ContentBlock> blocks) => finishReason switch
    {
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "stop" => HasToolCalls(blocks) ? StopReason.ToolUse : StopReason.EndTurn,
        null => HasToolCalls(blocks) ? StopReason.ToolUse : StopReason.Other,
        _ => StopReason.Other,
    };

    private static bool HasToolCalls(List<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is ToolCallBlock)
            {
                return true;
            }
        }

        return false;
    }

    private ProviderException StreamError(JsonElement error)
    {
        var message = error.ValueKind == JsonValueKind.Object
            ? ProviderJson.Str(error, "message") ?? error.GetRawText()
            : error.GetRawText();
        var status = error.ValueKind == JsonValueKind.Object ? ProviderJson.Int(error, "status") : null;
        var retryable = status is { } code && ProviderErrors.IsRetryableStatus(code);
        var exception = new ProviderException($"{Id}: stream error: {message}", retryable, status);
        return exception;
    }

    private sealed class ToolCallState
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool Started { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
