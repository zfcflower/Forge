namespace Forge.Abstractions;

public enum ThinkingEffort { Off, Low, Medium, High }

/// <summary>Which model to use and how hard it should think; set per agent kind.</summary>
public sealed record ModelSettings(string Model, ThinkingEffort Thinking = ThinkingEffort.Off, int? MaxOutputTokens = null);

/// <summary>Tells the provider where the stable, cacheable prefix ends.</summary>
public sealed record CacheHint(bool CacheSystemAndTools, int? CacheThroughMessageIndex = null);

public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

public sealed record ChatRequest(
    string SystemPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<ChatMessage> Messages,
    ModelSettings Model,
    CacheHint? Cache = null);

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

public sealed record ProviderConfig(string Id, string Kind, string BaseUrl, string? ApiKey, IReadOnlyDictionary<string, string>? Headers = null);
