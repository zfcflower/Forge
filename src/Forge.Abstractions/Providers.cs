namespace Forge.Abstractions;

public enum ThinkingEffort { Off, Low, Medium, High }

/// <summary>Which model to use and how hard it should think; set per agent kind.</summary>
/// <param name="ProviderId">Which connected provider serves <paramref name="Model"/>; <c>null</c> means the default one.
/// Two providers can serve a model with the same id, so the choice is part of the settings rather than looked up.
/// </param>
public sealed record ModelSettings(
    string Model,
    ThinkingEffort Thinking = ThinkingEffort.Off,
    int? MaxOutputTokens = null,
    string? ProviderId = null);

/// <summary>Tells the provider where the stable, cacheable prefix ends.</summary>
/// <summary>
/// Prompt-cache placement. <paramref name="MessageTtl"/> applies to the message breakpoint only: null = the provider
/// default (5 minutes), <c>"1h"</c> = the one-hour cache; tool and system breakpoints keep the default.
/// </summary>
public sealed record CacheHint(bool CacheSystemAndTools, int? CacheThroughMessageIndex = null, string? MessageTtl = null);

public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

public sealed record ChatRequest(
    string SystemPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<ChatMessage> Messages,
    ModelSettings Model,
    CacheHint? Cache = null)
{
    /// <summary>
    /// Provider-side cache routing key (OpenAI-compatible <c>prompt_cache_key</c>); the session's lineage root id.
    /// Null = do not send. Providers that do not support it ignore it.
    /// </summary>
    public string? CacheRoutingKey { get; init; }
}

public enum StopReason { EndTurn, ToolUse, MaxTokens, Other }

public abstract record ChatStreamEvent;
public sealed record TextDelta(string Text) : ChatStreamEvent;
public sealed record ThinkingDelta(string Text) : ChatStreamEvent;
public sealed record ToolCallStarted(string Id, string Name) : ChatStreamEvent;
public sealed record ToolCallArgumentsDelta(string Id, string Json) : ChatStreamEvent;
public sealed record MessageCompleted(ChatMessage Message, TokenUsage Usage, StopReason Stop) : ChatStreamEvent;

public sealed class ProviderException(string message, bool retryable, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;
}

public interface IChatProvider
{
    string Id { get; }
    /// <summary>Streams one assistant turn; the final event is always <see cref="MessageCompleted"/>.</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}

public sealed record ProviderConfig(
    string Id,
    string Kind,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyDictionary<string, string>? Headers = null,
    ProviderAuthStyle AuthStyle = ProviderAuthStyle.Default,
    string? SystemPromptPreamble = null);

/// <summary>
/// How the credential travels on the wire. <see cref="Default"/> is each protocol's own convention (Anthropic's
/// <c>x-api-key</c>, OpenAI-compatible's bearer token); <see cref="BearerOnly"/> is what an OAuth or Copilot credential
/// needs even on a protocol that would otherwise use a key header; <see cref="None"/> means the caller put everything it
/// wants into <see cref="ProviderConfig.Headers"/>, which is how a gateway with its own auth scheme is reached.
/// </summary>
public enum ProviderAuthStyle { Default, BearerOnly, None }
