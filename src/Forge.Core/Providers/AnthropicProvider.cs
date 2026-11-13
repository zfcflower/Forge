using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>Anthropic Messages API wire format, with explicit prompt-cache breakpoints.</summary>
public sealed class AnthropicProvider : HttpChatProvider
{
    public const string Version = "2023-06-01";

    /// <summary>Signature prefix that marks a thinking block as a server-redacted one.</summary>
    public const string RedactedSignaturePrefix = "redacted:";

    /// <summary>
    /// The attachment kinds the Messages API has a block for: images and PDF (<c>document</c>). Audio and video have none, so
    /// a request carrying them fails before it is sent.
    /// </summary>
    public const ModelInputs WireInputs = ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf;

    /// <summary>Whether an attachment of <paramref name="mediaType"/> can be written: any image, or a PDF.</summary>
    public static bool CanCarry(string mediaType) =>
        ProviderAttachments.KindOf(mediaType) is ModelInputs.Image or ModelInputs.Pdf;

    private const int DefaultMaxTokens = 8192;
    private const int ThinkingHeadroom = 4096;

    /// <summary>
    /// Providers whose account already refused the announced betas in this process. A capability of the account, not of the
    /// request, so remembering it saves one wasted round trip per turn for the rest of the run.
    /// </summary>
    /// <summary>
    /// Forgets which providers refused their announced betas. Called when the account or its settings change (a reloaded provider
    /// catalogue, a re-signed-in credential) so an account that was fixed is not stuck on the reduced shape for the whole run; tests
    /// use it to isolate one case from the next.
    /// </summary>
    public static void ForgetDegradations() => ProviderDegradations.Clear();

    private readonly Func<string?> runtimeBuild;
    private readonly Func<IReadOnlyList<string>?> runtimeBetas;
    private bool downgraded;

    /// <param name="runtimeBuild">
    /// The build suffix to claim when settings do not pin one. Defaults to asking the locally installed CLI once per process;
    /// injectable so a test can answer without a 300 MB program on the machine.
    /// </param>
    /// <param name="runtimeBetas">
    /// The <c>anthropic-beta</c> list to announce. Defaults to the list the same local CLI announces — the set is version specific,
    /// so a frozen copy disagrees with the version this request claims.
    /// </param>
    public AnthropicProvider(
        ProviderConfig config,
        HttpClient http,
        TimeSpan? idleTimeout = null,
        Func<string?>? runtimeBuild = null,
        Func<IReadOnlyList<string>?>? runtimeBetas = null,
        ProviderOptions? options = null)
        : base(config, http, idleTimeout, options)
    {
        this.runtimeBuild = runtimeBuild ?? (static () => ClaudeCliProbe.CachedBuild);
        this.runtimeBetas = runtimeBetas ?? (static () => ClaudeCliProbe.Cached.Value?.Betas);
        downgraded = ProviderDegradations.Contains(DegradationKey);
    }

    /// <summary>Identity under which "this account refuses the announced betas" is remembered.</summary>
    private string DegradationKey => Id + "|" + Config.AuthStyle + "|" + (Wire?.Entrypoint ?? string.Empty);

    /// <summary>
    /// A 400 that names <c>anthropic-beta</c> is the account (or a gateway) refusing a beta, not refusing the request: the answer is
    /// to announce less, not to fail the turn. A context-overflow 400 is a different thing and is handled before it gets here.
    /// </summary>
    protected override bool CanDegrade(ProviderException failure) =>
        !downgraded
        && failure.StatusCode == 400
        && failure.Message.Contains("anthropic-beta", StringComparison.OrdinalIgnoreCase);

    protected override void Degrade()
    {
        downgraded = true;
        ProviderDegradations.Add(DegradationKey);
    }

    /// <summary>
    /// The sentence the shell shows when this request had to be resent in a reduced shape: what was dropped and what it costs, so the
    /// smaller bill does not turn into an unexplained one.
    /// </summary>
    protected override string? DegradationNotice =>
        "This account or gateway refused one of the announced beta features, so the request was resent with the beta list reduced to "
        + "the ones a subscription requires and the five-minute prompt cache. The answer is unaffected; cache writes will cost more "
        + "often until the account accepts the longer lifetime.";

    protected override string Endpoint => "v1/messages";

    /// <summary>
    /// Anthropic names the account's spending windows in the response, so the same request that costs the user quota also
    /// reports how much of it is gone — including on the 429 that refuses it.
    /// </summary>
    protected override ProviderRateLimit? ReadRateLimit(HttpResponseMessage response) =>
        AnthropicRateLimits.Read(response.Headers);

    /// <summary>The official client posts to <c>v1/messages?beta=true</c>; the query is part of what its requests look like.</summary>
    protected override string EndpointQuery => Wire is null ? string.Empty : "?beta=true";

    /// <summary>
    /// The claimed-identity mode, or null when this provider sends an ordinary API request. Only a bearer (subscription)
    /// credential can claim to be the official client — an API key has no client identity to claim.
    /// </summary>
    private ClaudeWire? Wire => Config.AuthStyle == ProviderAuthStyle.BearerOnly ? Config.Wire : null;

    /// <summary>
    /// The wire to put on this request: a build pinned in settings wins, otherwise the one the local CLI calls itself right now,
    /// otherwise none — the billing block then omits the dot part rather than inventing a value that no client sends.
    /// </summary>
    private ClaudeWire Effective(ClaudeWire wire) =>
        wire.Build is { Length: > 0 } ? wire : wire with { Build = runtimeBuild() ?? string.Empty };

    protected override void ApplyAuth(HttpRequestMessage request)
    {
        // Every Messages request names the API version; an OAuth token still needs it.
        request.Headers.TryAddWithoutValidation("anthropic-version", Version);

        if (Config.AuthStyle == ProviderAuthStyle.BearerOnly)
        {
            // A subscription token (or a gateway that speaks OAuth) authenticates as a bearer token, not as an API key, and
            // the request must opt into the betas that admit an OAuth credential and the Claude Code client identity.
            if (!string.IsNullOrEmpty(Config.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Config.ApiKey);
            }

            var betas = Wire is null || downgraded
                ? ClaudeSubscriptionCredentials.Betas
                : runtimeBetas() ?? (Wire.Betas is { Count: > 0 } claimed ? claimed : ClaudeSubscriptionCredentials.Betas);
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', betas));

            if (Wire is { } wire)
            {
                // Everything the official client sends alongside the identity it claims. Omitting any of these while claiming
                // the entrypoint would be a request that contradicts itself.
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("user-agent", AnthropicWire.UserAgent(wire));
                request.Headers.TryAddWithoutValidation("x-app", AnthropicWire.AppHeader);
                request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
                request.Headers.TryAddWithoutValidation("x-stainless-lang", AnthropicWire.Language);
                request.Headers.TryAddWithoutValidation("x-stainless-package-version", AnthropicWire.StainlessPackageVersion);
                request.Headers.TryAddWithoutValidation("x-stainless-os", OperatingSystemName());
                request.Headers.TryAddWithoutValidation("x-stainless-arch", RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    Architecture.X86 => "ia32",
                    _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                });
                request.Headers.TryAddWithoutValidation("x-stainless-runtime", AnthropicWire.Runtime);
                request.Headers.TryAddWithoutValidation("x-stainless-runtime-version", AnthropicWire.StainlessRuntimeVersion);
                request.Headers.TryAddWithoutValidation("x-stainless-retry-count", "0");
                request.Headers.TryAddWithoutValidation("x-stainless-timeout", "600");
            }

            return;
        }

        if (!string.IsNullOrEmpty(Config.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", Config.ApiKey);
        }

        // One request shape for both credentials: the cache lifetime below is the one-hour beta, so an API key announces it too
        // rather than maintaining a second, shorter-lived configuration that only differs by credential. After a downgrade the
        // lifetime falls back to the server default and there is nothing left to announce.
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        if (!downgraded)
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicWire.ExtendedCacheTtlBeta);
        }
    }

    protected override byte[] BuildBody(ChatRequest request) => ProviderJson.Write(writer =>
    {
        var budget = ThinkingBudget(request.Model.Thinking);
        var maxTokens = request.Model.MaxOutputTokens ?? DefaultMaxTokens;
        if (budget >= maxTokens)
        {
            maxTokens = budget + ThinkingHeadroom;
        }

        writer.WriteString("model", request.Model.Model);
        writer.WriteNumber("max_tokens", maxTokens);
        writer.WriteBoolean("stream", true);

        if (budget > 0)
        {
            writer.WriteStartObject("thinking");
            writer.WriteString("type", "enabled");
            writer.WriteNumber("budget_tokens", budget);
            writer.WriteEndObject();
        }

        WriteSystem(writer, request);
        WriteMessages(writer, request);
        WriteTools(writer, request);
    });

    /// <summary>
    /// The session header the official client sends: a per-session uuid. Forge's session ids are its own strings, so one that
    /// is not already a uuid is hashed into one — stable for the life of the session, never a made-up constant.
    /// </summary>
    protected override void ApplyRequestHeaders(HttpRequestMessage request, ChatRequest chatRequest)
    {
        if (Wire is null || string.IsNullOrEmpty(chatRequest.SessionId))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("x-claude-code-session-id", SessionUuid(chatRequest.SessionId));
    }

    /// <summary>A uuid shaped from a session id: the id itself when it already is one, else a name-based (v5) uuid.</summary>
    internal static string SessionUuid(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var parsed))
        {
            return parsed.ToString("D");
        }

        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(sessionId));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString("D");
    }

    private static string OperatingSystemName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "MacOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "Other";

    private void WriteSystem(Utf8JsonWriter writer, ChatRequest request)
    {
        var system = new StringBuilder(request.SystemPrompt);
        foreach (var message in request.Messages)
        {
            if (message.Role != MessageRole.System || ProviderJson.Text(message.Content) is not { Length: > 0 } text)
            {
                continue;
            }

            if (system.Length > 0)
            {
                system.Append("\n\n");
            }

            system.Append(text);
        }

        // An OAuth (subscription) request is only accepted when the preamble is its own first system block; for an API-key
        // request the preamble is empty and the array keeps its single block.
        var preamble = Config.SystemPromptPreamble;
        var hasPreamble = !string.IsNullOrEmpty(preamble);
        var billing = Wire is { } claimedWire ? AnthropicWire.BillingHeader(Effective(claimedWire)) : null;
        if (system.Length == 0 && !hasPreamble && billing is null)
        {
            return;
        }

        var cache = request.Cache?.CacheSystemAndTools == true;
        // One shape for every credential, one lifetime for every breakpoint of the request: an hour unless the caller asked for the
        // short one. Mixing them would mean the same prefix is written twice at two prices.
        var ttl = CacheTtl(request);
        writer.WriteStartArray("system");
        if (billing is not null)
        {
            // The official client puts the billing block first, ahead of the identity block; the server strips it before the
            // model sees anything, so it carries no instruction and gets no cache breakpoint.
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", billing);
            writer.WriteEndObject();
        }

        if (hasPreamble)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", preamble);
            if (cache)
            {
                WriteCacheControl(writer, ttl);
            }

            writer.WriteEndObject();
        }

        if (system.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", system.ToString());
            if (cache)
            {
                WriteCacheControl(writer, ttl);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteTools(Utf8JsonWriter writer, ChatRequest request)
    {
        if (request.Tools.Count == 0)
        {
            return;
        }

        // No breakpoint on the tool array, for either credential: a breakpoint is a prefix, so the one at the end of the system
        // blocks already caches the tools, and the official client puts none here either. Dropping it costs nothing and keeps one
        // cache shape instead of two.
        writer.WriteStartArray("tools");
        foreach (var tool in request.Tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("input_schema");
            ProviderJson.WriteRaw(writer, tool.InputSchemaJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteMessages(Utf8JsonWriter writer, ChatRequest request)
    {
        var messages = BuildMessages(request.Messages);
        var cacheIndex = request.Cache?.CacheThroughMessageIndex ?? -1;
        // The policy's own lifetime wins; the claimed-identity shape otherwise uses the hour the official client asks for, which is
        // also the only way the message breakpoint survives a pause between turns.
        var messageTtl = CacheTtl(request);

        writer.WriteStartArray("messages");
        foreach (var message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", message.Role);
            writer.WriteStartArray("content");
            for (var i = 0; i < message.Blocks.Count; i++)
            {
                var block = message.Blocks[i];
                var nextSource = i + 1 < message.Blocks.Count ? message.Blocks[i + 1].Source : -1;
                WriteBlock(writer, block.Block, cacheControl: block.Source == cacheIndex && nextSource != cacheIndex, messageTtl, request.IncludeToolImages);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static List<WireMessage> BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var wire = new List<WireMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == MessageRole.System)
            {
                continue;
            }

            var role = message.Role == MessageRole.Assistant && !HasToolResult(message.Content) ? "assistant" : "user";
            var blocks = new List<WireBlock>(message.Content.Count);
            foreach (var block in message.Content)
            {
                if (IsSendable(block) && (block is not (ImageBlock or FileBlock) || message.Role == MessageRole.User))
                {
                    blocks.Add(new WireBlock(block, index));
                }
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            if (wire.Count > 0 && wire[^1].Role == role)
            {
                wire[^1].Blocks.AddRange(blocks);
            }
            else
            {
                wire.Add(new WireMessage(role, blocks));
            }
        }

        foreach (var message in wire)
        {
            OrderToolResultsFirst(message.Blocks);
        }

        return wire;
    }

    /// <summary>Tool results must open the user turn that answers a tool_use assistant turn.</summary>
    private static void OrderToolResultsFirst(List<WireBlock> blocks)
    {
        var firstOther = -1;
        var misplaced = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Block is ToolResultBlock)
            {
                misplaced |= firstOther >= 0;
                continue;
            }

            if (firstOther < 0)
            {
                firstOther = i;
            }
        }

        if (!misplaced)
        {
            return;
        }

        var ordered = new List<WireBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block.Block is ToolResultBlock)
            {
                ordered.Add(block);
            }
        }

        foreach (var block in blocks)
        {
            if (block.Block is not ToolResultBlock)
            {
                ordered.Add(block);
            }
        }

        blocks.Clear();
        blocks.AddRange(ordered);
    }

    private static bool IsSendable(ContentBlock block) => block switch
    {
        ThinkingBlock thinking => !string.IsNullOrEmpty(thinking.Signature),
        TextBlock text => text.Text.Length > 0,
        _ => true,
    };

    private static bool HasToolResult(IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is ToolResultBlock)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteBlock(Utf8JsonWriter writer, ContentBlock block, bool cacheControl, string? ttl, bool includeToolImages)
    {
        writer.WriteStartObject();
        switch (block)
        {
            case TextBlock text:
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                break;
            case ToolCallBlock call:
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", call.Id);
                writer.WriteString("name", call.Name);
                writer.WritePropertyName("input");
                ProviderJson.WriteRaw(writer, call.ArgumentsJson);
                break;
            case ToolResultBlock result:
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", result.CallId);
                WriteToolResultContent(writer, result, includeToolImages);
                if (result.IsError)
                {
                    writer.WriteBoolean("is_error", true);
                }

                break;
            case ThinkingBlock thinking when thinking.Signature?.StartsWith(RedactedSignaturePrefix, StringComparison.Ordinal) == true:
                writer.WriteString("type", "redacted_thinking");
                writer.WriteString("data", thinking.Signature[RedactedSignaturePrefix.Length..]);
                break;
            case ThinkingBlock thinking:
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", thinking.Text);
                writer.WriteString("signature", thinking.Signature);
                break;
            case ArtifactRefBlock artifact:
                writer.WriteString("type", "text");
                writer.WriteString("text", ProviderJson.RenderArtifact(artifact));
                break;
            case ImageBlock image:
                WriteImageBlock(writer, image);
                break;
            case FileBlock file when ProviderAttachments.IsPdf(file.MediaType):
                writer.WriteString("type", "document");
                writer.WriteStartObject("source");
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", ProviderAttachments.Pdf);
                writer.WriteString("data", file.Data);
                writer.WriteEndObject();
                break;
            case FileBlock file:
                throw ProviderAttachments.Unsupported(ProviderFactory.AnthropicKind, file);
            default:
                throw new ProviderException($"Unsupported content block {block.GetType().Name}.", retryable: false);
        }

        if (cacheControl)
        {
            WriteCacheControl(writer, ttl);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The <c>content</c> of a <c>tool_result</c>: the plain string when there is no picture to send — byte-identical to what
    /// this provider wrote before tool results could carry one — and otherwise the block array Anthropic accepts, the result's
    /// text first and then each image. The images go here rather than in a following user turn because a <c>tool_result</c> is
    /// itself the user-turn content that answers the call.
    /// </summary>
    private static void WriteToolResultContent(Utf8JsonWriter writer, ToolResultBlock result, bool includeToolImages)
    {
        if (!includeToolImages || result.Images.Count == 0)
        {
            writer.WriteString("content", result.Content);
            return;
        }

        writer.WriteStartArray("content");
        if (result.Text.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", result.Text);
            writer.WriteEndObject();
        }

        foreach (var image in result.Images)
        {
            WriteImageBlock(writer, image);
        }

        writer.WriteEndArray();
    }

    private static void WriteImageBlock(Utf8JsonWriter writer, ImageBlock image)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteStartObject("source");
        writer.WriteString("type", "base64");
        writer.WriteString("media_type", image.MediaType);
        writer.WriteString("data", image.Data);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The <c>ttl</c> to write: an hour by default, nothing at all when the caller asked for the short-lived default (the server's
    /// own), which is how <c>"5m"</c> is expressed.
    /// </summary>
    private string? CacheTtl(ChatRequest request) =>
        downgraded || string.Equals(request.Cache?.Ttl, "5m", StringComparison.OrdinalIgnoreCase)
            ? null
            : AnthropicWire.CacheTtl;

    private static void WriteCacheControl(Utf8JsonWriter writer, string? ttl)
    {
        writer.WriteStartObject("cache_control");
        writer.WriteString("type", "ephemeral");
        if (!string.IsNullOrEmpty(ttl))
        {
            writer.WriteString("ttl", ttl);
        }

        writer.WriteEndObject();
    }

    // The extended-thinking budget per step: 1k, 2k, 8k, 24k, 32k and 48k tokens, each still under the output ceiling
    // Anthropic allows (see the ThinkingHeadroom clamp above, which lifts max_tokens whenever the budget would exceed it).
    private static int ThinkingBudget(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => 1024,
        ThinkingEffort.Low => 2048,
        ThinkingEffort.Medium => 8192,
        ThinkingEffort.High => 24576,
        ThinkingEffort.XHigh => 32768,
        ThinkingEffort.Max => 49152,
        _ => 0,
    };

    protected override async IAsyncEnumerable<ChatStreamEvent> ParseAsync(
        SseReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var blocks = new List<BlockState?>();
        var usage = new UsageState();
        string? stopReason = null;
        var completed = false;

        while (true)
        {
            var evt = await ReadEventAsync(reader, ct).ConfigureAwait(false);
            if (evt is not { } sse)
            {
                break;
            }

            var type = sse.Event;
            if (sse.Data.Length == 0)
            {
                continue;
            }

            using var document = ParseChunk(sse.Data);
            var root = document.RootElement;
            type = ProviderJson.Str(root, "type") ?? type;

            switch (type)
            {
                case "ping":
                    break;

                case "message_start":
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    {
                        throw Malformed(sse.Data);
                    }

                    if (message.TryGetProperty("usage", out var startUsage))
                    {
                        usage.Merge(startUsage);
                    }

                    break;

                case "content_block_start":
                {
                    var index = ProviderJson.Int(root, "index") ?? blocks.Count;
                    while (blocks.Count <= index)
                    {
                        blocks.Add(null);
                    }

                    var state = blocks[index] ??= new BlockState();
                    root.TryGetProperty("content_block", out var contentBlock);
                    state.Type = ProviderJson.Str(contentBlock, "type") ?? string.Empty;
                    state.Id = ProviderJson.Str(contentBlock, "id") ?? string.Empty;
                    state.Name = ProviderJson.Str(contentBlock, "name") ?? string.Empty;
                    state.Text.Append(ProviderJson.Str(contentBlock, "text"));
                    state.Thinking.Append(ProviderJson.Str(contentBlock, "thinking"));
                    state.Signature.Append(ProviderJson.Str(contentBlock, "signature"));
                    if (state.Type == "redacted_thinking")
                    {
                        state.Signature.Append(RedactedSignaturePrefix).Append(ProviderJson.Str(contentBlock, "data"));
                    }

                    if (state.Type == "tool_use")
                    {
                        yield return new ToolCallStarted(state.Id, state.Name);
                    }

                    break;
                }

                case "content_block_delta":
                {
                    if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    {
                        break;
                    }

                    var index = ProviderJson.Int(root, "index") ?? -1;
                    var state = index >= 0 && index < blocks.Count ? blocks[index] : null;
                    var deltaType = ProviderJson.Str(delta, "type");

                    switch (deltaType)
                    {
                        case "text_delta":
                        case "text":
                            if (ProviderJson.Str(delta, "text") is { Length: > 0 } text)
                            {
                                state?.Text.Append(text);
                                yield return new TextDelta(text);
                            }

                            break;

                        case "thinking_delta":
                        case "thinking":
                            if (ProviderJson.Str(delta, "thinking") is { Length: > 0 } thinking)
                            {
                                state?.Thinking.Append(thinking);
                                yield return new ThinkingDelta(thinking);
                            }

                            break;

                        case "signature_delta":
                        case "signature":
                            state?.Signature.Append(ProviderJson.Str(delta, "signature"));
                            break;

                        case "input_json_delta":
                        case "input_json":
                            if (ProviderJson.Str(delta, "partial_json") is { Length: > 0 } partial)
                            {
                                state?.Json.Append(partial);
                                yield return new ToolCallArgumentsDelta(state?.Id ?? string.Empty, partial);
                            }

                            break;
                    }

                    break;
                }

                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta) && messageDelta.ValueKind == JsonValueKind.Object)
                    {
                        stopReason = ProviderJson.Str(messageDelta, "stop_reason") ?? stopReason;
                    }

                    if (root.TryGetProperty("usage", out var endUsage))
                    {
                        usage.Merge(endUsage);
                    }

                    break;

                case "message_stop":
                    completed = true;
                    break;

                case "error":
                    throw StreamError(root);
            }

            if (completed)
            {
                break;
            }
        }

        if (!completed)
        {
            throw Truncated();
        }

        var content = new List<ContentBlock>(blocks.Count);
        foreach (var state in blocks)
        {
            if (state is null)
            {
                continue;
            }

            switch (state.Type)
            {
                case "thinking":
                    if (state.Thinking.Length > 0 || state.Signature.Length > 0)
                    {
                        content.Add(new ThinkingBlock(
                            state.Thinking.ToString(),
                            state.Signature.Length == 0 ? null : state.Signature.ToString()));
                    }

                    break;

                case "redacted_thinking":
                    content.Add(new ThinkingBlock(string.Empty, state.Signature.ToString()));
                    break;

                case "text":
                    if (state.Text.Length > 0)
                    {
                        content.Add(new TextBlock(state.Text.ToString()));
                    }

                    break;

                case "tool_use":
                    content.Add(new ToolCallBlock(
                        state.Id,
                        state.Name,
                        state.Json.Length == 0 ? "{}" : state.Json.ToString()));
                    break;
            }
        }

        var stop = MapStopReason(stopReason, content);
        yield return new MessageCompleted(new ChatMessage(MessageRole.Assistant, content), usage.ToTokenUsage(), stop);
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

    private ProviderException StreamError(JsonElement root)
    {
        root.TryGetProperty("error", out var error);
        var message = ProviderJson.Str(error, "message") ?? root.GetRawText();
        var errorType = ProviderJson.Str(error, "type");
        var retryable = errorType is "overloaded_error" or "api_error" or "rate_limit_error" or "timeout_error";
        return new ProviderException($"{Id}: stream error ({errorType ?? "unknown"}): {message}", retryable);
    }

    private static StopReason MapStopReason(string? stopReason, List<ContentBlock> content) => stopReason switch
    {
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "end_turn" or "stop_sequence" => HasToolCalls(content) ? StopReason.ToolUse : StopReason.EndTurn,
        null => HasToolCalls(content) ? StopReason.ToolUse : StopReason.Other,
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

    private sealed record WireBlock(ContentBlock Block, int Source);

    private sealed record WireMessage(string Role, List<WireBlock> Blocks);

    private sealed class BlockState
    {
        public string Type { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public StringBuilder Signature { get; } = new();

        public StringBuilder Json { get; } = new();
    }

    private sealed class UsageState
    {
        private int _input;
        private int _output;
        private int _cacheRead;
        private int _cacheWrite;

        public void Merge(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _input = ProviderJson.Int(usage, "input_tokens") ?? _input;
            _output = ProviderJson.Int(usage, "output_tokens") ?? _output;
            _cacheRead = ProviderJson.Int(usage, "cache_read_input_tokens") ?? _cacheRead;
            _cacheWrite = ProviderJson.Int(usage, "cache_creation_input_tokens") ?? _cacheWrite;
        }

        public TokenUsage ToTokenUsage() => new(_input + _cacheRead + _cacheWrite, _output, _cacheRead, _cacheWrite);
    }
}
