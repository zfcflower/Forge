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

    /// <summary>No provider overrides its ceilings; the global numbers stand.</summary>
    public static IReadOnlyDictionary<string, ProviderRateLimitLimits> NoOverrides { get; } =
        new Dictionary<string, ProviderRateLimitLimits>(StringComparer.Ordinal);

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
    /// Ceilings that replace the global ones for the provider they are keyed by, for the one case the global numbers cannot
    /// express: a gateway whose operator knows it is tighter than the defaults (a self-hosted proxy on a small plan, most of
    /// them). Providers that report their own limits do not need an entry — the response headers are more accurate than anything
    /// typed here, and they tighten their own bucket. A missing dimension inherits the global number; zero means unlimited.
    /// </summary>
    public IReadOnlyDictionary<string, ProviderRateLimitLimits> Overrides { get; init; } = NoOverrides;

    /// <summary>The ceilings that apply to <paramref name="providerId"/>: its override where it has one, else the globals.</summary>
    public (int RequestsPerMinute, int TokensPerMinute) LimitsFor(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        if (Overrides.Count > 0 && Overrides.TryGetValue(providerId, out var limits))
        {
            return (limits.RequestsPerMinute ?? RequestsPerMinute, limits.TokensPerMinute ?? TokensPerMinute);
        }

        return (RequestsPerMinute, TokensPerMinute);
    }

    /// <summary>
    /// The same policy with every value inside its legal range: a negative rate is zero, a negative gap is zero, and an override
    /// entry that overrides nothing is dropped. Applied by <see cref="ProviderRateLimiter.Publish"/>, so a caller cannot build a
    /// bucket with a negative budget.
    /// </summary>
    public ProviderRateLimitPolicy Normalized()
    {
        // One dictionary per publish (startup, a settings edit), never on the request path: the buckets read the map they were
        // configured from, so this copy is what keeps a published policy immutable from the inside too.
        Dictionary<string, ProviderRateLimitLimits>? overrides = null;
        foreach (var (providerId, limits) in Overrides)
        {
            var normalized = limits.Normalized();
            if (normalized.IsEmpty || providerId.Length == 0)
            {
                continue;
            }

            overrides ??= new Dictionary<string, ProviderRateLimitLimits>(Overrides.Count, StringComparer.Ordinal);
            overrides[providerId] = normalized;
        }

        return new ProviderRateLimitPolicy
        {
            Enabled = Enabled,
            RequestsPerMinute = Math.Max(0, RequestsPerMinute),
            TokensPerMinute = Math.Max(0, TokensPerMinute),
            AutomationGap = AutomationGap > TimeSpan.Zero ? AutomationGap : TimeSpan.Zero,
            Overrides = overrides ?? NoOverrides,
        };
    }
}

/// <summary>
/// One provider's own ceilings. Each dimension is optional: a dimension left null inherits the policy's global number, so an
/// override only has to state what actually differs (a gateway that allows 20 requests but the usual token budget does not need
/// to repeat the token number).
/// </summary>
public sealed record ProviderRateLimitLimits(int? RequestsPerMinute = null, int? TokensPerMinute = null)
{
    /// <summary>True when the entry states nothing, which is the same as having no entry at all.</summary>
    public bool IsEmpty => RequestsPerMinute is null && TokensPerMinute is null;

    /// <summary>The same limits with negative numbers read as zero ("no limit"), never as a negative budget.</summary>
    public ProviderRateLimitLimits Normalized() => new(
        RequestsPerMinute is { } requests ? Math.Max(0, requests) : null,
        TokensPerMinute is { } tokens ? Math.Max(0, tokens) : null);
}
