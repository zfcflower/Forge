using System.Net.Http.Headers;

namespace Forge.Core.Providers;

/// <summary>
/// The header plumbing the provider layer shares: the user's per-provider request headers (a value may carry
/// <see cref="SessionPlaceholder"/>, filled with the session id per request — OpenCode Zen/Go want
/// <c>x-opencode-session: {session}</c>; without a session such a header is not sent), and the one rule for reading a response
/// header's first non-blank value.
/// </summary>
public static class ProviderHeaders
{
    public const string SessionPlaceholder = "{session}";

    /// <summary>The value to send, or <c>null</c> when it needs a session id and there is none.</summary>
    public static string? Expand(string value, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains(SessionPlaceholder, StringComparison.Ordinal))
        {
            return value;
        }

        return string.IsNullOrEmpty(sessionId) ? null : value.Replace(SessionPlaceholder, sessionId, StringComparison.Ordinal);
    }

    /// <summary>Sets each header on <paramref name="message"/>, replacing one of the same name (the configured value wins).</summary>
    public static void Apply(HttpRequestMessage message, IReadOnlyDictionary<string, string> headers, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var (name, value) in headers)
        {
            if (Expand(value, sessionId) is not { } expanded)
            {
                continue;
            }

            message.Headers.Remove(name);
            message.Headers.TryAddWithoutValidation(name, expanded);
        }
    }

    /// <summary>
    /// The first non-blank value of one response header, trimmed, or null. The rate-limit header families
    /// (<c>anthropic-ratelimit-*</c> and the generic <c>x-ratelimit-*</c>) both read their numbers this way, so the rule lives
    /// in one place: headers may repeat, these never do, and a blank value means the server said nothing rather than zero.
    /// </summary>
    public static string? ReadFirst(HttpResponseHeaders headers, string name)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(name);
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
