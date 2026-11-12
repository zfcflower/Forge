using System.Net;
using System.Net.Http.Headers;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>Status/retry classification shared by the built-in providers.</summary>
public static class ProviderErrors
{
    /// <summary>Key under which a provider records the server's Retry-After delay on <see cref="ProviderException.Data"/>.</summary>
    public const string RetryAfterKey = "Forge.RetryAfter";

    /// <summary>Key of the flag a caller may set on <see cref="ProviderException.Data"/> to mark a context-overflow rejection.</summary>
    public const string ContextOverflowKey = "Forge.ContextOverflow";

    /// <summary>
    /// Phrases the built-in wire formats use when the prompt does not fit the model's window (Anthropic
    /// <c>prompt is too long</c>, OpenAI <c>context_length_exceeded</c>/<c>maximum context length</c>, and the variants
    /// other compatible servers send). Matched case-insensitively against the error text, which every adapter includes
    /// for both HTTP errors and in-stream error events.
    /// </summary>
    private static readonly string[] ContextOverflowPhrases =
    [
        "prompt is too long",
        "context_length_exceeded",
        "maximum context length",
        "context window exceeded",
        "context_window_exceeded",
        "exceeds the context window",
        "exceeds the maximum context",
        "input is too long",
        "too many input tokens",
        "reduce the length of the messages",
    ];

    public static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 429 || statusCode is >= 500 and <= 599;

    /// <summary>
    /// True when <paramref name="exception"/> is a provider rejecting the request because the prompt exceeds the model's
    /// context window, the case the agent loop answers with one forced compaction and one resend. Rate limits (429) and
    /// retryable failures never count, even when their text mentions tokens.
    /// </summary>
    public static bool IsContextOverflow(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ProviderException provider)
            {
                continue;
            }

            if (provider.Data[ContextOverflowKey] is true)
            {
                return true;
            }

            if (provider.Retryable || provider.StatusCode is 429)
            {
                return false;
            }

            if (provider.StatusCode is 413)
            {
                return true;
            }

            var message = provider.Message;
            foreach (var phrase in ContextOverflowPhrases)
            {
                if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

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
