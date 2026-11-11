using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Tells a spent quota (hours/days until it resets; the automation flow waits for it) apart from a short rate
/// limit (seconds; the retry layer handles it) and from everything else. Conservative: an unknown error is never quota, and
/// loose wording ("quota exceeded", "usage limit") does not count when the message describes a per-minute rate.
/// </summary>
public static partial class ProviderQuota
{
    /// <summary>A Retry-After beyond this is a quota window, not a rate-limit blip.</summary>
    public static readonly TimeSpan LongRetryAfter = TimeSpan.FromMinutes(5);

    private const int MaxMessageLength = 500;

    // Substrings that name a spent quota or balance unambiguously (an error code or a fixed sentence). One source per line.
    private static readonly string[] DefiniteMarkers =
    [
        "insufficient_quota",                 // OpenAI: error.code on 429
        "exceeded your current quota",        // OpenAI: error.message
        "exceeded_current_quota",             // Kimi / Moonshot: exceeded_current_quota_error
        "usage_limit_reached",                // Codex / ChatGPT
        "usage limit reached",                // Claude subscription ("Claude AI usage limit reached", sent as rate_limit_error)
        "billing_hard_limit_reached",         // OpenAI: organization hard limit
        "credit balance is too low",          // Anthropic: 400 invalid_request_error
        "余额不足",                            // GLM / Zhipu, DeepSeek-CN: insufficient balance
    ];

    // Wording that usually means a spent quota but also appears in per-minute rate limits ("Quota exceeded for metric:
    // ..._per_minute"); it counts only when the message does not describe a rate (see RateWordingRegex).
    private static readonly string[] WordedMarkers =
    [
        "quota exceeded",                     // Gemini / generic OpenAI-compatible
        "usage limit",                        // Claude subscription / Codex: "usage limit reached"
        "使用上限",                            // GLM / Zhipu: usage cap reached
    ];

    /// <summary>Returns the quota failure described by <paramref name="exception"/>, or null when it is not one.</summary>
    public static ProviderQuotaExhausted? Classify(ProviderException exception, ModelSettings model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(model);

        var status = exception.StatusCode;
        var message = exception.Message ?? string.Empty;
        var hasRetryAfter = ProviderErrors.TryGetRetryAfter(exception, out var retryAfter) && retryAfter > TimeSpan.Zero;

        var quota = status switch
        {
            402 => true,
            429 => HasMarker(message) || (hasRetryAfter && retryAfter > LongRetryAfter),
            400 or 403 => HasMarker(message),
            _ => false,
        };
        if (!quota)
        {
            return null;
        }

        DateTimeOffset? resetAt = hasRetryAfter ? now + retryAfter : ParseReset(message, now);
        if (resetAt is { } at && at <= now)
        {
            resetAt = null;
        }

        return new ProviderQuotaExhausted(model.ProviderId, model.Model, resetAt, Trim(message));
    }

    private static bool HasMarker(string message)
    {
        foreach (var marker in DefiniteMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // GLM / Zhipu business codes: 1113 insufficient balance, 1308 usage limit, 1310 weekly/monthly cap.
        if (ZhipuCodeRegex().IsMatch(message))
        {
            return true;
        }

        // A short rate limit ("Rate limit reached ... tokens per min", "..._per_minute", 请求过于频繁) is the retry layer's.
        if (RateWordingRegex().IsMatch(message))
        {
            return false;
        }

        foreach (var marker in WordedMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return PeriodLimitRegex().IsMatch(message) || ChineseQuotaRegex().IsMatch(message);
    }

    private static string Trim(string message)
    {
        var text = message.Trim();
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength];
    }

    private static DateTimeOffset? ParseReset(string message, DateTimeOffset now)
    {
        var iso = IsoResetRegex().Match(message);
        if (iso.Success && DateTimeOffset.TryParse(
                iso.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var isoTime))
        {
            return isoTime;
        }

        var local = LocalTimeRegex().Match(message);
        if (local.Success && DateTime.TryParseExact(
                local.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var localTime))
        {
            return new DateTimeOffset(localTime);
        }

        var unix = UnixResetRegex().Match(message);
        if (unix.Success && long.TryParse(unix.Groups[1].Value, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                return unix.Groups[1].Length == 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                    : DateTimeOffset.FromUnixTimeSeconds(number);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        var relative = RelativeRegex().Match(message);
        if (relative.Success)
        {
            var total = TimeSpan.Zero;
            foreach (Match part in RelativePartRegex().Matches(relative.Groups[1].Value))
            {
                if (!double.TryParse(part.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    continue;
                }

                var unit = char.ToLowerInvariant(part.Groups[2].Value[0]);
                total += unit switch
                {
                    'd' => TimeSpan.FromDays(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    _ => TimeSpan.FromSeconds(amount),
                };
            }

            if (total > TimeSpan.Zero)
            {
                return now + total;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?:code|错误码)\W{0,4}(?:1113|1308|1310)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZhipuCodeRegex();

    [GeneratedRegex(@"rate[ _-]?limit|per[ _-]?(?:minute|min|second|sec)\b|\b(?:rpm|tpm|rps)\b|请求过于频繁|频率|速率|并发", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateWordingRegex();

    // "monthly limit reached" (Copilot), "daily usage limit exceeded", "plan limit reached".
    [GeneratedRegex(@"\b(?:daily|weekly|monthly|hourly|plan|subscription)\s+(?:usage\s+|request\s+|token\s+)?limit\s+(?:reached|exceeded|hit)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PeriodLimitRegex();

    // 额度 only together with a word saying it is spent: 额度已用尽 / 额度不足 / 超出额度 / 额度用完.
    [GeneratedRegex(@"额度\s*(?:已)?(?:用尽|用完|耗尽|不足|超限)|(?:超出|超过|已达|达到)\s*(?:\S{0,6})额度", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseQuotaRegex();

    [GeneratedRegex(@"(?:reset|resets|重置)\D{0,60}?(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetRegex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex LocalTimeRegex();

    [GeneratedRegex(@"(?:reset|resets|重置)\D{0,30}?(\d{13}|\d{10})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnixResetRegex();

    [GeneratedRegex(@"\b(?:in|after)\s+((?:\d+(?:\.\d+)?\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b[\s,]*(?:and\s+)?)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativePartRegex();
}
