using System.Net;
using System.Net.Http.Headers;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>Status/retry classification shared by the built-in providers.</summary>
public static class ProviderErrors
{
    /// <summary>Key under which a provider records the server's Retry-After delay on <see cref="ProviderException.Data"/>.</summary>
    public const string RetryAfterKey = "Forge.RetryAfter";

    public static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 429 || statusCode is >= 500 and <= 599;

    internal static ProviderException FromStatus(
        string providerId,
        HttpStatusCode status,
        string body,
        RetryConditionHeaderValue? retryAfter)
    {
        var code = (int)status;
        var exception = new ProviderException(
            $"{providerId} returned {code} {status}: {body}",
            IsRetryableStatus(code),
            code);
        AttachRetryAfter(exception, retryAfter);
        return exception;
    }

    internal static void AttachRetryAfter(ProviderException exception, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            exception.Data[RetryAfterKey] = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            exception.Data[RetryAfterKey] = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    public static bool TryGetRetryAfter(ProviderException exception, out TimeSpan delay)
    {
        if (exception.Data[RetryAfterKey] is TimeSpan value)
        {
            delay = value;
            return true;
        }

        delay = default;
        return false;
    }
}
