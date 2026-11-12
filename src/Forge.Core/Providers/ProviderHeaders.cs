namespace Forge.Core.Providers;

/// <summary>
/// The user's per-provider request headers. A value may carry <see cref="SessionPlaceholder"/>, filled with the session id per
/// request (OpenCode Zen/Go want <c>x-opencode-session: {session}</c>); without a session such a header is not sent.
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
}
