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

    /// <summary>
    /// The line above the images of a tool result: this protocol's tool message is text-only, so the pictures follow in their own
    /// user turn and need one sentence saying what they are. Never shown to the user — it is model input.
    /// </summary>
    private const string ToolImagesFollowUp = "Images returned by the tool calls above.";

    private readonly bool sendPromptCacheKey;
    private readonly bool sendPromptCacheRetention;
    private readonly bool sendConversationCacheHeader;
    private readonly bool sendInteractionId;
    private bool downgraded;
    private string? notice;

    /// <summary>
    /// The attachment kinds Chat Completions has a content part for: images (<c>image_url</c>), PDF (<c>file</c>) and audio
    /// (<c>input_audio</c>, wav or mp3 only). Video has none, so a request carrying it fails before it is sent.
    /// </summary>
    public const ModelInputs WireInputs = ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf | ModelInputs.Audio;

    /// <summary>Whether an attachment of <paramref name="mediaType"/> can be written: any image, a PDF, wav or mp3 audio.</summary>
    public static bool CanCarry(string mediaType) => ProviderAttachments.KindOf(mediaType) switch
    {
        ModelInputs.Image or ModelInputs.Pdf => true,
        ModelInputs.Audio => ProviderAttachments.AudioFormat(mediaType) is not null,
        _ => false,
    };

    public OpenAiCompatibleProvider(ProviderConfig config, HttpClient http, TimeSpan? idleTimeout = null, ProviderOptions? options = null)
        : base(config, http, idleTimeout, options)
    {
        var knobs = options ?? ProviderOptions.Default;
        // DeepSeek caches by prefix on its own and refuses unknown fields, so nothing cache-shaped is sent to it whatever the options say.
        var tunable = !IsDeepSeek(config.BaseUrl);
        sendPromptCacheKey = knobs.SendPromptCacheKey && tunable;
        sendPromptCacheRetention = knobs.SendPromptCacheRetention && tunable;
        sendConversationCacheHeader = knobs.SendConversationCacheHeader && tunable;
        sendInteractionId = knobs.SendInteractionId && tunable;
        downgraded = ProviderDegradations.Contains(DegradationKey);
    }

    /// <summary>Identity under which "this endpoint refused the cache fields" is remembered for the rest of the process.</summary>
    private string DegradationKey => Id + "|chat|" + Config.BaseUrl;

    /// <summary>
    /// A 400 naming one of the cache fields means this model does not know it: the answer is to send less, not to fail the turn.
    /// </summary>
    protected override bool CanDegrade(ProviderException failure) =>
        !downgraded
        && failure.StatusCode == 400
        && (failure.Message.Contains("prompt_cache_key", StringComparison.OrdinalIgnoreCase)
            || failure.Message.Contains("prompt_cache_retention", StringComparison.OrdinalIgnoreCase));

    protected override void Degrade()
    {
        downgraded = true;
        ProviderDegradations.Add(DegradationKey);
    }

    protected override string? DegradationNotice => notice ??=
        "This model or gateway refused the prompt-cache fields, so the request was resent without them. The answer is unaffected; "
        + "caching is then whatever the provider does on its own.";

    /// <summary>
    /// The retention this request asks for, or null when it should not be sent: <c>in_memory</c> is the short lifetime (the default
    /// anyway), <c>24h</c> the long one, which is how a five-to-sixty-minute pause is bridged on this vendor.
    /// </summary>
    private string? CacheRetention(ChatRequest request)
    {
        if (!sendPromptCacheRetention || downgraded)
        {
            return null;
        }

        return string.Equals(request.Cache?.Ttl, "5m", StringComparison.OrdinalIgnoreCase) ? "in_memory" : "24h";
    }

    protected override void ApplyRequestHeaders(HttpRequestMessage request, ChatRequest chatRequest)
    {
        if (downgraded)
        {
            return;
        }

        // Both headers are per-conversation cache keys, not credentials: the routing key (the lineage root) is exactly what they want,
        // so a handover or fork keeps hitting the same entries. Not a claim of identity, so they apply whatever the credential.
        var conversation = string.IsNullOrEmpty(chatRequest.CacheRoutingKey) ? chatRequest.SessionId : chatRequest.CacheRoutingKey;
        if (string.IsNullOrEmpty(conversation))
        {
            return;
        }

        // xAI: raises the automatic cache's hit rate.
        if (sendConversationCacheHeader)
        {
            request.Headers.TryAddWithoutValidation("x-grok-conv-id", conversation);
        }

        // GitHub Copilot: the header its API keys the prompt cache on.
        if (sendInteractionId)
        {
            request.Headers.TryAddWithoutValidation("X-Interaction-Id", conversation);
        }
    }

    /// <summary>
    /// True when <paramref name="baseUrl"/> points at DeepSeek's own API (host contains "deepseek"). DeepSeek caches by
    /// prefix on its own and is never sent <c>prompt_cache_key</c>.
    /// </summary>
    public static bool IsDeepSeek(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && uri.Host.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    protected override string Endpoint => "chat/completions";

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

        if (sendPromptCacheKey && !downgraded && !string.IsNullOrEmpty(request.CacheRoutingKey))
        {
            writer.WriteString("prompt_cache_key", request.CacheRoutingKey);
        }

        if (CacheRetention(request) is { } retention)
        {
            writer.WriteString("prompt_cache_retention", retention);
        }

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

        var messages = request.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            var role = messages[i].Role;
            if (role == MessageRole.System)
            {
                WritePlain(writer, "system", ProviderJson.Text(messages[i].Content) ?? string.Empty);
                continue;
            }

            var end = i;
            while (end + 1 < messages.Count && messages[end + 1].Role == role)
            {
                end++;
            }

            if (role == MessageRole.Tool)
            {
                WriteToolResults(writer, messages, i, end, request.IncludeToolImages);
            }
            else
            {
                WriteMerged(writer, role, messages, i, end, request.IncludeToolImages);
            }

            i = end;
        }
    }

    private static void WriteMerged(
        Utf8JsonWriter writer, MessageRole role, IReadOnlyList<ChatMessage> messages, int start, int end, bool includeToolImages)
    {
        if (role == MessageRole.User)
        {
            WriteToolResults(writer, messages, start, end, includeToolImages);

            var text = MergedText(messages, start, end);
            if (ProviderJson.HasMedia(messages, start, end))
            {
                WriteUserWithImages(writer, text, messages, start, end);
            }
            else if (text is not null)
            {
                WritePlain(writer, "user", text);
            }

            return;
        }

        var calls = CountToolCalls(messages, start, end);
        var assistantText = MergedText(messages, start, end);
        if (assistantText is null && calls == 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        if (assistantText is null)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", assistantText);
        }

        if (calls > 0)
        {
            writer.WriteStartArray("tool_calls");
            for (var i = start; i <= end; i++)
            {
                foreach (var block in messages[i].Content)
                {
                    if (block is ToolCallBlock call)
                    {
                        WriteToolCall(writer, call);
                    }
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static string? MergedText(IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        System.Text.StringBuilder? builder = null;
        for (var i = start; i <= end; i++)
        {
            var text = ProviderJson.Text(messages[i].Content);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder is null)
            {
                builder = new System.Text.StringBuilder();
            }
            else
            {
                builder.Append("\n\n");
            }

            builder.Append(text);
        }

        return builder?.ToString();
    }

    private static int CountToolCalls(IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        var calls = 0;
        for (var i = start; i <= end; i++)
        {
            foreach (var block in messages[i].Content)
            {
                if (block is ToolCallBlock)
                {
                    calls++;
                }
            }
        }

        return calls;
    }

    /// <summary>
    /// A user turn carrying images or files switches to the content-array form: the merged text first, then each image as a
    /// data URL and each file as its own part (<see cref="WriteFile"/>).
    /// A text-only turn keeps the plain string form (<see cref="WritePlain"/>) so its bytes never change.
    /// </summary>
    private static void WriteUserWithImages(
        Utf8JsonWriter writer, string? text, IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");
        if (text is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        for (var i = start; i <= end; i++)
        {
            foreach (var block in messages[i].Content)
            {
                switch (block)
                {
                    case ImageBlock image:
                        WriteImagePart(writer, image);
                        break;
                    case FileBlock file:
                        WriteFile(writer, file);
                        break;
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>A PDF as a <c>file</c> part (data URL), wav/mp3 audio as <c>input_audio</c>; anything else fails the request.</summary>
    private static void WriteFile(Utf8JsonWriter writer, FileBlock file)
    {
        if (ProviderAttachments.IsPdf(file.MediaType))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "file");
            writer.WriteStartObject("file");
            writer.WriteString("filename", ProviderAttachments.FileName(file));
            writer.WriteString("file_data", ProviderAttachments.DataUrl(file));
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        if (ProviderAttachments.KindOf(file.MediaType) == ModelInputs.Audio &&
            ProviderAttachments.AudioFormat(file.MediaType) is { } format)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "input_audio");
            writer.WriteStartObject("input_audio");
            writer.WriteString("data", file.Data);
            writer.WriteString("format", format);
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        throw ProviderAttachments.Unsupported(ProviderFactory.OpenAiCompatibleKind, file);
    }

    private static void WritePlain(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static void WriteToolCall(Utf8JsonWriter writer, ToolCallBlock call)
    {
        writer.WriteStartObject();
        writer.WriteString("id", call.Id);
        writer.WriteString("type", "function");
        writer.WriteStartObject("function");
        writer.WriteString("name", call.Name);
        writer.WriteString("arguments", call.ArgumentsJson);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The tool messages of <c>messages[start..end]</c> — a Chat Completions <c>tool</c> message carries text only — and, when the
    /// results carry images the model may see, one user message with all of those images after the last of them. That second
    /// message is the protocol's own shape for "a tool produced this picture": the text-only tool message would otherwise have
    /// to drop it. It comes after every tool message of the run, never between two of them, because the protocol wants all the
    /// answers to one assistant turn's calls directly after that turn. A text-only result writes exactly the bytes it wrote
    /// before.
    /// </summary>
    private static void WriteToolResults(
        Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages, int start, int end, bool includeToolImages)
    {
        var carriesImages = false;
        for (var i = start; i <= end; i++)
        {
            var blocks = messages[i].Content;
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index] is not ToolResultBlock result)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", result.CallId);
                writer.WriteString("content", result.Content);
                writer.WriteEndObject();
                carriesImages |= result.HasImages;
            }
        }

        if (includeToolImages && carriesImages)
        {
            WriteToolImagesTurn(writer, messages, start, end);
        }
    }

    /// <summary>One user turn carrying every image the tool results of the run produced, with a line saying where they came from.</summary>
    private static void WriteToolImagesTurn(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", ToolImagesFollowUp);
        writer.WriteEndObject();
        for (var i = start; i <= end; i++)
        {
            var blocks = messages[i].Content;
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index] is not ToolResultBlock result)
                {
                    continue;
                }

                foreach (var block in result.Blocks)
                {
                    if (block is ImageBlock image)
                    {
                        WriteImagePart(writer, image);
                    }
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteImagePart(Utf8JsonWriter writer, ImageBlock image)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image_url");
        writer.WriteStartObject("image_url");
        writer.WriteString("url", ProviderJson.DataUrl(image));
        writer.WriteEndObject();
        writer.WriteEndObject();
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

    // The wire names the OpenAI protocol defines. It has no step above "xhigh", so Max is clamped down to it rather than
    // sent as an unknown value the server would reject.
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

    /// <summary>
    /// Reads a usage object. <c>InputTokens</c> includes cached input, as OpenAI's <c>prompt_tokens</c> does. DeepSeek reports
    /// <c>prompt_cache_hit_tokens</c> (cache read) and <c>prompt_cache_miss_tokens</c> (uncached input) instead; when a
    /// <c>cached_tokens</c> field is present as well, it wins.
    /// </summary>
    private static TokenUsage ReadUsage(JsonElement usage, TokenUsage previous)
    {
        var hit = ProviderJson.Int(usage, "prompt_cache_hit_tokens");
        var miss = ProviderJson.Int(usage, "prompt_cache_miss_tokens");
        var input = ProviderJson.Int(usage, "prompt_tokens")
                    ?? (hit is null && miss is null ? previous.InputTokens : (hit ?? 0) + (miss ?? 0));
        var output = ProviderJson.Int(usage, "completion_tokens") ?? previous.OutputTokens;

        int? detailsCached = null;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            detailsCached = ProviderJson.Int(details, "cached_tokens");
        }

        var cached = detailsCached is > 0 ? detailsCached : ProviderJson.Int(usage, "cached_tokens") ?? detailsCached;
        var cacheRead = cached switch
        {
            > 0 => cached.Value,
            0 => hit is null ? previous.CacheReadTokens : 0,
            _ => hit ?? previous.CacheReadTokens,
        };

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
