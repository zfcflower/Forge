using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The derived quantities behind the session's usage readout: how full the context window is, how much of the prompt was
/// served from the provider's cache, and what the session has cost so far. Pure arithmetic over
/// <see cref="TokenUsage"/>, with no formatting and no rounding — the shell decides how to display these.
/// </summary>
public static class SessionUsageMath
{
    /// <summary>
    /// Cache reads are already inside <c>InputTokens</c>: the OpenAI-compatible adapter reports <c>prompt_tokens</c>
    /// (which includes cached tokens) and the Anthropic adapter reports <c>input + cacheRead + cacheWrite</c>. So the
    /// denominator is <c>InputTokens</c> itself, and <see cref="Cost"/> subtracts the cached part instead of adding it.
    /// </summary>
    public static double CacheHitRatio(TokenUsage usage) =>
        usage.InputTokens <= 0 ? 0 : Math.Clamp((double)usage.CacheReadTokens / usage.InputTokens, 0, 1);

    /// <summary>
    /// How full the context window is, or <c>null</c> when the window is unknown (no metadata for the model).
    /// <paramref name="usedTokens"/> is the size of the most recent request's prompt, not the session's cumulative input.
    /// </summary>
    public static double? ContextRatio(int usedTokens, int? contextWindow)
    {
        if (contextWindow is not > 0)
        {
            return null;
        }

        return Math.Clamp((double)usedTokens / contextWindow.Value, 0, 1);
    }

    /// <summary>Prompt tokens billed at the full input rate, i.e. everything that was not served from cache.</summary>
    public static int BilledInputTokens(TokenUsage usage) =>
        Math.Max(0, usage.InputTokens - usage.CacheReadTokens - usage.CacheWriteTokens);

    /// <summary>
    /// An hour-long cache write is billed at twice the base input price. Three facts hold that constant here instead of
    /// a fifth <see cref="ModelPricing"/> field: ① it is Anthropic's official rate for the one-hour tier (the short
    /// lifetime writes at 1.25×); ② the fifth <see cref="TokenUsage"/> field's only producer is the Anthropic adapter —
    /// <c>AnthropicProvider</c> keeps <c>ephemeral_1h_input_tokens</c> out of <c>InputTokens</c> and reports it there,
    /// every other adapter leaves it zero — so the formula's scope coincides with the field's producer; ③ the 2× ratio
    /// is knowledge the product already ships: it is the promotion rationale in <c>AgentOptions.CacheTtl</c>'s remarks
    /// and the user-visible cache-lifetime hover text. If Anthropic changes the rate or a second provider bills 1h
    /// writes differently, this becomes a pricing field (a contract change to register, the path stays open).
    /// </summary>
    private const double OneHourCacheWriteInputMultiple = 2;

    /// <summary>
    /// Cost in US dollars for <paramref name="usage"/>, or <c>null</c> when the model's prices are unknown. Cache writes
    /// are billed at their own (usually higher) rate; a provider that does not bill them reports zero for that path,
    /// and hour-long cache writes at <see cref="OneHourCacheWriteInputMultiple"/> the input rate. Every token count is
    /// clamped to non-negative first, so a malformed usage record (an adapter that reports a negative delta) cannot
    /// turn into a negative price.
    /// </summary>
    public static double? Cost(TokenUsage usage, ModelPricing? pricing)
    {
        if (pricing is null)
        {
            return null;
        }

        const double PerToken = 1d / 1_000_000d;
        // No double billing, on either side of the cache-write split. The one-hour write is a PART of CacheWriteTokens
        // (TokenUsage's contract; Anthropic reports the split), so the five-minute remainder — the total minus the 1h
        // part — is billed once at the cache-write rate and the 1h part once at its own 2× input rate. A malformed record
        // claiming more 1h tokens than the total is clamped back into it rather than turning into a negative price.
        var cacheWriteTokens = Math.Max(0, usage.CacheWriteTokens);
        var cacheWrite1hTokens = Math.Clamp(usage.CacheWrite1hTokens, 0, cacheWriteTokens);
        var cost = BilledInputTokens(usage) * pricing.Input * PerToken
            + Math.Max(0, usage.CacheReadTokens) * pricing.CacheRead * PerToken
            + (cacheWriteTokens - cacheWrite1hTokens) * pricing.CacheWrite * PerToken
            + cacheWrite1hTokens * pricing.Input * OneHourCacheWriteInputMultiple * PerToken
            + Math.Max(0, usage.OutputTokens) * pricing.Output * PerToken;
        return Math.Max(0, cost);
    }
}
