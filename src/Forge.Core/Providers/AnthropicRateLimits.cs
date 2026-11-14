using System.Globalization;
using System.Net.Http.Headers;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Reads the account spending windows out of an Anthropic response: the <c>anthropic-ratelimit-unified-*</c> headers that
/// carry the 5-hour and 7-day utilization, when each resets and whether the server would accept the next request.
/// </summary>
/// <remarks>
/// <para>
/// These headers are the only place a window is visible <em>before</em> it is spent — an exhausted window otherwise only
/// shows up as a rejected request, and its body ("This request would exceed your account's rate limit") names neither the
/// window nor the reset. They arrive on accepted responses too, so a session's meter can move without ever hitting a limit.
/// </para>
/// <para>
/// Tolerant by design: a missing or unparseable header yields null rather than an error, and a response carrying none of
/// them yields null — a provider that reports no meter must not look like an account with an empty one. The <c>reset</c>
/// values are unix seconds (measured: 1794547200); a value that is not a plausible second count is dropped instead of being
/// reinterpreted, because a wrong reset time would hold the automation flow until a time that never comes.
/// </para>
/// </remarks>
public static class AnthropicRateLimits
{
    private const string FiveHourUtilization = "anthropic-ratelimit-unified-5h-utilization";
    private const string FiveHourStatus = "anthropic-ratelimit-unified-5h-status";
    private const string FiveHourReset = "anthropic-ratelimit-unified-5h-reset";
    private const string WeeklyUtilization = "anthropic-ratelimit-unified-7d-utilization";
    private const string WeeklyStatus = "anthropic-ratelimit-unified-7d-status";
    private const string WeeklyReset = "anthropic-ratelimit-unified-7d-reset";
    private const string RepresentativeClaim = "anthropic-ratelimit-unified-representative-claim";
    private const string OverageStatus = "anthropic-ratelimit-unified-overage-status";

    // The console's own limits: a minute-scale ceiling for an API key, unrelated to the subscription windows above.
    private const string RequestLimit = "anthropic-ratelimit-requests-limit";
    private const string RequestRemaining = "anthropic-ratelimit-requests-remaining";
    private const string RequestReset = "anthropic-ratelimit-requests-reset";
    private const string TokenLimit = "anthropic-ratelimit-tokens-limit";
    private const string TokenRemaining = "anthropic-ratelimit-tokens-remaining";
    private const string TokenReset = "anthropic-ratelimit-tokens-reset";

    /// <summary>The windows of <paramref name="headers"/>, or null when it reported none of them.</summary>
    public static ProviderRateLimit? Read(HttpResponseHeaders? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var fiveHour = ReadWindow(headers, FiveHourUtilization, FiveHourStatus, FiveHourReset);
        var weekly = ReadWindow(headers, WeeklyUtilization, WeeklyStatus, WeeklyReset);
        var claim = ReadClaim(headers);
        var overage = ReadStatus(headers, OverageStatus);
        var throttle = ReadThrottle(headers);
        if (fiveHour is null && weekly is null && claim is null && overage == RateLimitStatus.Unknown && throttle is null)
        {
            return null;
        }

        return new ProviderRateLimit(
            fiveHour?.Utilization,
            fiveHour?.ResetAt,
            fiveHour?.Status ?? RateLimitStatus.Unknown,
            weekly?.Utilization,
            weekly?.ResetAt,
            weekly?.Status ?? RateLimitStatus.Unknown,
            claim,
            overage)
        {
            Throttle = throttle,
        };
    }

    /// <summary>
    /// The console rate limits, when reported. Their reset is an RFC 3339 timestamp (not the unix seconds the unified windows
    /// use), so both shapes are accepted rather than assuming one.
    /// </summary>
    private static ProviderThrottle? ReadThrottle(HttpResponseHeaders headers)
    {
        var requestLimit = ReadCount(headers, RequestLimit);
        var requestRemaining = ReadCount(headers, RequestRemaining);
        var requestReset = ReadTimestamp(headers, RequestReset);
        var tokenLimit = ReadCount(headers, TokenLimit);
        var tokenRemaining = ReadCount(headers, TokenRemaining);
        var tokenReset = ReadTimestamp(headers, TokenReset);
        if (requestLimit is null && requestRemaining is null && requestReset is null
            && tokenLimit is null && tokenRemaining is null && tokenReset is null)
        {
            return null;
        }

        return new ProviderThrottle(requestLimit, requestRemaining, requestReset, tokenLimit, tokenRemaining, tokenReset);
    }

    private static int? ReadCount(HttpResponseHeaders headers, string name) =>
        ProviderHeaders.ReadFirst(headers, name) is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;

    /// <summary>Either an RFC 3339 timestamp or unix seconds: the two header families disagree and neither is worth guessing at.</summary>
    private static DateTimeOffset? ReadTimestamp(HttpResponseHeaders headers, string name)
    {
        if (ProviderHeaders.ReadFirst(headers, name) is not { } text)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var stamp)
            ? stamp
            : null;
    }

    /// <summary>One window's numbers; null when the response mentioned none of them.</summary>
    private sealed record Window(double? Utilization, DateTimeOffset? ResetAt, RateLimitStatus Status);

    private static Window? ReadWindow(HttpResponseHeaders headers, string utilization, string status, string reset)
    {
        var used = ReadFraction(headers, utilization);
        var until = ReadReset(headers, reset);
        var state = ReadStatus(headers, status);
        return used is null && until is null && state == RateLimitStatus.Unknown
            ? null
            : new Window(used, until, state);
    }

    /// <summary>A 0-1 share; the server sends a fraction (0.41), not a percentage. A negative value is not a share at all.</summary>
    private static double? ReadFraction(HttpResponseHeaders headers, string name) =>
        ProviderHeaders.ReadFirst(headers, name) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
        && value >= 0
            ? value
            : null;

    private static DateTimeOffset? ReadReset(HttpResponseHeaders headers, string name) =>
        ProviderHeaders.ReadFirst(headers, name) is { } text
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static RateLimitStatus ReadStatus(HttpResponseHeaders headers, string name) =>
        ProviderHeaders.ReadFirst(headers, name)?.ToLowerInvariant() switch
        {
            "allowed" => RateLimitStatus.Allowed,
            "allowed_warning" => RateLimitStatus.AllowedWarning,
            "rejected" => RateLimitStatus.Rejected,
            _ => RateLimitStatus.Unknown,
        };

    private static RateLimitWindow? ReadClaim(HttpResponseHeaders headers) =>
        ProviderHeaders.ReadFirst(headers, RepresentativeClaim)?.ToLowerInvariant() switch
        {
            "five_hour" => RateLimitWindow.FiveHour,
            "seven_day" => RateLimitWindow.Weekly,
            _ => null,
        };

}
