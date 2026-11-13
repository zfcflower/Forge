namespace Forge.Abstractions;

/// <summary>Reasoning effort, seven steps. Which steps a model accepts is decided per model, not by this enum.</summary>
public enum ThinkingEffort { Off, Minimal, Low, Medium, High, XHigh, Max }

/// <summary>Which model to use and how hard it should think; set per agent kind.</summary>
/// <param name="ProviderId">Which connected provider serves <paramref name="Model"/>; <c>null</c> means the default one.
/// Two providers can serve a model with the same id, so the choice is part of the settings rather than looked up.
/// </param>
public sealed record ModelSettings(
    string Model,
    ThinkingEffort Thinking = ThinkingEffort.Off,
    int? MaxOutputTokens = null,
    string? ProviderId = null);

/// <summary>
/// Prompt-cache placement: where the stable prefix ends, and for how long it should survive. <paramref name="Ttl"/> applies to
/// every breakpoint of the request — the system blocks and the message — because an hour-long system breakpoint next to a
/// five-minute message one would just mean the same prefix is written twice. <c>null</c> = the provider's default (an hour for
/// the claimed Claude shape), <c>"5m"</c> = ask for the short-lived default, <c>"1h"</c> = the hour.
/// </summary>
public sealed record CacheHint(bool CacheSystemAndTools, int? CacheThroughMessageIndex = null, string? Ttl = null);

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

    /// <summary>
    /// The session the turn belongs to; fills a <c>{session}</c> placeholder in a provider's configured request headers
    /// (OpenCode's <c>x-opencode-session</c>, for one). Null outside a session: a header that needs it is then not sent.
    /// </summary>
    public string? SessionId { get; init; }
}

public enum StopReason { EndTurn, ToolUse, MaxTokens, Other }

public abstract record ChatStreamEvent;
public sealed record TextDelta(string Text) : ChatStreamEvent;
public sealed record ThinkingDelta(string Text) : ChatStreamEvent;
public sealed record ToolCallStarted(string Id, string Name) : ChatStreamEvent;
public sealed record ToolCallArgumentsDelta(string Id, string Json) : ChatStreamEvent;

/// <summary>
/// The turn's last stream event. <see cref="RateLimit"/> is the provider's own account-usage reading, when the protocol
/// returned one with this response (Anthropic's <c>anthropic-ratelimit-unified-*</c> headers); null means the provider has
/// no such meter, not that the account is unlimited.
/// </summary>
public sealed record MessageCompleted(ChatMessage Message, TokenUsage Usage, StopReason Stop) : ChatStreamEvent
{
    public ProviderRateLimit? RateLimit { get; init; }

    /// <summary>
    /// Something the user should know about <em>how</em> this answer was obtained rather than what it says — the request had to
    /// fall back to a reduced shape, for one. Null when there is nothing to report; never a substitute for an error.
    /// </summary>
    public string? Notice { get; init; }
}

/// <summary>One usage window as the server reported it; <see cref="Unknown"/> is a window the response did not mention.</summary>
public enum RateLimitStatus { Unknown, Allowed, AllowedWarning, Rejected }

/// <summary>Which window a rate-limit reading is about.</summary>
public enum RateLimitWindow { FiveHour, Weekly }

/// <summary>
/// The account's spending windows as carried by a provider response: how much of each window is used (0-1, the server's own
/// figure), when it resets, and whether the server would accept the next request. This is the only place Forge can see a
/// window <em>before</em> it is spent — an exhausted window otherwise shows up only as a rejected request. Every field is
/// optional because providers differ in what they report and any one header can be missing.
/// </summary>
public sealed record ProviderRateLimit(
    double? FiveHourUtilization,
    DateTimeOffset? FiveHourResetAt,
    RateLimitStatus FiveHour,
    double? WeeklyUtilization,
    DateTimeOffset? WeeklyResetAt,
    RateLimitStatus Weekly,
    RateLimitWindow? Representative,
    RateLimitStatus Overage)
{
    /// <summary>The window the server says is binding (its representative claim), when it named one.</summary>
    public RateLimitWindow? BindingWindow => Representative switch
    {
        RateLimitWindow.FiveHour => RateLimitWindow.FiveHour,
        RateLimitWindow.Weekly => RateLimitWindow.Weekly,
        _ => FiveHour == RateLimitStatus.Rejected ? RateLimitWindow.FiveHour
            : Weekly == RateLimitStatus.Rejected ? RateLimitWindow.Weekly
            : null,
    };

    /// <summary>True when the server would reject the next request; nothing to do with the numbers being high.</summary>
    public bool IsRejected => FiveHour == RateLimitStatus.Rejected || Weekly == RateLimitStatus.Rejected;

    /// <summary>The reset of the window that is in the way: the binding one, else the nearest rejected one.</summary>
    public DateTimeOffset? ResetAt => BindingWindow switch
    {
        RateLimitWindow.FiveHour => FiveHourResetAt,
        RateLimitWindow.Weekly => WeeklyResetAt,
        _ => null,
    };

    /// <summary>
    /// The per-minute request and token limits an API key is throttled by, when the response reported them. A different thing
    /// from the spending windows above — those are an account's budget for hours, this is a short-term ceiling — so it lives in
    /// its own record and never feeds <see cref="IsRejected"/>: a 429 against it is transient and belongs to the retry layer.
    /// </summary>
    public ProviderThrottle? Throttle { get; init; }

    /// <summary>True when a spending window was reported, i.e. the account is on a subscription rather than an API key.</summary>
    public bool HasWindows => FiveHourUtilization is not null
        || WeeklyUtilization is not null
        || FiveHour != RateLimitStatus.Unknown
        || Weekly != RateLimitStatus.Unknown;
}

/// <summary>
/// A rate limit with a minute-scale horizon, as reported by <c>anthropic-ratelimit-requests-*</c> and
/// <c>anthropic-ratelimit-tokens-*</c>: how many requests and tokens the current period still allows. All fields are optional
/// because a response may report only one of them.
/// </summary>
public sealed record ProviderThrottle(
    int? RequestLimit,
    int? RequestRemaining,
    DateTimeOffset? RequestResetAt,
    int? TokenLimit,
    int? TokenRemaining,
    DateTimeOffset? TokenResetAt)
{
    /// <summary>Whether anything about the request limit was reported.</summary>
    public bool HasRequests => RequestLimit is not null || RequestRemaining is not null;

    /// <summary>Whether anything about the token limit was reported.</summary>
    public bool HasTokens => TokenLimit is not null || TokenRemaining is not null;
}

public sealed class ProviderException(string message, bool retryable, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;

    /// <summary>
    /// The usage windows the failed response reported, when it reported any. An exhausted quota is usually a 429 whose only
    /// machine-readable evidence is this reading, so the failure path keeps it.
    /// </summary>
    public ProviderRateLimit? RateLimit { get; init; }
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
    string? SystemPromptPreamble = null,
    ClaudeWire? Wire = null);

/// <summary>
/// Makes a subscription request indistinguishable from the official client's: the billing header block the client puts at
/// the front of <c>system</c> (<c>cc_version</c> + <c>cc_entrypoint</c>), the matching <c>User-Agent</c> that repeats the
/// entrypoint, the beta list, and the SDK transport headers. The values are what the identity actually claims, so they are
/// configuration rather than constants — a release changes them, and the shell reads them from <c>settings.json</c>.
/// </summary>
/// <param name="Version">The client version to claim, e.g. <c>2.1.317</c>; also appears in the user agent.</param>
/// <param name="Build">The build suffix the billing header appends (<c>cc_version=2.1.317.68a</c>), empty when none.</param>
/// <param name="Entrypoint">The entrypoint name to claim, e.g. <c>claude-desktop-3p</c>.</param>
/// <param name="Betas">The <c>anthropic-beta</c> features to announce; the shell supplies the official list.</param>
public sealed record ClaudeWire(string Version, string Build, string Entrypoint, IReadOnlyList<string> Betas);

/// <summary>
/// How the credential travels on the wire. <see cref="Default"/> is each protocol's own convention (Anthropic's
/// <c>x-api-key</c>, OpenAI-compatible's bearer token); <see cref="BearerOnly"/> is what an OAuth or Copilot credential
/// needs even on a protocol that would otherwise use a key header; <see cref="None"/> means the caller put everything it
/// wants into <see cref="ProviderConfig.Headers"/>, which is how a gateway with its own auth scheme is reached.
/// </summary>
public enum ProviderAuthStyle { Default, BearerOnly, None }
