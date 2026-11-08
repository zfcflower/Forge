using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the derived usage numbers behind the strip (task spec "Forge.Core", <c>SessionUsageMath</c>): the cache hit ratio
/// divides by the prompt size that already includes the cached tokens, the context ratio is clipped to a real percentage
/// and unknown when the window is, and the cost bills the non-cached input, both cache paths and the output — or is
/// <c>null</c> when the model's prices are unknown, so the strip shows no invented $0.00.
/// </summary>
public sealed class SessionUsageMathTests
{
    private static readonly ModelPricing Pricing = new(Input: 3.00, Output: 15.00, CacheRead: 0.30, CacheWrite: 3.75);

    // ---------------------------------------------------------------- cache hit ratio

    [Fact]
    public void A_prompt_of_nothing_has_no_cache_hit_ratio_instead_of_not_a_number()
    {
        Assert.Equal(0d, SessionUsageMath.CacheHitRatio(TokenUsage.Empty));
        Assert.Equal(0d, SessionUsageMath.CacheHitRatio(new TokenUsage(0, 500, CacheReadTokens: 400)));
    }

    [Fact]
    public void The_cache_hit_ratio_is_the_cached_share_of_the_prompt()
    {
        Assert.Equal(0.25, SessionUsageMath.CacheHitRatio(new TokenUsage(800, 100, CacheReadTokens: 200)));
        Assert.Equal(1d, SessionUsageMath.CacheHitRatio(new TokenUsage(800, 100, CacheReadTokens: 800)));
    }

    [Fact]
    public void The_cache_hit_ratio_is_clamped_into_zero_to_one()
    {
        // A provider that reports more cached tokens than prompt tokens cannot produce 120%.
        Assert.Equal(1d, SessionUsageMath.CacheHitRatio(new TokenUsage(1_000, 10, CacheReadTokens: 1_200)));
        Assert.Equal(0d, SessionUsageMath.CacheHitRatio(new TokenUsage(1_000, 10, CacheReadTokens: -50)));
    }

    // ---------------------------------------------------------------- context ratio

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-4_096)]
    public void Without_a_known_window_the_context_ratio_is_null(int? window)
    {
        Assert.Null(SessionUsageMath.ContextRatio(9_000, window));
    }

    [Fact]
    public void The_context_ratio_is_the_share_of_the_window_that_the_last_prompt_used()
    {
        Assert.Equal(0.09, SessionUsageMath.ContextRatio(18_000, 200_000)!.Value);
        Assert.Equal(0d, SessionUsageMath.ContextRatio(0, 200_000)!.Value);
    }

    [Fact]
    public void The_context_ratio_is_clamped_below_a_full_window()
    {
        Assert.Equal(1d, SessionUsageMath.ContextRatio(400_000, 200_000)!.Value);
        Assert.Equal(0d, SessionUsageMath.ContextRatio(-5, 200_000)!.Value);
    }

    // ---------------------------------------------------------------- billed input

    [Fact]
    public void Billed_input_is_the_prompt_without_the_cached_parts()
    {
        Assert.Equal(80_000, SessionUsageMath.BilledInputTokens(new TokenUsage(200_000, 1_000, 100_000, 20_000)));
    }

    [Fact]
    public void Billed_input_never_goes_negative()
    {
        Assert.Equal(0, SessionUsageMath.BilledInputTokens(TokenUsage.Empty));
        Assert.Equal(0, SessionUsageMath.BilledInputTokens(new TokenUsage(0, 100, CacheReadTokens: 300)));
        Assert.Equal(0, SessionUsageMath.BilledInputTokens(new TokenUsage(10, 100, 10, 10)));
    }

    // ---------------------------------------------------------------- cost

    [Fact]
    public void Without_prices_the_cost_is_null()
    {
        Assert.Null(SessionUsageMath.Cost(new TokenUsage(1_000, 1_000), null));
    }

    [Fact]
    public void The_cost_bills_every_path_at_its_own_price()
    {
        // 80k input × $3 + 100k cache read × $0.30 + 20k cache write × $3.75 + 10k output × $15, per one million tokens.
        var cost = SessionUsageMath.Cost(
            new TokenUsage(200_000, 10_000, CacheReadTokens: 100_000, CacheWriteTokens: 20_000),
            Pricing);

        Assert.NotNull(cost);
        Assert.Equal(0.24 + 0.03 + 0.075 + 0.15, cost!.Value, 9);
    }

    [Fact]
    public void Cached_input_is_not_billed_a_second_time_at_the_full_input_rate()
    {
        // The prompt is entirely cache read: only the cache-read path may be charged.
        var cost = SessionUsageMath.Cost(new TokenUsage(1_000_000, 0, CacheReadTokens: 1_000_000), Pricing);

        Assert.NotNull(cost);
        Assert.Equal(0.30, cost!.Value, 9);
    }

    [Fact]
    public void An_output_only_usage_costs_only_the_output()
    {
        var cost = SessionUsageMath.Cost(new TokenUsage(0, 1_000_000), Pricing);

        Assert.NotNull(cost);
        Assert.Equal(15.0, cost!.Value, 9);
    }

    [Fact]
    public void A_model_that_bills_nothing_costs_zero_rather_than_being_unknown()
    {
        var cost = SessionUsageMath.Cost(new TokenUsage(1_000_000, 1_000_000), ModelPricing.None);

        Assert.Equal(0d, cost!.Value, 9);
    }

    [Fact]
    public void Cache_read_tokens_beyond_the_prompt_are_not_billed_at_the_input_rate()
    {
        var cost = SessionUsageMath.Cost(new TokenUsage(0, 0, CacheReadTokens: 1_000), Pricing);

        Assert.Equal(0.0003, cost!.Value, 9);
    }
}
