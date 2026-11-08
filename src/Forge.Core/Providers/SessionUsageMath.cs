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
    /// Cost in US dollars for <paramref name="usage"/>, or <c>null</c> when the model's prices are unknown. Cache writes
    /// are billed at their own (usually higher) rate; a provider that does not bill them reports zero for that path. Every
    /// token count is clamped to non-negative first, so a malformed usage record (an adapter that reports a negative
    /// delta) cannot turn into a negative price.
    /// </summary>
    public static double? Cost(TokenUsage usage, ModelPricing? pricing)
    {
        if (pricing is null)
        {
            return null;
        }

        const double PerToken = 1d / 1_000_000d;
        var cost = BilledInputTokens(usage) * pricing.Input * PerToken
            + Math.Max(0, usage.CacheReadTokens) * pricing.CacheRead * PerToken
            + Math.Max(0, usage.CacheWriteTokens) * pricing.CacheWrite * PerToken
            + Math.Max(0, usage.OutputTokens) * pricing.Output * PerToken;
        return Math.Max(0, cost);
    }
}
