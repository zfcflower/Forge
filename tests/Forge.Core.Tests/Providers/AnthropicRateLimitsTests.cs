using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the account-window reading (task spec "provider quota meter"): the <c>anthropic-ratelimit-unified-*</c> headers are
/// the only place a usage window is visible before it is spent, so the parsing is tolerant (a missing header is null, never
/// an invented zero) and the rejected case is what the quota classifier now trusts above any wording.
/// </summary>
public sealed class AnthropicRateLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 4, 30, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void Reads_both_windows_the_claim_and_the_overage_state()
    {
        var response = Response(
            ("anthropic-ratelimit-unified-5h-utilization", "1.0"),
            ("anthropic-ratelimit-unified-5h-status", "rejected"),
            ("anthropic-ratelimit-unified-5h-reset", "1794547200"),
            ("anthropic-ratelimit-unified-7d-utilization", "0.41"),
            ("anthropic-ratelimit-unified-7d-status", "allowed"),
            ("anthropic-ratelimit-unified-7d-reset", "1794816000"),
            ("anthropic-ratelimit-unified-representative-claim", "five_hour"),
            ("anthropic-ratelimit-unified-overage-status", "rejected"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        Assert.Equal(1.0, reading.FiveHourUtilization);
        Assert.Equal(RateLimitStatus.Rejected, reading.FiveHour);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794547200), reading.FiveHourResetAt);
        Assert.Equal(0.41, reading.WeeklyUtilization);
        Assert.Equal(RateLimitStatus.Allowed, reading.Weekly);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794816000), reading.WeeklyResetAt);
        Assert.Equal(RateLimitWindow.FiveHour, reading.Representative);
        Assert.Equal(RateLimitStatus.Rejected, reading.Overage);
        Assert.True(reading.IsRejected);
        Assert.Equal(RateLimitWindow.FiveHour, reading.BindingWindow);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794547200), reading.ResetAt);
    }

    [Fact]
    public void A_response_without_the_headers_reports_no_reading_rather_than_an_empty_account()
    {
        Assert.Null(AnthropicRateLimits.Read(Response(("request-id", "req_1")).Headers));
        Assert.Null(AnthropicRateLimits.Read(null));
    }

    [Fact]
    public void A_partial_reading_keeps_what_it_has_and_marks_the_rest_unknown()
    {
        var response = Response(("anthropic-ratelimit-unified-7d-utilization", "0.25"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        Assert.Null(reading.FiveHourUtilization);
        Assert.Equal(RateLimitStatus.Unknown, reading.FiveHour);
        Assert.Equal(0.25, reading.WeeklyUtilization);
        Assert.Null(reading.Representative);
        Assert.Equal(RateLimitStatus.Unknown, reading.Overage);
        Assert.False(reading.IsRejected);
        Assert.Null(reading.BindingWindow);
        Assert.Null(reading.ResetAt);
    }

    [Theory]
    [InlineData("not-a-number", "not-a-number", "surprising")]
    public void Unparseable_values_are_dropped_instead_of_guessed(string utilization, string reset, string status)
    {
        var response = Response(
            ("anthropic-ratelimit-unified-5h-utilization", utilization),
            ("anthropic-ratelimit-unified-5h-reset", reset),
            ("anthropic-ratelimit-unified-5h-status", status));

        var reading = AnthropicRateLimits.Read(response.Headers);

        // The claim and the overage state are absent, so a window with nothing readable leaves no reading at all.
        Assert.Null(reading);
    }

    [Fact]
    public void A_window_reported_only_by_its_status_keeps_that_status_and_no_numbers()
    {
        var response = Response(
            ("anthropic-ratelimit-unified-5h-utilization", "-1"),
            ("anthropic-ratelimit-unified-5h-reset", "0"),
            ("anthropic-ratelimit-unified-5h-status", "allowed_warning"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        Assert.Null(reading.FiveHourUtilization);
        Assert.Null(reading.FiveHourResetAt);
        Assert.Equal(RateLimitStatus.AllowedWarning, reading.FiveHour);
    }

    [Fact]
    public void An_absent_claim_falls_back_to_the_rejected_window()
    {
        var response = Response(
            ("anthropic-ratelimit-unified-5h-status", "allowed"),
            ("anthropic-ratelimit-unified-7d-status", "rejected"),
            ("anthropic-ratelimit-unified-7d-reset", "1794816000"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        Assert.Equal(RateLimitWindow.Weekly, reading.BindingWindow);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794816000), reading.ResetAt);
    }

    [Fact]
    public void The_console_rate_limits_are_read_as_their_own_thing()
    {
        var response = Response(
            ("anthropic-ratelimit-requests-limit", "50"),
            ("anthropic-ratelimit-requests-remaining", "43"),
            ("anthropic-ratelimit-requests-reset", "2026-11-13T05:28:00Z"),
            ("anthropic-ratelimit-tokens-limit", "40000"),
            ("anthropic-ratelimit-tokens-remaining", "12300"),
            ("anthropic-ratelimit-tokens-reset", "2026-11-13T05:28:00Z"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        // No spending window: this is an API key, and the reading must not pretend to be a subscription budget.
        Assert.False(reading.HasWindows);
        Assert.False(reading.IsRejected);
        Assert.Null(reading.ResetAt);
        var throttle = Assert.IsType<ProviderThrottle>(reading.Throttle);
        Assert.Equal(50, throttle.RequestLimit);
        Assert.Equal(43, throttle.RequestRemaining);
        Assert.Equal(DateTimeOffset.Parse("2026-11-13T05:28:00Z"), throttle.RequestResetAt);
        Assert.Equal(40_000, throttle.TokenLimit);
        Assert.Equal(12_300, throttle.TokenRemaining);
        Assert.True(throttle.HasRequests);
        Assert.True(throttle.HasTokens);
    }

    [Fact]
    public void A_console_reading_survives_a_missing_half_and_an_unreadable_reset()
    {
        var response = Response(
            ("anthropic-ratelimit-requests-remaining", "7"),
            ("anthropic-ratelimit-requests-reset", "not-a-timestamp"));

        var reading = AnthropicRateLimits.Read(response.Headers);

        Assert.NotNull(reading);
        var throttle = Assert.IsType<ProviderThrottle>(reading.Throttle);
        Assert.Equal(7, throttle.RequestRemaining);
        Assert.Null(throttle.RequestLimit);
        Assert.Null(throttle.RequestResetAt);
        Assert.False(throttle.HasTokens);
    }

    // ---------------------------------------------------------------- the reading on the wire

    [Fact]
    public async Task An_accepted_turn_carries_the_reading_on_the_completion()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var response = FakeHttpMessageHandler.SseResponse(string.Concat(
                TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":5,"output_tokens":0}}}"""),
                TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
                TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
                TestData.WithEvent("message_stop", """{"type":"message_stop"}""")));
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "0.62");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1794547200");
            return response;
        });

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());
        var events = await provider.CollectAsync(TestData.Request());

        var completed = events.Completed();
        Assert.NotNull(completed.RateLimit);
        Assert.Equal(0.62, completed.RateLimit.FiveHourUtilization);
    }

    [Fact]
    public async Task A_rejected_request_keeps_the_reading_on_the_exception()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    """{"type":"error","error":{"type":"rate_limit_error","message":"This request would exceed your account's rate limit. Please try again later."}}"""),
            };
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-utilization", "1.0");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "rejected");
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-reset", "1794547200");
            return response;
        });

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var failure = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.NotNull(failure.RateLimit);
        Assert.True(failure.RateLimit.IsRejected);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794547200), failure.RateLimit.ResetAt);
    }

    // ---------------------------------------------------------------- quota classification

    [Fact]
    public void A_rejected_window_is_a_spent_quota_even_without_a_retry_after_or_a_marker()
    {
        // The exact shape the account actually returned: no marker phrase, and the retry-after is not what proves it.
        var failure = new ProviderException(
            "anthropic returned 429 TooManyRequests: {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}",
            retryable: true,
            statusCode: 429)
        {
            RateLimit = new ProviderRateLimit(1.0, Now.AddMinutes(54), RateLimitStatus.Rejected, 0.41, Now.AddDays(3), RateLimitStatus.Allowed, RateLimitWindow.FiveHour, RateLimitStatus.Rejected),
        };

        var quota = ProviderQuota.Classify(failure, new ModelSettings("claude-opus-5"), Now);

        Assert.NotNull(quota);
        Assert.Equal(Now.AddMinutes(54), quota.ResetAt);
    }

    [Fact]
    public void A_429_that_reports_no_window_is_left_to_the_retry_layer()
    {
        var failure = new ProviderException("openai returned 429 TooManyRequests: slow down", retryable: true, statusCode: 429);

        Assert.Null(ProviderQuota.Classify(failure, new ModelSettings("gpt-x"), Now));
    }

    [Fact]
    public void A_window_whose_reset_has_passed_reports_no_reset_time()
    {
        var failure = new ProviderException("anthropic returned 429 TooManyRequests: busy", retryable: true, statusCode: 429)
        {
            RateLimit = new ProviderRateLimit(1.0, Now.AddMinutes(-5), RateLimitStatus.Rejected, 0.2, null, RateLimitStatus.Allowed, null, RateLimitStatus.Unknown),
        };

        var quota = ProviderQuota.Classify(failure, new ModelSettings("claude-opus-5"), Now);

        Assert.NotNull(quota);
        Assert.Null(quota.ResetAt);
    }

    private static HttpResponseMessage Response(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}
