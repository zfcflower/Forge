using System.Globalization;
using System.Net.Http.Headers;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// The generic <c>x-ratelimit-*</c> family (<see cref="RateLimitHeaders"/>), which is the only meter an OpenAI-compatible
/// provider sends on an accepted response. The reader is tolerant by contract, so both halves are pinned: what a complete
/// set becomes, and that a missing or malformed value is dropped rather than guessed.
/// </summary>
public class RateLimitHeadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private const string LimitRequests = "x-ratelimit-limit-requests";
    private const string RemainingRequests = "x-ratelimit-remaining-requests";
    private const string ResetRequests = "x-ratelimit-reset-requests";
    private const string LimitTokens = "x-ratelimit-limit-tokens";
    private const string RemainingTokens = "x-ratelimit-remaining-tokens";
    private const string ResetTokens = "x-ratelimit-reset-tokens";

    // ------------------------------------------------------------------ the whole reading

    [Fact]
    public void All_six_headers_become_one_throttle()
    {
        var headers = Headers(
            (LimitRequests, "50"),
            (RemainingRequests, "40"),
            (ResetRequests, "30s"),
            (LimitTokens, "100000"),
            (RemainingTokens, "90000"),
            (ResetTokens, "1m0s"));

        var throttle = RateLimitHeaders.Read(headers, Now);

        Assert.NotNull(throttle);
        Assert.Equal(50, throttle.RequestLimit);
        Assert.Equal(40, throttle.RequestRemaining);
        Assert.Equal(Now + TimeSpan.FromSeconds(30), throttle.RequestResetAt);
        Assert.Equal(100000, throttle.TokenLimit);
        Assert.Equal(90000, throttle.TokenRemaining);
        Assert.Equal(Now + TimeSpan.FromSeconds(60), throttle.TokenResetAt);
        Assert.True(throttle.HasRequests);
        Assert.True(throttle.HasTokens);
    }

    [Fact]
    public void A_partial_set_leaves_the_missing_fields_null()
    {
        var throttle = RateLimitHeaders.Read(Headers((LimitTokens, "100000"), (RemainingTokens, "90000")), Now);

        Assert.NotNull(throttle);
        Assert.Equal(100000, throttle.TokenLimit);
        Assert.Equal(90000, throttle.TokenRemaining);
        Assert.Null(throttle.TokenResetAt);
        Assert.Null(throttle.RequestLimit);
        Assert.Null(throttle.RequestRemaining);
        Assert.Null(throttle.RequestResetAt);
        Assert.False(throttle.HasRequests);
    }

    [Fact]
    public void A_response_with_none_of_them_reads_as_nothing()
    {
        Assert.Null(RateLimitHeaders.Read(Headers(), Now));
        Assert.Null(RateLimitHeaders.Read(headers: null, Now));
    }

    // ------------------------------------------------------------------ the reset dialects

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("6m0s", 360)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("250ms", 0.25)]
    [InlineData("45", 45)]
    [InlineData("0s", 0)]
    public void A_relative_reset_is_turned_into_an_absolute_moment(string value, double seconds)
    {
        var throttle = RateLimitHeaders.Read(Headers((ResetRequests, value), (ResetTokens, value)), Now);

        Assert.NotNull(throttle);
        Assert.Equal(Now + TimeSpan.FromSeconds(seconds), throttle.RequestResetAt);
        Assert.Equal(Now + TimeSpan.FromSeconds(seconds), throttle.TokenResetAt);
    }

    [Fact]
    public void An_RFC_3339_reset_is_taken_as_an_absolute_moment()
    {
        var stamp = Now + TimeSpan.FromSeconds(45);
        var throttle = RateLimitHeaders.Read(
            Headers((ResetRequests, stamp.ToString("O", CultureInfo.InvariantCulture))),
            Now);

        Assert.NotNull(throttle);
        Assert.Equal(stamp, throttle.RequestResetAt);
    }

    [Fact]
    public void A_reset_stamp_in_the_past_is_dropped()
    {
        var throttle = RateLimitHeaders.Read(
            Headers((ResetRequests, (Now - TimeSpan.FromMinutes(5)).ToString("O", CultureInfo.InvariantCulture))),
            Now);

        // The header was there but says nothing usable, so the reading keeps whatever else it had — nothing here.
        Assert.Null(throttle);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("6x")]
    [InlineData("1m2x")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("s")]
    public void A_malformed_reset_is_dropped_without_taking_the_rest_of_the_reading_with_it(string value)
    {
        Assert.Null(RateLimitHeaders.Read(Headers((ResetRequests, value)), Now));

        var throttle = RateLimitHeaders.Read(Headers((ResetRequests, value), (LimitRequests, "50")), Now);

        Assert.NotNull(throttle);
        Assert.Equal(50, throttle.RequestLimit);
        Assert.Null(throttle.RequestResetAt);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("50.5")]
    public void A_malformed_count_is_dropped(string value)
    {
        var throttle = RateLimitHeaders.Read(Headers((LimitRequests, value), (RemainingTokens, "40")), Now);

        Assert.NotNull(throttle);
        Assert.Null(throttle.RequestLimit);
        Assert.Equal(40, throttle.TokenRemaining);
    }

    [Fact]
    public void A_count_wrapped_in_spaces_is_still_read()
    {
        var throttle = RateLimitHeaders.Read(Headers((LimitRequests, " 50 "), (RemainingRequests, "40")), Now);

        Assert.NotNull(throttle);
        Assert.Equal(50, throttle.RequestLimit);
        Assert.Equal(40, throttle.RequestRemaining);
    }

    // ------------------------------------------------------------------ the duration dialect itself

    [Theory]
    [InlineData("1s", true, 1000)]
    [InlineData("6m0s", true, 360000)]
    [InlineData("1h2m3s", true, 3723000)]
    [InlineData("250ms", true, 250)]
    [InlineData("2d", true, 172800000)]
    [InlineData(" 2s", true, 2000)]
    [InlineData("500", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("6x", false, 0)]
    [InlineData("1m2x", false, 0)]
    [InlineData("400d", false, 0)]
    public void TryParseDuration_reads_the_documented_dialect_and_refuses_the_rest(string text, bool expected, double milliseconds)
    {
        var parsed = RateLimitHeaders.TryParseDuration(text, out var duration);

        Assert.Equal(expected, parsed);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), duration);
    }

    // ------------------------------------------------------------------ wrapping as a reading

    [Fact]
    public void ReadRateLimit_wraps_the_throttle_and_reports_no_spending_window()
    {
        var reading = RateLimitHeaders.ReadRateLimit(Headers((LimitRequests, "50"), (RemainingRequests, "40")), Now);

        Assert.NotNull(reading);
        Assert.False(reading.HasWindows);
        Assert.False(reading.IsRejected);
        Assert.Null(reading.ResetAt);
        Assert.Null(reading.FiveHourUtilization);
        Assert.Null(reading.WeeklyUtilization);
        Assert.Equal(RateLimitStatus.Unknown, reading.FiveHour);
        Assert.Equal(RateLimitStatus.Unknown, reading.Weekly);
        Assert.Equal(RateLimitStatus.Unknown, reading.Overage);
        Assert.NotNull(reading.Throttle);
        Assert.Equal(50, reading.Throttle.RequestLimit);
        Assert.Equal(40, reading.Throttle.RequestRemaining);
    }

    [Fact]
    public void ReadRateLimit_is_null_when_the_response_carried_nothing()
    {
        Assert.Null(RateLimitHeaders.ReadRateLimit(Headers(), Now));
        Assert.Null(RateLimitHeaders.ReadRateLimit(headers: null, Now));
    }

    private static HttpResponseHeaders Headers(params (string Name, string Value)[] values)
    {
        using var response = new HttpResponseMessage();
        foreach (var (name, value) in values)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response.Headers;
    }
}
