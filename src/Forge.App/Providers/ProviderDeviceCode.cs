using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// The RFC 8628 device-code skeleton the form-encoded sign-ins share: one start step (a user code), one poll state
/// machine (pending / slow_down / expired / denied / unknown) and the refresh backoff loop. What a provider does
/// differently is a value — its name, its endpoints, its form fields, which error codes it spells as terminal, and
/// how it reads a completed answer — so each sign-in assembles a <see cref="DeviceCodePoll{T}"/> once, as cached
/// delegates and literals, and keeps its own public surface and record types.
/// </summary>
internal static class ProviderDeviceCode
{
    /// <summary>Polling faster than this would hammer the token endpoint; every reference client adds it to the waited interval.</summary>
    internal const int SlowDownIntervalIncrementSeconds = 5;

    /// <summary>A transient refresh answer (429 or a 5xx) is retried three times before the sign-in gives up.</summary>
    private const int MaxRefreshRetries = 3;

    /// <summary>The tail of a failed request's error message: what the server said, if it said anything.</summary>
    internal static string Describe(string body) => body.Length == 0 ? string.Empty : ": " + body;

    /// <summary>The form-encoded POST every shared step sends, with the JSON accept the answers carry.</summary>
    internal static HttpRequestMessage FormRequest(string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    /// <summary>
    /// Sends one authorization request and reads its body once; <see cref="ProviderSignInHttp"/> owns the timeout, the
    /// error mapping and the single body read, this class only names the provider and the step.
    /// </summary>
    internal static Task<(int Status, JsonDocument? Root, string Body)> SendForJsonAsync(
        HttpClient http,
        HttpRequestMessage message,
        string provider,
        string operation,
        CancellationToken ct) =>
        ProviderSignInHttp.SendForJsonAsync(http, message, provider, "authorization", operation, ct);

    /// <summary>A device-flow URL has to be an absolute http(s) address before the app offers to open it.</summary>
    internal static string? TrustedUrl(JsonElement element, string name) =>
        ProviderSignInJson.Text(element, name) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? text
            : null;

    /// <summary>
    /// A device-flow URL has to be an absolute https address before the app offers to open it: the user code is typed
    /// there, so it is never opened over plain http.
    /// </summary>
    internal static string? SecureUrl(JsonElement element, string name) =>
        ProviderSignInJson.Text(element, name) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? text
            : null;

    /// <summary>
    /// The start step: sends the request, refuses every non-2xx answer, and hands the parsed body to the provider
    /// together with the instant its answer is worth from.
    /// </summary>
    internal static async Task<TCode> StartAsync<TCode>(
        HttpClient http,
        TimeProvider time,
        string provider,
        HttpRequestMessage message,
        string operation,
        Func<JsonElement, DateTimeOffset, TCode> parse,
        CancellationToken ct)
    {
        var (status, root, body) = await SendForJsonAsync(http, message, provider, operation, ct).ConfigureAwait(false);
        using var document = root;

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                provider + ": the " + operation + " failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        return parse(document!.RootElement, time.GetUtcNow());
    }

    /// <summary>
    /// Polls once. A 2xx answer that the provider's <see cref="DeviceCodePoll{T}.Complete"/> delegate recognizes is
    /// completion (the delegate throws on an approval too incomplete to use), a 5xx is a retryable fault, and the
    /// documented error codes decide between waiting (pending, slow_down) and ending the login (expired, denied,
    /// unknown). The interval never drops below the floor and never shrinks by itself.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    internal static async Task<TPoll> PollAsync<TPoll>(
        HttpClient http,
        TimeProvider time,
        string provider,
        HttpRequestMessage message,
        int minimumIntervalSeconds,
        int intervalSeconds,
        DeviceCodePoll<TPoll> poll,
        CancellationToken ct)
    {
        var waited = Math.Max(minimumIntervalSeconds, intervalSeconds);

        var (status, root, body) = await SendForJsonAsync(http, message, provider, "device token", ct).ConfigureAwait(false);
        using var document = root;

        // A server-side fault is not an answer about this authorization: it fails the login instead of looking like "pending".
        if (status >= 500)
        {
            throw new ProviderException(
                provider + ": the device token request failed with HTTP " + status + Describe(body),
                retryable: true,
                status);
        }

        var element = document?.RootElement ?? default;
        if (status is >= 200 and < 300 && poll.Complete(element, time, waited) is { } complete)
        {
            return complete;
        }

        var error = ProviderSignInJson.Text(element, "error");
        if (error == "authorization_pending")
        {
            return poll.Pending(waited);
        }

        if (error == "slow_down")
        {
            // The server names the interval when it wants a specific one; otherwise the reference client adds five seconds.
            return poll.Pending(
                ProviderSignInJson.PositiveInt(element, "interval") is { } named
                    ? Math.Max(minimumIntervalSeconds, named)
                    : waited + SlowDownIntervalIncrementSeconds);
        }

        if (error == "expired_token")
        {
            throw new ProviderException(
                provider + ": the device authorization expired. Please restart the login.",
                retryable: false);
        }

        if (error is not null && Array.IndexOf(poll.DeniedCodes, error) >= 0)
        {
            throw new ProviderException(provider + ": the login was denied.", retryable: false);
        }

        if (error is null)
        {
            // No error code still has to say what the server said, or the failure reads as a bare status number.
            var unexplained = poll.NullDetail?.Invoke(element);
            throw new ProviderException(
                unexplained is null
                    ? provider + ": the device token request " + poll.NoAnswerPhrase + " (HTTP " + status + ")"
                    : provider + ": the device token request " + poll.NoAnswerWithDetailPhrase + " (HTTP " + status + "): " + unexplained,
                retryable: false,
                status);
        }

        var detail = poll.Detail(element);
        throw new ProviderException(
            detail is null
                ? $"{provider}: the device token request ended with '{error}' (HTTP {status})"
                : $"{provider}: the device token request ended with '{error}': {detail}",
            retryable: false,
            status);
    }

    /// <summary>
    /// Trades the refresh token for a new bundle, retrying a transient answer (429 or a 5xx) with a 1s/2s/4s backoff
    /// because the access token is already dead and a refresh that gives up early costs the user a full sign-in.
    /// </summary>
    /// <exception cref="ProviderException">
    /// Status 401 when the refresh token is refused — the user has to sign in again — and the failing status otherwise.
    /// </exception>
    internal static async Task<TBundle> RefreshAsync<TBundle>(
        HttpClient http,
        TimeProvider time,
        string provider,
        string url,
        KeyValuePair<string, string>[] fields,
        string refreshToken,
        Func<JsonElement, string, TimeProvider, TBundle> parseBundle,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxRefreshRetries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), time, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            using var message = FormRequest(url, fields);
            var (status, root, body) = await SendForJsonAsync(http, message, provider, "token refresh", ct).ConfigureAwait(false);
            using var document = root;

            if (status is >= 200 and < 300)
            {
                return parseBundle(document!.RootElement, refreshToken, time);
            }

            var element = document?.RootElement ?? default;

            // A refused refresh token is reported as 401 whatever the server called it, so the caller has one rule.
            if (status is 401 or 403 || ProviderSignInJson.Text(element, "error") == "invalid_grant")
            {
                throw new ProviderException(
                    provider + ": the token refresh was rejected (HTTP " + status + "). Please sign in again.",
                    retryable: false,
                    status is 401 or 403 ? status : 401);
            }

            if (status != 429 && status < 500)
            {
                throw new ProviderException(
                    provider + ": the token refresh failed with HTTP " + status + Describe(body),
                    retryable: false,
                    status);
            }

            if (attempt == MaxRefreshRetries)
            {
                throw new ProviderException(
                    provider + ": the token refresh failed with HTTP " + status + Describe(body),
                    retryable: true,
                    status);
            }
        }

        // Unreachable: the last attempt either returned a bundle or threw.
        throw new ProviderException(provider + ": the token refresh failed.", retryable: false);
    }
}

/// <summary>
/// How one provider's poll differs from the shared state machine: how a completed answer is read (null while the
/// user has not finished; an approval too incomplete to use throws), which error codes spell a refusal, and what the
/// body says about a failure. Every field is a literal or a delegate created once per provider, so polling itself
/// allocates nothing the flows did not allocate before.
/// </summary>
internal sealed record DeviceCodePoll<TPoll>(
    Func<JsonElement, TimeProvider, int, TPoll?> Complete,
    Func<int, TPoll> Pending,
    string[] DeniedCodes,
    Func<JsonElement, string?> Detail,
    Func<JsonElement, string?>? NullDetail,
    string NoAnswerPhrase,
    string NoAnswerWithDetailPhrase);
