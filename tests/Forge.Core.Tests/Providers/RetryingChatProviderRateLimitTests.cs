using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// The retry middleware as the one place a request is charged to its provider's budget (<see cref="ProviderRateLimiter"/>):
/// every attempt pays, a payment wait does not consume an attempt, a wait longer than the budget limit is reported as a 429,
/// and what the answer really cost replaces the estimate. Nothing here sleeps: the wait hook both records the delay and
/// moves the test clock, so the numbers asserted are the ones the middleware computed.
/// </summary>
public class RetryingChatProviderRateLimitTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The estimate every request in this class carries: the counter below charges 100 tokens per prompt string.</summary>
    private const int Estimate = 100;

    // ------------------------------------------------------------------ charging every attempt

    [Fact]
    public async Task Every_attempt_is_charged_so_the_second_one_waits_for_the_budget()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 100 });
        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Fails(), Completes());

        var events = await Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request());

        Assert.Equal(2, endpoint.Calls);
        Assert.Equal("ok", events.Text());

        // The retry's own backoff, then the budget wait: one request's worth of tokens had to be earned back.
        var waits = Waits(delays);
        Assert.Equal(2, waits.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(100), waits[0]);
        Assert.InRange(waits[1], TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60.0));
    }

    [Fact]
    public async Task A_budget_wait_does_not_consume_one_of_the_attempts()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 100 });

        // Already spent: the first attempt has to pay before it may leave, and every later one pays again.
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", Estimate, out _));

        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Fails(), Completes());
        var events = await Wrap(endpoint, limiter, time, delays, maxAttempts: 2).CollectAsync(TestData.Request());

        // Two real attempts, each preceded by its own payment wait: if a wait counted as an attempt the second one
        // would never have run and the call would have failed instead of returning the answer.
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal("ok", events.Text());
        var waits = Waits(delays);
        Assert.Equal(3, waits.Length);
        Assert.InRange(waits[0], TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60.0));
        Assert.Equal(TimeSpan.FromMilliseconds(100), waits[1]);
        Assert.InRange(waits[2], TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60.0));
    }

    [Fact]
    public async Task A_request_larger_than_the_whole_budget_is_sent_anyway()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 50 });
        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Completes());

        var events = await Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request());

        // Waiting could never make room for a request bigger than the whole ceiling, so it is not waited for.
        Assert.Equal(1, endpoint.Calls);
        Assert.Equal("ok", events.Text());
        Assert.Empty(delays);
    }

    // ------------------------------------------------------------------ a wait that is not slept through

    [Fact]
    public async Task A_wait_longer_than_the_budget_limit_becomes_a_quota_shaped_429()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);

        // A response said nothing is left for the next half hour: no retry loop should live through that.
        var reset = Start + TimeSpan.FromMinutes(30);
        limiter.Observe("p", ReadingWith(new ProviderThrottle(10, 0, reset, null, null, null)), Start);

        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Completes());

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request()));

        Assert.Equal(429, failure.StatusCode);
        Assert.True(failure.Retryable);
        Assert.True(ProviderErrors.TryGetRetryAfter(failure, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        Assert.NotNull(ProviderQuota.Classify(failure, new ModelSettings("m", ProviderId: "p"), Start));

        // Nothing was sent and nothing was slept through.
        Assert.Equal(0, endpoint.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_failure_that_reports_a_rate_limit_holds_the_retry_until_its_reset()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        var reset = Start + TimeSpan.FromSeconds(90);
        var delays = new List<TimeSpan>();

        // The refusal carries its own minute-scale reading: nothing is left and the window reopens in 90 s.
        var endpoint = new ScriptedEndpoint(
            "p",
            Fails(ReadingWith(new ProviderThrottle(null, 0, reset, null, null, null))),
            Completes());

        var events = await Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request());

        Assert.Equal(2, endpoint.Calls);
        Assert.Equal("ok", events.Text());
        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[0]);

        // The retry waits out the announced window (less the backoff already spent), not the default backoff.
        Assert.InRange(delays[1], TimeSpan.FromSeconds(89.5), TimeSpan.FromSeconds(90));
    }

    // ------------------------------------------------------------------ settling against the real usage

    [Theory]
    // Input + cache read + cache write: a provider meters all of it, so the refund is the whole difference.
    [InlineData(30, 10, 5, 955)]
    [InlineData(30, 0, 0, 970)]
    [InlineData(100, 0, 0, 900)]
    // No usage at all is not a free request: the estimate stands.
    [InlineData(0, 0, 0, 900)]
    public async Task The_retry_settles_the_estimate_against_the_usage_it_reported(
        int inputTokens,
        int cacheReadTokens,
        int cacheWriteTokens,
        int expectedRoom)
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 1000 });
        var delays = new List<TimeSpan>();
        var usage = new TokenUsage(inputTokens, 1, cacheReadTokens, cacheWriteTokens);
        var endpoint = new ScriptedEndpoint("p", Completes(usage));

        await Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request());

        // The balance is read by asking for it: exactly `expectedRoom` tokens are left, one more is refused.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", expectedRoom + 1, out _));
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", expectedRoom, out _));
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task A_cancelled_call_surfaces_and_charges_nothing()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 1000 });
        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Completes());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Wrap(endpoint, limiter, time, delays).CollectAsync(TestData.Request(), cts.Token));

        Assert.Equal(0, endpoint.Calls);
        Assert.Empty(delays);

        // Nothing was charged: the whole budget is still there.
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", 1000, out _));
    }

    [Fact]
    public async Task A_cancel_during_a_budget_wait_surfaces_and_sends_nothing()
    {
        var time = new SteppingTimeProvider(Start);
        var limiter = new ProviderRateLimiter(time);
        limiter.Publish(new ProviderRateLimitPolicy { RequestsPerMinute = 0, TokensPerMinute = 100 });
        Assert.Equal(RateLimitDecision.Authorized, limiter.Acquire("p", Estimate, out _));
        using var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Completes());
        var provider = new RetryingChatProvider(
            endpoint,
            maxAttempts: 2,
            baseDelay: TimeSpan.FromMilliseconds(100),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                cts.Cancel();
                return ValueTask.CompletedTask;
            },
            jitter: delay => delay,
            timeProvider: time,
            rateLimiter: limiter,
            tokenCounter: new FlatTokenCounter(Estimate));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CollectAsync(TestData.Request(), cts.Token));

        Assert.Equal(0, endpoint.Calls);
        Assert.InRange(Assert.Single(delays), TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60.0));

        // The cancelled request paid nothing: the bucket is still empty, so a one-token request still waits for one.
        Assert.Equal(RateLimitDecision.Wait, limiter.Acquire("p", 1, out var empty));
        Assert.InRange(empty, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------------ wiring it up changes nothing else

    [Fact]
    public async Task A_budget_with_room_changes_neither_the_events_nor_the_delays()
    {
        var withoutLimiter = await RunScript(limiter: null);

        var time = new SteppingTimeProvider(Start);
        var loose = new ProviderRateLimiter(time);
        var withLimiter = await RunScript(loose, time);

        var off = new ProviderRateLimiter(time);
        off.Publish(ProviderRateLimitPolicy.Default with { Enabled = false });
        var withDisabledLimiter = await RunScript(off, time);

        Assert.Equal(withoutLimiter.Calls, withLimiter.Calls);
        Assert.Equal(withoutLimiter.Calls, withDisabledLimiter.Calls);
        Assert.Equal(withoutLimiter.Text, withLimiter.Text);
        Assert.Equal(withoutLimiter.Text, withDisabledLimiter.Text);
        Assert.Equal(withoutLimiter.Delays, withLimiter.Delays);
        Assert.Equal(withoutLimiter.Delays, withDisabledLimiter.Delays);
    }

    private static async Task<(int Calls, string Text, TimeSpan[] Delays)> RunScript(
        ProviderRateLimiter? limiter,
        TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        var delays = new List<TimeSpan>();
        var endpoint = new ScriptedEndpoint("p", Fails(), Completes());

        var events = await Wrap(endpoint, limiter, clock, delays).CollectAsync(TestData.Request());
        return (endpoint.Calls, events.Text(), delays.ToArray());
    }

    // ------------------------------------------------------------------ the harness

    private static RetryingChatProvider Wrap(
        IChatProvider inner,
        ProviderRateLimiter? limiter,
        TimeProvider time,
        List<TimeSpan> delays,
        int maxAttempts = 3) =>
        new(
            inner,
            maxAttempts: maxAttempts,
            baseDelay: TimeSpan.FromMilliseconds(100),
            delayAsync: (delay, _) =>
            {
                // Recording the wait and moving the clock together is what makes "no real sleeping" observable: the
                // budget that asked for the wait is refilled by exactly that much when the delay is over.
                delays.Add(delay);
                if (time is SteppingTimeProvider stepping)
                {
                    stepping.Advance(delay);
                }

                return ValueTask.CompletedTask;
            },
            jitter: delay => delay,
            timeProvider: time,
            rateLimiter: limiter,
            tokenCounter: new FlatTokenCounter(Estimate));

    private static Func<ChatStreamEvent[]> Fails(ProviderRateLimit? rateLimit = null) =>
        () => throw new ProviderException("the endpoint refused the request", retryable: true) { RateLimit = rateLimit };

    private static Func<ChatStreamEvent[]> Completes(TokenUsage? usage = null) =>
        () =>
        [
            new TextDelta("ok"),
            new MessageCompleted(ChatMessage.Assistant("ok"), usage ?? TokenUsage.Empty, StopReason.EndTurn),
        ];

    /// <summary>
    /// The waits the middleware meant to wait. The refill is fixed point, so a wait that a rounding residue left one unit
    /// short is asked for again with a sub-millisecond delay; counting those as waits would be counting the same wait twice.
    /// </summary>
    private static TimeSpan[] Waits(IEnumerable<TimeSpan> delays) =>
        delays.Where(static delay => delay >= TimeSpan.FromMilliseconds(1)).ToArray();

    private static ProviderRateLimit ReadingWith(ProviderThrottle throttle) =>
        new(null, null, RateLimitStatus.Unknown, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown)
        {
            Throttle = throttle,
        };

    /// <summary>An endpoint that replays one step per call, so "how often was this charged" is just <see cref="Calls"/>.</summary>
    private sealed class ScriptedEndpoint(string id, params Func<ChatStreamEvent[]>[] steps) : IChatProvider
    {
        private readonly Queue<Func<ChatStreamEvent[]>> pending = new(steps);

        public string Id { get; } = id;

        public int Calls { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (!pending.TryDequeue(out var step))
            {
                throw new ProviderException($"{Id}: the script is exhausted", retryable: false);
            }

            foreach (var evt in step())
            {
                yield return evt;
            }
        }
    }

    /// <summary>A counter with a fixed answer, so the estimate a test reasons about is a constant.</summary>
    private sealed class FlatTokenCounter(int perText) : ITokenCounter
    {
        public int Count(IReadOnlyList<ChatMessage> messages) => 0;

        public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : perText;
    }
}
