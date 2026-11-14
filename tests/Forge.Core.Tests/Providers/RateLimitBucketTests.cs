using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// The one budget behind every provider (<see cref="RateLimitBucket"/>): a token bucket refilled lazily from the injected
/// clock, charged only when the request may leave. Every assertion here is arithmetic the test can predict — the clock is
/// moved by hand, so a wait is an exact number, not "something above zero".
/// </summary>
public class RateLimitBucketTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ authorizing

    [Fact]
    public void A_full_bucket_authorizes_and_charges_the_cost()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 10, perSecondTokens: 0);

        var decision = bucket.Take(3, out var retryAfter);

        Assert.Equal(RateLimitDecision.Authorized, decision);
        Assert.Equal(TimeSpan.Zero, retryAfter);
        Assert.Equal(7, bucket.RemainingTokens);
        Assert.Equal(10, bucket.CapacityTokens);
    }

    [Fact]
    public void A_refused_take_charges_nothing_so_a_retry_pays_once()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 4, perSecondTokens: 0.5);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(3, out _));
        Assert.Equal(1, bucket.RemainingTokens);

        var decision = bucket.Take(3, out var retryAfter);

        Assert.Equal(RateLimitDecision.Wait, decision);
        Assert.Equal(TimeSpan.FromSeconds(4), retryAfter);
        Assert.Equal(1, bucket.RemainingTokens);

        // The same request, four seconds later, fits: the refused attempt left the balance alone.
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(3, out _));
        Assert.Equal(0, bucket.RemainingTokens);
    }

    [Fact]
    public void A_cost_equal_to_the_capacity_is_authorized_and_empties_the_bucket()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 5, perSecondTokens: 1);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(5, out _));
        Assert.Equal(0, bucket.RemainingTokens);

        Assert.Equal(RateLimitDecision.Wait, bucket.Take(1, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(1), retryAfter);
    }

    [Fact]
    public void A_cost_of_zero_only_checks_that_the_bucket_is_open()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 1, perSecondTokens: 0);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(0, out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
        Assert.Equal(1, bucket.RemainingTokens);
    }

    // ------------------------------------------------------------------ the wait

    [Fact]
    public void The_wait_is_linear_and_shrinks_as_the_clock_moves()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 10, perSecondTokens: 1);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(10, out _));

        // Two tokens are missing and one arrives per second.
        Assert.Equal(RateLimitDecision.Wait, bucket.Take(2, out var first));
        Assert.Equal(TimeSpan.FromSeconds(2), first);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RateLimitDecision.Wait, bucket.Take(2, out var second));
        Assert.Equal(TimeSpan.FromSeconds(1), second);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(2, out var third));
        Assert.Equal(TimeSpan.Zero, third);
        Assert.Equal(0, bucket.RemainingTokens);
    }

    [Fact]
    public void An_empty_bucket_with_no_refill_rate_never_refills()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 2, perSecondTokens: 0);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(2, out _));

        Assert.Equal(RateLimitDecision.NeverRefills, bucket.Take(1, out var retryAfter));
        Assert.Equal(TimeSpan.MaxValue, retryAfter);

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, bucket.RemainingTokens);
    }

    [Fact]
    public void A_cost_above_the_capacity_exceeds_it_and_charges_nothing()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 2, perSecondTokens: 1);

        var decision = bucket.Take(3, out var retryAfter);

        Assert.Equal(RateLimitDecision.ExceedsCapacity, decision);
        Assert.Equal(TimeSpan.Zero, retryAfter);
        Assert.Equal(2, bucket.RemainingTokens);
    }

    // ------------------------------------------------------------------ refilling

    [Fact]
    public void Refill_accumulates_and_stops_at_the_capacity()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 10, perSecondTokens: 1);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(10, out _));

        // The remaining count itself reflects the credit, without charging for it.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, bucket.RemainingTokens);

        // Ten seconds of credit into a bucket holding seven clamps at ten.
        time.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(10, bucket.RemainingTokens);
    }

    [Fact]
    public void A_refill_smaller_than_one_token_is_kept()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 4, perSecondTokens: 0.25);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(4, out _));

        // Six seconds = 1.5 tokens: the request takes one and the half must survive into the next window.
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
        Assert.Equal(0, bucket.RemainingTokens);

        // Two more seconds add the other half token, and there is a whole one again.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
        Assert.Equal(0, bucket.RemainingTokens);

        // One more second is a quarter token: three quarters are missing at a quarter per second, so three seconds —
        // not the four a bucket blind to the fraction already earned would report.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RateLimitDecision.Wait, bucket.Take(1, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(3), retryAfter);

        // Waiting exactly that long fits the request.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
    }

    [Fact]
    public void Four_seconds_of_a_quarter_token_rate_add_exactly_one_token()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 4, perSecondTokens: 0.25);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(4, out _));
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
        Assert.Equal(0, bucket.RemainingTokens);
    }

    // ------------------------------------------------------------------ Configure / SetRemaining / Adjust

    [Fact]
    public void Configure_lowers_the_ceiling_and_trims_the_balance_at_once()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 10, perSecondTokens: 1);
        Assert.Equal(10, bucket.RemainingTokens);

        bucket.Configure(capacityTokens: 4, perSecondTokens: 1);

        Assert.Equal(4, bucket.CapacityTokens);
        Assert.Equal(4, bucket.RemainingTokens);
        Assert.Equal(RateLimitDecision.ExceedsCapacity, bucket.Take(5, out _));
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(4, out _));
    }

    [Fact]
    public void Configure_raises_the_ceiling_without_handing_out_free_credit()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 4, perSecondTokens: 1);
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));

        bucket.Configure(capacityTokens: 10, perSecondTokens: 1);

        Assert.Equal(10, bucket.CapacityTokens);
        Assert.Equal(3, bucket.RemainingTokens);

        // The raised ceiling is where the refill now stops.
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(10, bucket.RemainingTokens);
    }

    [Fact]
    public void Configure_also_replaces_the_refill_rate()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 2, perSecondTokens: 0);
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(2, out _));
        Assert.Equal(RateLimitDecision.NeverRefills, bucket.Take(1, out _));

        bucket.Configure(capacityTokens: 2, perSecondTokens: 1);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
    }

    [Fact]
    public void SetRemaining_clamps_to_the_ceiling_and_never_goes_negative()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 5, perSecondTokens: 0);

        bucket.SetRemaining(3);
        Assert.Equal(3, bucket.RemainingTokens);

        bucket.SetRemaining(-2);
        Assert.Equal(0, bucket.RemainingTokens);

        bucket.SetRemaining(50);
        Assert.Equal(5, bucket.RemainingTokens);

        bucket.SetRemaining(0);
        Assert.Equal(RateLimitDecision.NeverRefills, bucket.Take(1, out _));
    }

    [Fact]
    public void Adjust_adds_credit_and_clamps_at_the_ceiling()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 5, perSecondTokens: 0);
        bucket.SetRemaining(1);

        bucket.Adjust(3);
        Assert.Equal(4, bucket.RemainingTokens);

        bucket.Adjust(100);
        Assert.Equal(5, bucket.RemainingTokens);
    }

    [Fact]
    public void Adjust_with_a_negative_delta_takes_credit_back_and_clamps_at_zero()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 5, perSecondTokens: 0);

        // A negative delta has to take credit back: ProviderRateLimiter.Settle relies on exactly this to charge an answer
        // that cost more than the estimate. A two-token debt leaves three of the five.
        bucket.Adjust(-2);
        Assert.Equal(3, bucket.RemainingTokens);

        bucket.Adjust(-100);
        Assert.Equal(0, bucket.RemainingTokens);
    }

    [Fact]
    public void A_refunded_slot_can_be_spent_again()
    {
        var time = new SteppingTimeProvider(Start);
        var bucket = new RateLimitBucket(time, capacityTokens: 1, perSecondTokens: 0);
        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));

        // What ProviderRateLimiter.Acquire does when the token bucket refuses: hand the request slot back.
        bucket.Adjust(1);

        Assert.Equal(RateLimitDecision.Authorized, bucket.Take(1, out _));
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public void Concurrent_takes_never_spend_more_than_the_capacity()
    {
        const int Capacity = 100;
        const long Cost = 7;
        const int Threads = 8;
        const int AttemptsPerThread = 50;
        var bucket = new RateLimitBucket(new SteppingTimeProvider(Start), Capacity, perSecondTokens: 0);
        var authorized = 0;
        var start = new ManualResetEventSlim(false);

        var workers = Enumerable.Range(0, Threads)
            .Select(worker => new Thread(() =>
            {
                start.Wait();
                for (var attempt = 0; attempt < AttemptsPerThread; attempt++)
                {
                    if (bucket.Take(Cost, out _) == RateLimitDecision.Authorized)
                    {
                        Interlocked.Increment(ref authorized);
                    }
                }
            }))
            .ToList();
        foreach (var worker in workers)
        {
            worker.Start();
        }

        start.Set();
        foreach (var worker in workers)
        {
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "a worker never finished");
        }

        // 100 tokens at 7 each: fourteen requests fit, and the refused ones must have changed nothing.
        Assert.Equal(Capacity / (int)Cost, authorized);
        Assert.Equal(Capacity - (authorized * (int)Cost), bucket.RemainingTokens);
    }
}

/// <summary>
/// A clock whose <see cref="TimeProvider.GetTimestamp"/> moves with <see cref="Advance"/>: the bucket refills lazily from
/// the timestamp domain, so a test that only moved the UTC clock would leave every refill at zero.
/// </summary>
internal sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long ticks;
    private DateTimeOffset now = start;

    /// <summary>One timestamp unit is one tick, so a duration converts exactly.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref ticks);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref ticks, (long)(by.TotalSeconds * TimestampFrequency));
        now += by;
    }
}
