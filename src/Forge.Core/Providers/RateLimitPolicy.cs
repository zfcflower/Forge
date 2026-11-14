namespace Forge.Core.Providers;

/// <summary>
/// How much of a provider's request and token budget Forge lets itself spend per minute, and the gap between two automated
/// resumes. One policy covers every provider: each provider still gets its own budget, because two accounts behind two
/// providers are throttled separately, but the numbers are one setting rather than a per-provider matrix nobody wants to
/// fill in. A value of zero means "do not limit that dimension".
/// </summary>
/// <remarks>
/// <para>
/// The defaults are deliberately loose: this layer exists so a runaway loop (a fan-out of sub-agents, an automation that
/// retries) cannot burn an account's whole minute in one second, not to shape ordinary use. A rejection from the provider
/// tightens the local budget by itself — see <see cref="ProviderRateLimiter.Observe"/> — so the numbers a vendor publishes
/// only have to be set when someone wants a stricter ceiling than the vendor enforces.
/// </para>
/// <para>
/// Immutable and published by reference (<see cref="ProviderRateLimiter.Publish"/>), so a reader sees one whole policy, never
/// half of an edit, and no lock is taken on the path that sends a request.
/// </para>
/// </remarks>
public sealed record ProviderRateLimitPolicy
{
    /// <summary>Requests per minute one provider may receive. Loose: a minute of ordinary agent turns is dozens, not hundreds.</summary>
    public const int DefaultRequestsPerMinute = 600;

    /// <summary>
    /// Tokens per minute one provider may receive. Loose for the same reason, and deliberately above the largest published
    /// tier: a single long context with cached history can pass a million tokens in a minute, and blocking that would be a
    /// regression, not a protection.
    /// </summary>
    public const int DefaultTokensPerMinute = 4_000_000;

    /// <summary>How far apart two automated resumes of the same provider are spaced; see <see cref="ProviderRateLimiter.ReserveAutomationSlot"/>.</summary>
    public static readonly TimeSpan DefaultAutomationGap = TimeSpan.FromSeconds(10);

    /// <summary>The policy in force until the shell publishes the user's own numbers.</summary>
    public static ProviderRateLimitPolicy Default { get; } = new();

    /// <summary>Whether the local budget is enforced at all. Off means every request is authorized without arithmetic.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Requests per minute per provider; 0 disables the request dimension.</summary>
    public int RequestsPerMinute { get; init; } = DefaultRequestsPerMinute;

    /// <summary>Tokens per minute per provider; 0 disables the token dimension.</summary>
    public int TokensPerMinute { get; init; } = DefaultTokensPerMinute;

    /// <summary>Minimum spacing of automated resumes of one provider; zero or negative means no spacing.</summary>
    public TimeSpan AutomationGap { get; init; } = DefaultAutomationGap;

    /// <summary>
    /// The same policy with every value inside its legal range: a negative rate is zero, a negative gap is zero. Applied by
    /// <see cref="ProviderRateLimiter.Publish"/>, so a caller cannot build a bucket with a negative budget.
    /// </summary>
    public ProviderRateLimitPolicy Normalized() => new()
    {
        Enabled = Enabled,
        RequestsPerMinute = Math.Max(0, RequestsPerMinute),
        TokensPerMinute = Math.Max(0, TokensPerMinute),
        AutomationGap = AutomationGap > TimeSpan.Zero ? AutomationGap : TimeSpan.Zero,
    };
}
