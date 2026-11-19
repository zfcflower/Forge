using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The session cost formula's cache-write split: the one-hour write is a part of CacheWriteTokens (TokenUsage's contract,
/// Anthropic reports the split), so the five-minute remainder — total minus the 1h part — is billed once at the
/// cache-write rate and the 1h part once at its own 2× input rate. No token is billed on two lines.
/// </summary>
public sealed class SessionUsageMathTests
{
    private static readonly ModelPricing Pricing = new(Input: 3, Output: 15, CacheRead: 0.3, CacheWrite: 3.75);

    private static double PerMillion(double tokens, double price) => tokens * price * (1d / 1_000_000d);

    [Fact]
    public void The_hour_write_is_billed_at_twice_input_and_the_remainder_at_the_write_rate()
    {
        // 1100 cache-write tokens of which 1000 are the hour-long kind: 100 at 1.25×, 1000 at 2×, nothing twice.
        var usage = new TokenUsage(10_000, 2_000, 8_000, 1_100, CacheWrite1hTokens: 1_000);

        Assert.Equal(
            PerMillion(10_000 - 8_000 - 1_100, 3)
                + PerMillion(8_000, 0.3)
                + PerMillion(1_100 - 1_000, 3.75)
                + PerMillion(1_000, 3 * 2)
                + PerMillion(2_000, 15),
            SessionUsageMath.Cost(usage, Pricing));
    }

    [Fact]
    public void A_one_hour_only_write_bills_the_hour_line_and_no_five_minute_remainder()
    {
        // Every cache-write token is the hour-long kind: the write-rate line is zero, the 2× line carries all of it.
        var usage = new TokenUsage(0, 0, 0, 100, CacheWrite1hTokens: 100);

        Assert.Equal(PerMillion(100, 3 * 2), SessionUsageMath.Cost(usage, Pricing));
    }

    [Fact]
    public void A_zero_one_hour_write_costs_the_four_field_formula()
    {
        var usage = new TokenUsage(1000, 100, 400, 200, 0);
        Assert.Equal(
            PerMillion(1000 - 400 - 200, 3) + PerMillion(400, 0.3) + PerMillion(200, 3.75) + PerMillion(100, 15),
            SessionUsageMath.Cost(usage, Pricing));
    }

    [Fact]
    public void A_malformed_record_claiming_more_hour_tokens_than_the_total_is_clamped()
    {
        // 50 written in total while 100 are claimed as hour-long: the hour part clamps to the total, so the five-minute
        // remainder is zero and the price stays positive instead of turning negative.
        var usage = new TokenUsage(0, 0, 0, 50, CacheWrite1hTokens: 100);

        Assert.Equal(PerMillion(50, 3 * 2), SessionUsageMath.Cost(usage, Pricing));
    }

    [Fact]
    public void Unknown_pricing_keeps_the_cost_unknown()
    {
        Assert.Null(SessionUsageMath.Cost(new TokenUsage(100, 50, 10, 5, 5), null));
    }

}
