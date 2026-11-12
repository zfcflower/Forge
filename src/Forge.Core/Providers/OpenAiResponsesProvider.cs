using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// OpenAI Responses API wire format (<c>POST {baseUrl}/responses</c>). Unlike Chat Completions the conversation is a list
/// of typed items rather than role/content messages, so tool calls and tool results travel as their own items and the
/// system prompt is a top-level <c>instructions</c> field.
/// </summary>
public sealed class OpenAiResponsesProvider : HttpChatProvider
{
    private const string FunctionCallItem = "function_call";

    public OpenAiResponsesProvider(ProviderConfig config, HttpClient http, TimeSpan? idleTimeout = null)
        : base(config, http, idleTimeout)
    {
    }

    protected override string Endpoint => "responses";

    protected override void ApplyAuth(HttpRequestMessage request)
    {
        if (Config.AuthStyle == ProviderAuthStyle.None)
        {
            // A gateway with its own auth scheme: the caller's headers are the only credential.
            return;
        }

        var apiKey = Config.ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    protected override byte[] BuildBody(ChatRequest request) => ProviderJson.Write(writer =>
    {
        writer.WriteString("model", request.Model.Model);

        if (Instructions(request) is { Length: > 0 } instructions)
        {
            writer.WriteString("instructions", instructions);
        }

        WriteInput(writer, request);

        if (request.Tools.Count > 0)
        {
            WriteTools(writer, request.Tools);
        }

        if (request.Model.MaxOutputTokens is { } maxOutputTokens)
        {
            writer.WriteNumber("max_output_tokens", maxOutputTokens);
        }

        if (MapReasoningEffort(request.Model.Thinking) is { } effort)
        {
            // The effort rides in its own object; a null member here means the model must reason at its default, which is
            // why "off" omits the object entirely instead of sending an effort name.
            writer.WriteStartObject("reasoning");
            writer.WriteString("effort", effort);
            writer.WriteEndObject();
        }

        writer.WriteBoolean("stream", true);

        // Nothing is kept on the server: the transcript lives in the session, and a stored response would outlive it.
        writer.WriteBoolean("store", false);
    });

    /// <summary>The system prompt, plus any system-role message the session appended after it.</summary>
    private static string? Instructions(ChatRequest request)
    {
        StringBuilder? builder = null;
        if (request.SystemPrompt.Length > 0)
        {
            builder = new StringBuilder(request.SystemPrompt);
        }

        foreach (var message in request.Messages)
        {
            if (message.Role != MessageRole.System || ProviderJson.Text(message.Content) is not { Length: > 0 } text)
            {
                continue;
            }

            if (builder is null)
            {
                builder = new StringBuilder();
            }
            else
            {
                builder.Append("\n\n");
            }

            builder.Append(text);
        }

        return builder?.ToString();
    }

    /// <summary>
    /// One item per content block: text becomes an input message, a tool call a <c>function_call</c> item and a tool result
    /// a <c>function_call_output</c> item keyed by the same call id. Nothing is merged, because the API reads items in order.
    /// </summary>
    private static void WriteInput(Utf8JsonWriter writer, ChatRequest request)
    {
        writer.WriteStartArray("input");
        foreach (var message in request.Messages)
        {
            if (message.Role == MessageRole.System)
            {
                continue;
            }

            // A tool result answers the call, so it opens the turn the way the other adapters order it.
            WriteToolResults(writer, message.Content);

            if (ProviderJson.Text(message.Content) is { Length: > 0 } text)
            {
                WriteTextItem(writer, message.Role == MessageRole.Assistant ? "assistant" : "user", text);
            }

            if (message.Role != MessageRole.Assistant)
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is ToolCallBlock call)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", FunctionCallItem);
                    writer.WriteString("call_id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                }
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteTextItem(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "input_text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
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
            writer.WriteString("type", "function_call_output");
            writer.WriteString("call_id", result.CallId);
            writer.WriteString("output", result.Content);
            writer.WriteEndObject();
        }
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            // Responses declares a function tool flat; there is no nested "function" object as in Chat Completions.
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            ProviderJson.WriteRaw(writer, tool.InputSchemaJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // The effort names the Responses protocol defines. It has no step above "xhigh", so Max is clamped down to it rather
    // than sent as an unknown value the server would reject.
    private static string? MapReasoningEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "xhigh",
        _ => null,
    };

    protected override async IAsyncEnumerable<ChatStreamEvent> ParseAsync(
        SseReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<ToolCallState?>();
        var callsById = new Dictionary<string, ToolCallState>(StringComparer.Ordinal);
        var usage = new ResponseUsage();
        string? status = null;
        string? incompleteReason = null;
        var finished = false;

        while (true)
        {
            var evt = await ReadEventAsync(reader, ct).ConfigureAwait(false);
            if (evt is not { } sse)
            {
                break;
            }

            var data = sse.Data;
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var document = ParseChunk(data);
            var root = document.RootElement;
            var type = ProviderJson.Str(root, "type") ?? sse.Event;

            switch (type)
            {
                case "error":
                    throw ErrorEvent(root);

                case "response.failed":
                    throw FailedResponse(root);

                case "response.output_item.added":
                {
                    if (!TryGetItem(root, out var item) || ProviderJson.Str(item, "type") != FunctionCallItem)
                    {
                        break;
                    }

                    var state = Slot(calls, callsById, root);
                    ApplyCall(state, item);
                    Remember(callsById, state);
                    if (!state.Started && state.Name.Length > 0)
                    {
                        state.Started = true;
                        yield return new ToolCallStarted(state.Id, state.Name);
                    }

                    if (ProviderJson.Str(item, "arguments") is { Length: > 0 } initial)
                    {
                        state.Arguments.Append(initial);
                        yield return new ToolCallArgumentsDelta(state.Id, initial);
                    }

                    break;
                }

                case "response.output_item.done":
                {
                    if (!TryGetItem(root, out var item) || ProviderJson.Str(item, "type") != FunctionCallItem)
                    {
                        break;
                    }

                    var state = Slot(calls, callsById, root);
                    ApplyCall(state, item);
                    Remember(callsById, state);
                    if (!state.Started && state.Name.Length > 0)
                    {
                        state.Started = true;
                        yield return new ToolCallStarted(state.Id, state.Name);
                    }

                    if (ProviderJson.Str(item, "arguments") is { Length: > 0 } final
                        && ReplaceArguments(state, final) is { Length: > 0 } suffix)
                    {
                        yield return new ToolCallArgumentsDelta(state.Id, suffix);
                    }

                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    if (Find(calls, callsById, root) is not { } state
                        || ProviderJson.Str(root, "delta") is not { Length: > 0 } delta)
                    {
                        break;
                    }

                    state.Arguments.Append(delta);
                    yield return new ToolCallArgumentsDelta(state.Id, delta);
                    break;
                }

                case "response.function_call_arguments.done":
                {
                    if (Find(calls, callsById, root) is not { } state
                        || ProviderJson.Str(root, "arguments") is not { } arguments
                        || ReplaceArguments(state, arguments) is not { Length: > 0 } suffix)
                    {
                        break;
                    }

                    yield return new ToolCallArgumentsDelta(state.Id, suffix);
                    break;
                }

                case "response.output_text.delta":
                    if (ProviderJson.Str(root, "delta") is { Length: > 0 } textDelta)
                    {
                        text.Append(textDelta);
                        yield return new TextDelta(textDelta);
                    }

                    break;

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (ProviderJson.Str(root, "delta") is { Length: > 0 } thinkingDelta)
                    {
                        thinking.Append(thinkingDelta);
                        yield return new ThinkingDelta(thinkingDelta);
                    }

                    break;

                case "response.reasoning_summary_part.done":
                    // Summaries arrive as separate parts; keep the blank line the API shows between them.
                    thinking.Append("\n\n");
                    yield return new ThinkingDelta("\n\n");
                    break;

                case "response.completed":
                case "response.incomplete":
                    status = ReadResponse(root, usage) ?? (type == "response.incomplete" ? "incomplete" : status);
                    incompleteReason = IncompleteReason(root) ?? incompleteReason;
                    finished = true;
                    break;
            }

            if (finished)
            {
                break;
            }
        }

        if (!finished)
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

        var stop = MapStopReason(status, incompleteReason, blocks);
        yield return new MessageCompleted(new ChatMessage(MessageRole.Assistant, blocks), usage.ToTokenUsage(), stop);
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

    private static bool TryGetItem(JsonElement root, out JsonElement item)
    {
        if (root.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>The state for the item an event names, creating a slot at its <c>output_index</c> when it is new.</summary>
    private static ToolCallState Slot(
        List<ToolCallState?> calls,
        Dictionary<string, ToolCallState> callsById,
        JsonElement root)
    {
        var index = ProviderJson.Int(root, "output_index") is { } parsed && parsed >= 0 ? parsed : calls.Count;
        while (calls.Count <= index)
        {
            calls.Add(null);
        }

        var state = calls[index];
        if (state is null)
        {
            state = new ToolCallState();
            calls[index] = state;
            if (ProviderJson.Str(root, "item_id") is { Length: > 0 } eventItemId)
            {
                callsById[eventItemId] = state;
            }
        }

        return state;
    }

    /// <summary>
    /// The state for an event that only carries deltas. A delta for an item that was never announced is dropped, exactly as
    /// the reference adapter drops it: without the announcement there is no call id or name to attach it to.
    /// </summary>
    private static ToolCallState? Find(
        List<ToolCallState?> calls,
        Dictionary<string, ToolCallState> callsById,
        JsonElement root)
    {
        var index = ProviderJson.Int(root, "output_index");
        if (index is { } parsed && parsed >= 0 && parsed < calls.Count && calls[parsed] is { } byIndex)
        {
            return byIndex;
        }

        return ProviderJson.Str(root, "item_id") is { Length: > 0 } itemId && callsById.TryGetValue(itemId, out var byId)
            ? byId
            : null;
    }

    private static void Remember(Dictionary<string, ToolCallState> callsById, ToolCallState state)
    {
        if (state.ItemId.Length > 0)
        {
            callsById[state.ItemId] = state;
        }
    }

    private static void ApplyCall(ToolCallState state, JsonElement item)
    {
        if (ProviderJson.Str(item, "id") is { Length: > 0 } itemId)
        {
            state.ItemId = itemId;
        }

        // The tool call as the caller sees it is the call id, because that is what the matching function_call_output names.
        if (ProviderJson.Str(item, "call_id") is { Length: > 0 } callId)
        {
            state.Id = callId;
        }
        else if (state.Id.Length == 0)
        {
            state.Id = state.ItemId;
        }

        if (ProviderJson.Str(item, "name") is { Length: > 0 } name)
        {
            state.Name = name;
        }
    }

    /// <summary>
    /// Replaces the accumulated arguments with the authoritative string, returning the tail the caller has not seen yet so
    /// the delta stream stays consistent with the final call.
    /// </summary>
    private static string? ReplaceArguments(ToolCallState state, string arguments)
    {
        var accumulated = state.Arguments.ToString();
        if (string.Equals(accumulated, arguments, StringComparison.Ordinal))
        {
            return null;
        }

        if (arguments.StartsWith(accumulated, StringComparison.Ordinal))
        {
            var suffix = arguments[accumulated.Length..];
            state.Arguments.Append(suffix);
            return suffix;
        }

        state.Arguments.Clear();
        state.Arguments.Append(arguments);
        return null;
    }

    /// <summary>Reads usage and status from the <c>response</c> object a terminal event carries.</summary>
    private static string? ReadResponse(JsonElement root, ResponseUsage usage)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (response.TryGetProperty("usage", out var usageElement))
        {
            usage.Read(usageElement);
        }

        return ProviderJson.Str(response, "status");
    }

    private static string? IncompleteReason(JsonElement root) =>
        root.TryGetProperty("response", out var response)
        && response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty("incomplete_details", out var details)
            ? ProviderJson.Str(details, "reason")
            : null;

    private static StopReason MapStopReason(string? status, string? incompleteReason, List<ContentBlock> blocks) => status switch
    {
        "incomplete" => incompleteReason == "max_output_tokens" ? StopReason.MaxTokens : StopReason.Other,
        "failed" or "cancelled" => StopReason.Other,
        null => HasToolCalls(blocks) ? StopReason.ToolUse : StopReason.Other,
        _ => HasToolCalls(blocks) ? StopReason.ToolUse : StopReason.EndTurn,
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

    /// <summary>An <c>error</c> event; some servers put the error object under <c>error</c>, some at the top level.</summary>
    private ProviderException ErrorEvent(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        var message = ProviderJson.Str(error, "message") ?? root.GetRawText();
        var code = ProviderJson.Str(error, "code") ?? ProviderJson.Str(error, "type");
        var status = ProviderJson.Int(error, "status");
        var retryable = status is { } code2 ? ProviderErrors.IsRetryableStatus(code2) : IsRetryableCode(code);
        return new ProviderException($"{Id}: stream error ({code ?? "unknown"}): {message}", retryable, status);
    }

    /// <summary>A terminal <c>response.failed</c> event: the turn is over and the server said why.</summary>
    private ProviderException FailedResponse(JsonElement root)
    {
        root.TryGetProperty("response", out var response);
        var isObject = response.ValueKind == JsonValueKind.Object;
        JsonElement error = default;
        var hasError = isObject
                       && response.TryGetProperty("error", out error)
                       && error.ValueKind == JsonValueKind.Object;
        var code = hasError ? ProviderJson.Str(error, "code") : null;
        var status = hasError ? ProviderJson.Int(error, "status") : null;
        var reason = isObject && response.TryGetProperty("incomplete_details", out var details)
            ? ProviderJson.Str(details, "reason")
            : null;
        var detail = (hasError ? ProviderJson.Str(error, "message") : null)
                     ?? (reason is { Length: > 0 } ? $"incomplete: {reason}" : "no error details in response");
        var retryable = status is { } code2 ? ProviderErrors.IsRetryableStatus(code2) : IsRetryableCode(code);
        return new ProviderException($"{Id}: response failed ({code ?? "unknown"}): {detail}", retryable, status);
    }

    private static bool IsRetryableCode(string? code) =>
        code is not null
        && (code.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || code.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || code.Contains("server_error", StringComparison.OrdinalIgnoreCase)
            || code.Contains("timeout", StringComparison.OrdinalIgnoreCase));

    private sealed class ToolCallState
    {
        public string Id { get; set; } = string.Empty;

        public string ItemId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool Started { get; set; }

        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// Reads the Responses usage object. <c>InputTokens</c> includes cached input, as <c>input_tokens</c> does; some servers
    /// also break cache writes out under <c>input_tokens_details.cache_write_tokens</c>.
    /// </summary>
    private sealed class ResponseUsage
    {
        private int _input;
        private int _output;
        private int _cacheRead;
        private int _cacheWrite;

        public void Read(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _input = ProviderJson.Int(usage, "input_tokens") ?? _input;
            _output = ProviderJson.Int(usage, "output_tokens") ?? _output;
            if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                _cacheRead = ProviderJson.Int(details, "cached_tokens") ?? _cacheRead;
                _cacheWrite = ProviderJson.Int(details, "cache_write_tokens") ?? _cacheWrite;
            }
        }

        public TokenUsage ToTokenUsage() => new(_input, _output, _cacheRead, _cacheWrite);
    }
}
