using System.Globalization;
using System.Net.Http.Headers;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Reads the minute-scale rate limits out of the generic <c>x-ratelimit-*</c> header family that OpenAI and most
/// OpenAI-compatible servers (xAI, Groq, Together, DeepSeek's compatible endpoints) send with every response, including the
/// ones that were accepted. Without it the local budget would only ever learn a limit from being rejected, while the server
/// had been telling it all along.
/// </summary>
/// <remarks>
/// <para>
/// The headers are <c>x-ratelimit-limit-requests</c> / <c>-remaining-requests</c> / <c>-reset-requests</c> and the same three
/// for <c>-tokens-</c>. They belong to <see cref="ProviderThrottle"/> rather than to a spending window: they describe a
/// minutes-long ceiling on an API key, not an account's hours-long budget, so a rejection against them is transient and the
/// retry layer owns it (the same split <see cref="AnthropicRateLimits"/> makes for <c>anthropic-ratelimit-*</c>).
/// </para>
/// <para>
/// Tolerant by design, like the Anthropic reader: a missing, non-numeric or malformed value is dropped, and a response that
/// carries none of the six yields null — a provider that reports nothing must not look like an account with an empty budget.
/// The <c>-reset-*</c> value is a duration in the OpenAI dialect (<c>1s</c>, <c>6m0s</c>, <c>1h2m3s</c>, <c>250ms</c>) and
/// sometimes a bare number of seconds; both are accepted, because guessing which one a gateway meant is how a client ends up
/// waiting an hour for a one-second window.
/// </para>
/// </remarks>
public static class RateLimitHeaders
{
    private const string LimitRequests = "x-ratelimit-limit-requests";
    private const string RemainingRequests = "x-ratelimit-remaining-requests";
    private const string ResetRequests = "x-ratelimit-reset-requests";
    private const string LimitTokens = "x-ratelimit-limit-tokens";
    private const string RemainingTokens = "x-ratelimit-remaining-tokens";
    private const string ResetTokens = "x-ratelimit-reset-tokens";

    /// <summary>The farthest a reset may lie in the future; beyond it the value is dropped as unreadable.</summary>
    private static readonly TimeSpan MaxReset = TimeSpan.FromDays(365);

    /// <summary>Bare numbers above this are unix timestamps, not second counts.</summary>
    private const double UnixEpochThreshold = 1e9;

    private const double MaxUnixSeconds = 253_402_300_799; // 9999-12-31, the last instant DateTimeOffset holds

    /// <summary>The limits <paramref name="headers"/> reported, or null when they carried none of them.</summary>
    /// <param name="headers">Response headers; may be null.</param>
    /// <param name="now">The instant the response was read, used to turn a relative reset into an absolute one.</param>
    public static ProviderThrottle? Read(HttpResponseHeaders? headers, DateTimeOffset now)
    {
        if (headers is null)
        {
            return null;
        }

        var requestLimit = ReadCount(headers, LimitRequests);
        var requestRemaining = ReadCount(headers, RemainingRequests);
        var requestReset = ReadReset(headers, ResetRequests, now);
        var tokenLimit = ReadCount(headers, LimitTokens);
        var tokenRemaining = ReadCount(headers, RemainingTokens);
        var tokenReset = ReadReset(headers, ResetTokens, now);
        if (requestLimit is null && requestRemaining is null && requestReset is null
            && tokenLimit is null && tokenRemaining is null && tokenReset is null)
        {
            return null;
        }

        return new ProviderThrottle(requestLimit, requestRemaining, requestReset, tokenLimit, tokenRemaining, tokenReset);
    }

    /// <summary>The same reading wrapped as a <see cref="ProviderRateLimit"/>, which is what a response carries.</summary>
    public static ProviderRateLimit? ReadRateLimit(HttpResponseHeaders? headers, DateTimeOffset now) =>
        Read(headers, now) is { } throttle
            ? new ProviderRateLimit(
                null,
                null,
                RateLimitStatus.Unknown,
                null,
                null,
                RateLimitStatus.Unknown,
                null,
                RateLimitStatus.Unknown)
            {
                Throttle = throttle,
            }
            : null;

    private static int? ReadCount(HttpResponseHeaders headers, string name) =>
        ProviderHeaders.ReadFirst(headers, name) is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;

    /// <summary>A relative duration (<c>6m0s</c>), a bare second count (<c>30</c>) or an RFC 3339 stamp.</summary>
    private static DateTimeOffset? ReadReset(HttpResponseHeaders headers, string name, DateTimeOffset now)
    {
        if (ProviderHeaders.ReadFirst(headers, name) is not { } text)
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            // A bare number is seconds from now, bounded like the duration form (a year): a hold must have an exit. A number
            // above a billion cannot be a wait (it would be 31 years) and is what a gateway sends when it means a unix
            // timestamp, so it is read as one.
            if (!double.IsFinite(seconds) || seconds < 0)
            {
                return null;
            }

            if (seconds > UnixEpochThreshold)
            {
                return seconds < MaxUnixSeconds
                    && DateTimeOffset.FromUnixTimeSeconds((long)seconds) is var epoch
                    && epoch > now
                    && epoch - now <= MaxReset
                        ? epoch
                        : null;
            }

            return seconds <= MaxReset.TotalSeconds ? now + TimeSpan.FromSeconds(seconds) : null;
        }

        if (TryParseDuration(text, out var duration))
        {
            return now + duration;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var stamp)
            && stamp > now
            && stamp - now <= MaxReset
            ? stamp
            : null;
    }

    /// <summary>
    /// Parses the <c>&lt;number&gt;&lt;unit&gt;</c> chain the header family uses (<c>1s</c>, <c>6m0s</c>, <c>1h2m3s</c>,
    /// <c>250ms</c>). A trailing unit or an unknown letter fails the whole value rather than being read as zero.
    /// </summary>
    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var total = TimeSpan.Zero;
        var index = 0;
        while (index < span.Length)
        {
            var start = index;
            while (index < span.Length && (char.IsAsciiDigit(span[index]) || span[index] == '.'))
            {
                index++;
            }

            if (index == start || !double.TryParse(span[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return false;
            }

            var unitStart = index;
            while (index < span.Length && char.IsAsciiLetter(span[index]))
            {
                index++;
            }

            var unit = span[unitStart..index];
            var part = unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => TimeSpan.MinValue,
            };
            if (part == TimeSpan.MinValue)
            {
                return false;
            }

            total += part;
        }

        if (total < TimeSpan.Zero || total > MaxReset)
        {
            return false;
        }

        duration = total;
        return true;
    }

}
