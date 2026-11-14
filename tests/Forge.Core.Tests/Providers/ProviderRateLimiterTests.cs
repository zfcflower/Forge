using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// The per-provider budget every model request is acquired from (<see cref="ProviderRateLimiter"/>): one bucket pair per
/// provider, calibrated by what a response reported, and the reservation that spaces automated resumes. The clock is moved
/// by hand, so every wait is the exact number the arithmetic says it is.
/// </summary>
public class ProviderRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ policy

    [Fact]
    public void The_policy_in_force_before_anything_is_published_is_the_loose_default()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));

        Assert.Equal(ProviderRateLimitPolicy.Default, limiter.Policy);
        Assert.True(limiter.Policy.Enabled);
        Assert.Equal(600, limiter.Policy.RequestsPerMinute);
        Assert.Equal(4_000_000, limiter.Policy.TokensPerMinute);
        Assert.Equal(TimeSpan.FromSeconds(10), limiter.Policy.AutomationGap);
    }

    [Fact]
    public void The_default_policy_lets_an_ordinary_turn_through()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));

        // A minute of ordinary agent turns: dozens of requests, not a rejection.
        for (var request = 0; request < 50; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 50_000, out _));
        }
    }

    [Fact]
    public void Publish_normalizes_negative_numbers_to_no_limit_and_no_gap()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));

        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = -5,
            TokensPerMinute = -1,
            AutomationGap = TimeSpan.FromSeconds(-3),
        });

        Assert.Equal(0, limiter.Policy.RequestsPerMinute);
        Assert.Equal(0, limiter.Policy.TokensPerMinute);
        Assert.Equal(TimeSpan.Zero, limiter.Policy.AutomationGap);
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));

        // Zero on both dimensions means "do not limit", so even an oversized request leaves.
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 100_000_000, out _));
    }

    [Fact]
    public void A_disabled_policy_authorizes_without_charging_and_leaves_the_bucket_as_it_was()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var tight = new ProviderRateLimitPolicy { RequestsPerMinute = 1, TokensPerMinute = 0 };
        limiter.Publish(tight);

        // Drain the only request slot first, so "did it charge while disabled" is visible afterwards.
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var before));
        Assert.InRange(before, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(58));

        limiter.Publish(tight with { Enabled = false });
        for (var request = 0; request < 100; request++)
        {
            Assert.Equal(RateLimitDecision.Disabled, limiter.Acquire("p", 0, out var retryAfter));
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }

        time.Advance(TimeSpan.FromSeconds(5));
        limiter.Publish(tight);

        // Still empty: 5 s of refill was counted, 100 disabled requests were not charged.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var after));
        Assert.InRange(after, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(54));
    }

    // ------------------------------------------------------------------ per provider

    [Fact]
    public void Each_provider_has_its_own_budget_and_its_own_wait()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1, TokensPerMinute = 0 });

        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("a", 0, out _));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("b", 0, out _));

        // "a" has been refilling for 30 s, "b" has not: emptying one provider does not touch the other.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("a", 0, out var a));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("b", 0, out var b));
        Assert.InRange(a, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(28));
        Assert.InRange(b, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(58));
        Assert.True(a < b, "the two providers must not share a bucket");
    }

    [Fact]
    public void A_token_refusal_hands_the_request_slot_back()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);

        // Two request slots, one minute's worth of tokens: a token refusal must not also spend a request.
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 2, TokensPerMinute = 60 });
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 60, out _));

        // Refused on tokens three times; each one hands its request slot back.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 60, out var tokens));
            Assert.InRange(tokens, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(61));
        }

        // The token dimension is switched off, so what is left to spend is exactly the request slots. One is gone with
        // the first request and each refusal returned one: exactly one more request fits, not two (a double refund) and
        // not none (no refund at all).
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 2, TokensPerMinute = 0 });
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 60, out _));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 60, out var requests));
        Assert.InRange(requests, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(31));
    }

    [Fact]
    public void Zero_requests_per_minute_skips_the_request_dimension()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 0 });

        for (var request = 0; request < 500; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        }
    }

    [Fact]
    public void Zero_tokens_per_minute_skips_the_token_dimension()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1000, TokensPerMinute = 0 });

        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 1_000_000, out _));

        // The same request against a real token ceiling is one request too large for the whole budget.
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1000, TokensPerMinute = 10 });
        Assert.Equal(RateLimitDecision.ExceedsCapacity, limiter.Acquire("p", 1_000_000, out _));
    }

    // ------------------------------------------------------------------ Publish

    [Fact]
    public void Publish_lowers_a_ceiling_an_existing_bucket_already_had()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1000, TokensPerMinute = 0 });
        for (var request = 0; request < 100; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        }

        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 5, TokensPerMinute = 0 });

        var granted = 0;
        TimeSpan wait = TimeSpan.Zero;
        for (var request = 0; request < 10; request++)
        {
            if (limiter.Acquire("p", 0, out var retryAfter) == RateLimitDecision.Authorized)
            {
                granted++;
            }
            else
            {
                wait = retryAfter;
            }
        }

        // The ceiling the user just typed, applied to the bucket the running session already had.
        Assert.Equal(5, granted);
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));

        // A provider that had no bucket yet starts from the same numbers.
        Assert.Equal(5, Room(limiter, "fresh"));
    }

    // ------------------------------------------------------------------ Observe

    [Fact]
    public void Observe_adopts_the_reported_limit_and_leaves_exactly_the_reported_requests()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 100, TokensPerMinute = 0 });

        // The account reports three requests per window with one left, resetting in thirty seconds.
        limiter.Observe("p", Throttle(new ProviderThrottle(3, 1, Start + TimeSpan.FromSeconds(30), null, null, null)), Start);

        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out var first));
        Assert.Equal(TimeSpan.Zero, first);

        // The remaining one is gone: the next request waits for the refill, and never past the announced reset.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var second));
        Assert.InRange(second, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(30));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var third));
        Assert.InRange(third, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Observe_holds_every_request_until_the_reported_reset_when_nothing_is_left()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 100, TokensPerMinute = 0 });
        var reset = Start + TimeSpan.FromSeconds(45);

        limiter.Observe("p", Throttle(new ProviderThrottle(10, 0, reset, null, null, null)), Start);

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var held));
        Assert.InRange(held, TimeSpan.FromSeconds(44), TimeSpan.FromSeconds(45));

        time.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var nearly));
        Assert.InRange(nearly, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));

        // The hold has expired and the bucket refilled for the 46 s that passed: the request may leave again.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void Observe_holds_on_a_rejected_spending_window_until_its_reset()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var reset = Start + TimeSpan.FromMinutes(2);

        limiter.Observe("p", RejectedAt(reset), Start);

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var held));
        Assert.InRange(held, TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(120));

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void Observe_of_nothing_changes_nothing()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var reset = Start + TimeSpan.FromMinutes(2);
        limiter.Observe("p", RejectedAt(reset), Start);

        limiter.Observe("p", null, Start);

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var held));
        Assert.InRange(held, TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(120));

        // A limiter that never saw a reading keeps the numbers its policy gave it.
        var fresh = new ProviderRateLimiter(time);
        fresh.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1, TokensPerMinute = 0 });
        Assert.Equal(RateLimitDecision.Authorized, fresh.Acquire("q", 0, out _));
        fresh.Observe("q", null, Start);
        Assert.Equal(RateLimitDecision.Wait, fresh.Acquire("q", 0, out var unchanged));
        Assert.InRange(unchanged, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(58));
    }

    [Fact]
    public void Observe_with_a_remaining_count_releases_a_previous_hold()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var reset = Start + TimeSpan.FromMinutes(5);
        limiter.Observe("p", Throttle(new ProviderThrottle(10, 0, reset, null, null, null)), Start);
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));

        // The window came back early and the account has room again.
        limiter.Observe("p", Throttle(new ProviderThrottle(10, 4, reset, null, null, null)), Start);

        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void Calibration_is_not_erased_by_the_refill_window_it_replaced()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 60_000 });

        // A quiet minute above the low-water mark: no refill runs, so the bucket still carries its creation stamp while the
        // whole minute of credit is unclaimed. The server then reports that only 100 tokens are left - its number already
        // includes whatever its own window added up to that answer, so the unclaimed minute must not be handed out on top.
        time.Advance(TimeSpan.FromSeconds(60));
        limiter.Observe("p", Throttle(new ProviderThrottle(null, null, null, 60_000, 100, Start + TimeSpan.FromSeconds(60))), Start + TimeSpan.FromSeconds(60));

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 50_000, out _));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 100, out _));
    }

    [Fact]
    public void A_dimension_with_room_does_not_lift_the_hold_another_dimension_needs()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 10, TokensPerMinute = 60_000 });
        var reset = Start + TimeSpan.FromSeconds(60);

        // Requests are spent until the reset while tokens still have room: requests are calibrated first, so an unconditional
        // release in the token branch would lift the hold a moment after it was set.
        limiter.Observe("p", Throttle(new ProviderThrottle(10, 0, reset, 1000, 500, reset)), Start);

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 10, out var held));
        Assert.InRange(held, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
    }

    // ------------------------------------------------------------------ per-provider overrides

    [Fact]
    public void An_override_replaces_the_globals_for_that_provider_only()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 100,
            TokensPerMinute = 0,
            Overrides = new Dictionary<string, ProviderRateLimitLimits>
            {
                ["tight"] = new(RequestsPerMinute: 3),
            },
        });

        // Three requests leave on the overridden provider, the fourth waits...
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("tight", 0, out _));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("tight", 0, out _));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("tight", 0, out _));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("tight", 0, out var over));
        Assert.InRange(over, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(60));

        // ...while a provider without an entry still has the whole global allowance.
        for (var request = 0; request < 100; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("loose", 0, out _));
        }
    }

    [Fact]
    public void An_override_of_zero_lifts_the_limit_for_that_provider()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 2,
            TokensPerMinute = 0,
            Overrides = new Dictionary<string, ProviderRateLimitLimits>
            {
                ["unlimited"] = new(RequestsPerMinute: 0),
            },
        });

        for (var request = 0; request < 500; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("unlimited", 0, out _));
        }
    }

    [Fact]
    public void An_override_that_states_one_dimension_inherits_the_other()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 100,
            TokensPerMinute = 1000,
            Overrides = new Dictionary<string, ProviderRateLimitLimits>
            {
                ["p"] = new(RequestsPerMinute: 50),
            },
        });

        // 1000 tokens is the global token ceiling: a request for all of it fits exactly once, so the override left the token
        // dimension alone while cutting the request dimension in half.
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 1000, out _));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 1000, out _));
        for (var request = 0; request < 49; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        }

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void Removing_an_override_restores_the_global_ceilings()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 100,
            TokensPerMinute = 0,
            Overrides = new Dictionary<string, ProviderRateLimitLimits> { ["p"] = new(RequestsPerMinute: 1) },
        });
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));

        // The entry is cleared: the bucket that already exists is reconfigured in place, so it now holds 100 and refills at the
        // global rate. A raised ceiling does not hand out credit retroactively (that rule is pinned by the bucket's own tests),
        // so the minute that passes here is what fills it again.
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 100, TokensPerMinute = 0 });
        time.Advance(TimeSpan.FromSeconds(60));

        for (var request = 0; request < 100; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        }

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void A_reported_limit_never_loosens_a_hand_typed_override()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 100,
            TokensPerMinute = 0,
            Overrides = new Dictionary<string, ProviderRateLimitLimits> { ["p"] = new(RequestsPerMinute: 20) },
        });

        // The account says 50 with 45 left. The user typed 20 on purpose (a stricter ceiling than the vendor enforces is the
        // reason the field exists), so the effective ceiling is the lower of the two: 20.
        limiter.Observe("p", Throttle(new ProviderThrottle(50, 45, Start + TimeSpan.FromSeconds(60), null, null, null)), Start);

        for (var request = 0; request < 20; request++)
        {
            Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
        }

        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void A_reported_limit_below_the_override_tightens_the_bucket_and_survives_a_publish()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var policy = new ProviderRateLimitPolicy
        {
            RequestsPerMinute = 100,
            TokensPerMinute = 0,
            Overrides = new Dictionary<string, ProviderRateLimitLimits> { ["p"] = new(RequestsPerMinute: 50) },
        };
        limiter.Publish(policy);

        limiter.Observe("p", Throttle(new ProviderThrottle(20, 20, Start + TimeSpan.FromSeconds(60), null, null, null)), Start);
        Assert.Equal(20, Room(limiter, "p"));

        // A preference change republishes the policy. The reported 20 lives beside the bucket, so it is not washed out by the
        // override's 50 (or by a raised global): after a while the bucket refills to 20, not to 50 or 100.
        limiter.Publish(policy with { RequestsPerMinute = 500 });
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(20, Room(limiter, "p", ceiling: 200));
    }

    [Fact]
    public void A_reported_limit_meters_a_dimension_the_policy_left_unlimited()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 0 });

        limiter.Observe("p", Throttle(new ProviderThrottle(3, 3, null, null, null, null)), Start);

        Assert.Equal(3, Room(limiter, "p", ceiling: 50));
    }

    [Fact]
    public void Normalizing_the_policy_drops_an_override_that_states_nothing()
    {
        var policy = new ProviderRateLimitPolicy
        {
            Overrides = new Dictionary<string, ProviderRateLimitLimits>
            {
                ["empty"] = new(),
                ["negative"] = new(-5, -7),
            },
        }.Normalized();

        Assert.Equal(0, policy.Overrides["negative"].RequestsPerMinute);
        Assert.Equal(0, policy.Overrides["negative"].TokensPerMinute);
        Assert.False(policy.Overrides.ContainsKey("empty"));
    }

    // ------------------------------------------------------------------ hold exits, switch, races

    [Fact]
    public void An_accepted_answer_with_a_rejected_window_sets_no_hold_but_a_refusal_does()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        var reset = Start + TimeSpan.FromHours(2);

        // The answer proves the account took the request (an overage allowance can carry it past a spent window).
        limiter.Observe("p", RejectedAt(reset), Start, answered: true);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));

        limiter.Observe("p", RejectedAt(reset), Start);
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void A_hold_is_capped_at_an_hour_and_ends_with_a_policy_publish()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);

        limiter.Observe("p", RejectedAt(Start + TimeSpan.FromDays(7)), Start);
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 0, out var held));
        Assert.True(held <= TimeSpan.FromHours(1), held.ToString());

        // The user's way out: any publish (including switching the budget off and on again) clears what is held.
        limiter.Publish(ProviderRateLimitPolicy.Default);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 0, out _));
    }

    [Fact]
    public void A_disabled_policy_reserves_no_automation_slot()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { Enabled = false, AutomationGap = TimeSpan.FromSeconds(30) });

        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));

        // Switched back on, the spacing applies again from a clean slate (nothing was reserved while it was off).
        limiter.Publish(new ProviderRateLimitPolicy { Enabled = true, AutomationGap = TimeSpan.FromSeconds(30) });
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.ReserveAutomationSlot("p"));
    }

    [Fact]
    public async Task Buckets_created_while_a_policy_is_being_published_end_up_on_the_new_policy()
    {
        for (var round = 0; round < 20; round++)
        {
            var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
            limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 600, TokensPerMinute = 0 });
            using var go = new ManualResetEventSlim();
            var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                go.Wait();
                for (var id = 0; id < 200; id++)
                {
                    limiter.Acquire($"p{worker}-{id}", 0, out _);
                }
            })).ToArray();

            go.Set();
            limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 1, TokensPerMinute = 0 });
            await Task.WhenAll(workers);

            // Whether a bucket was built before, during or after the publish, none may be left on the old 600: at most one
            // request (the new ceiling) can still be authorized from any of them.
            for (var worker = 0; worker < 4; worker++)
            {
                for (var id = 0; id < 200; id++)
                {
                    var authorized = 0;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        if (limiter.Acquire($"p{worker}-{id}", 0, out _) == RateLimitDecision.Authorized)
                        {
                            authorized++;
                        }
                    }

                    Assert.InRange(authorized, 0, 1);
                }
            }
        }
    }

    // ------------------------------------------------------------------ charged tokens

    [Fact]
    public void Acquire_reports_the_tokens_it_charged_and_zero_when_it_charged_none()
    {
        var limiter = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 400, out _, out var charged));
        Assert.Equal(400, charged);

        // Refused, over capacity and disabled all charge nothing.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 700, out _, out charged));
        Assert.Equal(0, charged);
        Assert.Equal(RateLimitDecision.ExceedsCapacity, limiter.Acquire("p", 5000, out _, out charged));
        Assert.Equal(0, charged);

        limiter.Publish(new ProviderRateLimitPolicy { Enabled = false });
        Assert.Equal(RateLimitDecision.Disabled, limiter.Acquire("p", 400, out _, out charged));
        Assert.Equal(0, charged);
    }

    [Fact]
    public void Settling_what_was_never_charged_changes_nothing()
    {
        var limiter = TokensOnly(1000);

        // A request over the whole budget leaves with nothing metered (and empties the bucket); settling "reserved 5000 vs
        // actual 100" as if 5000 had been taken would hand back 4900 tokens of credit that never existed.
        Assert.Equal(RateLimitDecision.ExceedsCapacity, limiter.Acquire("p", 5000, out _, out var charged));
        limiter.Settle("p", charged, 100);

        Assert.Equal(0, Room(limiter, "p"));
    }

    // ------------------------------------------------------------------ Settle

    [Fact]
    public void Settle_with_the_numbers_it_reserved_leaves_the_balance_alone()
    {
        var limiter = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 400, out _));

        limiter.Settle("p", 400, 400);

        Assert.Equal(600, Room(limiter, "p"));
    }

    [Fact]
    public void Settle_with_a_larger_actual_count_charges_the_difference()
    {
        var limiter = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 400, out _));

        // The answer cost 900 tokens: the 500 the estimate did not foresee are taken now, leaving 100 of the 1000.
        limiter.Settle("p", 400, 900);

        Assert.Equal(100, Room(limiter, "p"));
    }

    [Fact]
    public void Settle_with_a_smaller_actual_count_hands_the_difference_back()
    {
        var limiter = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 400, out _));

        limiter.Settle("p", 400, 100);

        Assert.Equal(900, Room(limiter, "p"));
    }

    [Fact]
    public void Settle_clamps_to_the_capacity_and_to_zero()
    {
        var high = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, high.Acquire("p", 500, out _));
        high.Settle("p", 500, -1500);
        Assert.Equal(1000, Room(high, "p"));

        var low = TokensOnly(1000);
        Assert.Equal(RateLimitDecision.Authorized, low.Acquire("p", 1000, out _));
        low.Settle("p", 1000, 5000);
        Assert.Equal(0, Room(low, "p"));
    }

    [Fact]
    public void Settle_never_creates_a_budget_for_a_provider_it_never_saw()
    {
        var limiter = TokensOnly(1000);

        // If this invented a bucket it would take 800 tokens out of it and the room below would read 200.
        limiter.Settle("ghost", 100, 900);

        Assert.Equal(1000, Room(limiter, "ghost"));
    }

    // ------------------------------------------------------------------ automation slots

    [Fact]
    public void Three_reservations_in_one_instant_are_zero_gap_and_twice_the_gap()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));

        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
        Assert.Equal(TimeSpan.FromSeconds(10), limiter.ReserveAutomationSlot("p"));
        Assert.Equal(TimeSpan.FromSeconds(20), limiter.ReserveAutomationSlot("p"));

        // The default gap is the one the settings section shows.
        Assert.Equal(TimeSpan.FromSeconds(10), ProviderRateLimitPolicy.Default.AutomationGap);
    }

    [Fact]
    public void A_zero_gap_reserves_nothing()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(ProviderRateLimitPolicy.Default with { AutomationGap = TimeSpan.Zero });

        for (var resume = 0; resume < 3; resume++)
        {
            Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
        }
    }

    [Fact]
    public void Providers_do_not_share_a_slot_and_keys_are_case_sensitive()
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));

        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("a"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("b"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("A"));
        Assert.Equal(TimeSpan.FromSeconds(10), limiter.ReserveAutomationSlot("a"));
    }

    [Fact]
    public void A_reserved_slot_shrinks_with_time_and_resets_after_it_passes()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.ReserveAutomationSlot("p");
        limiter.ReserveAutomationSlot("p");
        limiter.ReserveAutomationSlot("p");

        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.ReserveAutomationSlot("p"));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("p"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A limiter that only meters tokens, so the remaining balance can be measured without request noise.</summary>
    private static ProviderRateLimiter TokensOnly(int tokensPerMinute)
    {
        var limiter = new ProviderRateLimiter(new SteppingTimeProvider(Start));
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = tokensPerMinute });
        return limiter;
    }

    /// <summary>
    /// How many one-token requests <paramref name="provider"/> can still pay for. Measured by spending them, so it reads
    /// the bucket the way the next request will, and the clock never moves so no refill can hide a wrong balance.
    /// </summary>
    private static int Room(ProviderRateLimiter limiter, string provider, int ceiling = 1000)
    {
        var room = 0;
        while (room < ceiling && limiter.Acquire(provider, 1, out _) == RateLimitDecision.Authorized)
        {
            room++;
        }

        return room;
    }

    private static ProviderRateLimit Throttle(ProviderThrottle throttle) =>
        new(null, null, RateLimitStatus.Unknown, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown)
        {
            Throttle = throttle,
        };

    private static ProviderRateLimit RejectedAt(DateTimeOffset reset) =>
        new(null, reset, RateLimitStatus.Rejected, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown);
}
