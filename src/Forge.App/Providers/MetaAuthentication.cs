using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// One pending Meta device-code authorization: what the dialog shows the user and what the next poll needs.
/// The device code is a bearer credential for the poll call, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record MetaDeviceCode(
    string DeviceCode,
    string UserCode,
    string? VerificationUri,
    string? VerificationUriComplete,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"MetaDeviceCode(userCode={UserCode}, verificationUri={VerificationUriComplete ?? VerificationUri}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// What a Meta sign-in leaves behind: the minted API key that goes on the wire from then on, the identity token that can
/// mint a new one without a fresh login, and when the key stops being worth using. Never printed with either token in it.
/// </summary>
internal sealed record MetaCredentialBundle(string ApiKey, string IdentityToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Five minutes of slack: a request that starts just before expiry would otherwise be rejected mid-stream, and a mint
    /// inside the margin costs one round trip instead of the user's sign-in.
    /// </summary>
    public static readonly TimeSpan ValidityMargin = TimeSpan.FromMinutes(5);

    /// <summary>Whether a request started now is worth sending with this key.</summary>
    public bool IsValidAt(DateTimeOffset now) =>
        // Added rather than subtracted: an unknown lifetime is stored as DateTimeOffset.MinValue, which would overflow.
        !string.IsNullOrEmpty(ApiKey) && now + ValidityMargin < ExpiresAt;

    public override string ToString() => $"MetaCredentialBundle(expiresAt={ExpiresAt:O})";
}

/// <summary>
/// One poll's answer: the identity token once the user approved, or a pending signal. <see cref="IntervalSeconds"/> is the
/// wait the caller must observe before the next poll — unchanged for <c>authorization_pending</c>, raised for
/// <c>slow_down</c>. The identity token is a credential, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record MetaPollResult(string? IdentityToken, int IntervalSeconds)
{
    public bool IsPending => IdentityToken is null;

    public static MetaPollResult Pending(int intervalSeconds) => new(null, intervalSeconds);

    public static MetaPollResult Complete(string identityToken, int intervalSeconds) => new(identityToken, intervalSeconds);

    public override string ToString() =>
        IsPending ? $"MetaPollResult(pending, interval={IntervalSeconds})" : $"MetaPollResult(complete, interval={IntervalSeconds})";
}

/// <summary>
/// Meta's subscription sign-in: the device flow against <c>auth.meta.com</c> yields an <em>identity</em> token, which is then
/// exchanged at <c>api.meta.ai/muse-code/key</c> for the API key that actually authenticates the model requests. The key
/// lives twenty-four hours; the identity token is kept beside it so a later expiry costs a mint, not a new login.
/// </summary>
/// <remarks>
/// The poll cadence is the server's to decide: the start response carries an interval and the poll can raise it with
/// <c>slow_down</c>, so the interval never drops below <see cref="MinimumIntervalSeconds"/> and never shrinks by itself.
/// <see cref="PollAsync"/> is a single request on purpose — waiting belongs to the caller, which can drive it from the
/// injected <see cref="TimeProvider"/> instead of a real clock.
/// </remarks>
internal sealed class MetaAuthentication
{
    /// <summary>The public client the Meta Muse Code flow authenticates as; the device flow needs no secret with it.</summary>
    public const string ClientId = "1031625952748946";

    /// <summary>The OAuth host: device authorization and the token poll are both under it.</summary>
    public const string AuthorizationHost = "https://auth.meta.com";

    internal const string DeviceAuthorizationPath = "/oidc/device/authorization/";

    internal const string DeviceTokenPath = "/oidc/device/token/";

    /// <summary>The API host that mints a key for an identity token; it is not the host the models are served from.</summary>
    public const string KeyHost = "https://api.meta.ai";

    internal const string KeyPath = "/muse-code/key";

    /// <summary>Version of the muse-code API the mint call speaks; the reference client sends it verbatim.</summary>
    internal const string ApiVersion = "1.0.0";

    /// <summary>Values below this would hammer the token endpoint; it is also the reference client's own default interval.</summary>
    internal const int MinimumIntervalSeconds = 5;

    /// <summary>How long a minted key is treated as usable; the reference client's keys live a day.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    /// <summary>A start answer that leaves <c>expires_in</c> out still needs a deadline for the poll loop.</summary>
    private const int DefaultExpiresInSeconds = 900;

    private const int SlowDownIntervalIncrementSeconds = 5;

    private const int MaxErrorBodyLength = 1024;

    /// <summary>Each step stands alone, so a stalled connection must not hold the dialog for the whole code lifetime.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the code lifetime and the key expiry; tests inject one instead of sleeping.</param>
    public MetaAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Asks Meta for a user code the user types on the verification page.</summary>
    public async Task<MetaDeviceCode> StartAsync(CancellationToken ct)
    {
        using var message = FormRequest(
            DeviceAuthorizationPath,
            [new KeyValuePair<string, string>("client_id", ClientId)]);
        var (status, root, body) = await SendForJsonAsync(message, "device authorization", ct).ConfigureAwait(false);
        using var document = root;

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                "Meta: the device authorization failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        var element = document!.RootElement;
        var deviceCode = Text(element, "device_code");
        var userCode = Text(element, "user_code");
        if (deviceCode is null || userCode is null)
        {
            throw new ProviderException(
                "Meta: the device authorization response carried no device_code and user_code.",
                retryable: false);
        }

        // Both URLs are optional and the complete one is preferred: it carries the code, so the user confirms instead of
        // typing it. A plain http page is never offered, because the code is typed there.
        var verificationUri = SecureUrl(element, "verification_uri");
        var verificationUriComplete = SecureUrl(element, "verification_uri_complete") ?? verificationUri;

        return new MetaDeviceCode(
            deviceCode,
            userCode,
            verificationUri,
            verificationUriComplete,
            Interval(element, MinimumIntervalSeconds),
            time.GetUtcNow() + TimeSpan.FromSeconds(PositiveInt(element, "expires_in") ?? DefaultExpiresInSeconds));
    }

    /// <summary>
    /// Polls once. Returns the pending signal while the user has not finished authorising and the identity token once they
    /// have. A denial, an expired code and an unrecognised answer are terminal and throw; polling too fast raises the interval.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    public async Task<MetaPollResult> PollAsync(MetaDeviceCode code, int intervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        var waited = Math.Max(MinimumIntervalSeconds, intervalSeconds);

        using var message = FormRequest(
            DeviceTokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new KeyValuePair<string, string>("device_code", code.DeviceCode),
                new KeyValuePair<string, string>("client_id", ClientId),
            ]);
        var (status, root, body) = await SendForJsonAsync(message, "device token", ct).ConfigureAwait(false);
        using var document = root;

        // A server-side fault is not an answer about this authorization: it fails the login instead of looking like "pending".
        if (status >= 500)
        {
            throw new ProviderException(
                "Meta: the device token request failed with HTTP " + status + Describe(body),
                retryable: true,
                status);
        }

        var element = document?.RootElement ?? default;
        if (status is >= 200 and < 300 && Text(element, "access_token") is { Length: > 0 } identityToken)
        {
            // This is the identity token, not the API credential: the caller still has to mint a key with it.
            return MetaPollResult.Complete(identityToken, waited);
        }

        switch (Text(element, "error"))
        {
            case "authorization_pending":
                return MetaPollResult.Pending(waited);

            case "slow_down":
                // The server names the interval when it wants a specific one; otherwise five seconds are added.
                return MetaPollResult.Pending(
                    PositiveInt(element, "interval") is { } named
                        ? Math.Max(MinimumIntervalSeconds, named)
                        : waited + SlowDownIntervalIncrementSeconds);

            case "expired_token":
                throw new ProviderException(
                    "Meta: the device authorization expired. Please restart the login.",
                    retryable: false);

            case "access_denied":
            case "authorization_denied":
                throw new ProviderException("Meta: the login was denied.", retryable: false);

            case null:
                // No error code still has to say what the server said, or the failure reads as a bare status number.
                var unexplained = Detail(element);
                throw new ProviderException(
                    unexplained is null
                        ? "Meta: the device token request returned no identity token and no error code (HTTP " + status + ")"
                        : "Meta: the device token request returned no identity token (HTTP " + status + "): " + unexplained,
                    retryable: false,
                    status);

            case var error:
                var detail = Detail(element);
                throw new ProviderException(
                    detail is null
                        ? $"Meta: the device token request ended with '{error}' (HTTP {status})"
                        : $"Meta: the device token request ended with '{error}': {detail}",
                    retryable: false,
                    status);
        }
    }

    /// <summary>
    /// Trades an identity token for the API key the model requests use. The key is what goes on the wire from then on; the
    /// identity token stays in the bundle so a later expiry re-mints instead of asking the user to sign in again.
    /// </summary>
    /// <exception cref="ProviderException">
    /// Status 401 when the identity token is refused — the session is over and the user has to sign in again — and the
    /// failing status otherwise.
    /// </exception>
    public async Task<MetaCredentialBundle> MintAsync(string identityToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityToken);

        using var message = new HttpRequestMessage(HttpMethod.Post, KeyHost + KeyPath)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + identityToken);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("x-api-version", ApiVersion);

        var (status, root, body) = await SendForJsonAsync(message, "key mint", ct).ConfigureAwait(false);
        using var document = root;

        // A refused identity token means the session is gone: the bundle is worthless and the user must sign in again.
        if (status is 401 or 403)
        {
            throw new ProviderException(
                "Meta: the Meta session expired (HTTP " + status + "). Please sign in again.",
                retryable: false,
                status);
        }

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                "Meta: enabling API access failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        var element = document!.RootElement;
        if (Text(element, "api_key") is { Length: > 0 } apiKey)
        {
            return new MetaCredentialBundle(apiKey, identityToken, time.GetUtcNow() + KeyLifetime);
        }

        // The account is not set up yet: Meta answers with the page that finishes the job, and that page is the only useful
        // thing to tell the user — without it the failure reads like a broken server.
        var actionUrl = Text(element, "action_url");
        throw new ProviderException(
            actionUrl is null
                ? "Meta: the key response carried no api_key."
                : "Meta: access has not been enabled for this account yet. Finish the setup at " + actionUrl +
                  ", then sign in again.",
            retryable: false,
            status);
    }

    /// <summary>Waits one poll interval on the injected clock, so a test drives the wait instead of sleeping through it.</summary>
    internal Task WaitAsync(int intervalSeconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0, intervalSeconds)), time, ct);

    private static HttpRequestMessage FormRequest(string path, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, AuthorizationHost + path)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    /// <summary>
    /// Sends one request and reads its body once. The body comes back as text plus a parsed document (null when it is not
    /// JSON), because a failure still has to report the status and what the server said, JSON or not.
    /// </summary>
    private async Task<(int Status, JsonDocument? Root, string Body)> SendForJsonAsync(
        HttpRequestMessage message,
        string operation,
        CancellationToken ct)
    {
        // The shared client has no timeout of its own: each step gets its own, so a stalled connection cannot outlive the
        // device-code lifetime.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("Meta: the authorization request timed out.", retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProviderException($"Meta: the authorization request failed: {ex.Message}", retryable: true, inner: ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new ProviderException(
                    $"Meta: the {operation} response could not be read.",
                    retryable: true,
                    status,
                    ex);
            }

            if (status is >= 200 and < 300)
            {
                try
                {
                    return (status, JsonDocument.Parse(body), string.Empty);
                }
                catch (JsonException ex)
                {
                    // A mint answer of "{}" is valid JSON; an empty body is not, and the login needs an api_key, so this is
                    // reported as the unusable answer it is rather than as a transport problem.
                    throw new ProviderException(
                        $"Meta: the {operation} response is not JSON.",
                        retryable: false,
                        status,
                        ex);
                }
            }

            // A failure body is only shown to explain a failure; a successful one is parsed and never echoed.
            return (status, TryParse(body), body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength]);
        }
    }

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The tail of a failed request's error message: what the server said, if it said anything.</summary>
    private static string Describe(string body) => body.Length == 0 ? string.Empty : ": " + body;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// What the server said about a failure. Meta's error shape is not one name: depending on the step it fills
    /// <c>error_description</c>, <c>detail</c>, <c>message</c> or only <c>error</c>, so the first one present is the sentence.
    /// </summary>
    private static string? Detail(JsonElement element) =>
        Text(element, "error_description")
        ?? Text(element, "detail")
        ?? Text(element, "message")
        ?? Text(element, "error");

    /// <summary>
    /// A verification URL has to be an absolute https address before the app offers to open it: the user code is typed
    /// there, so it is never opened over plain http.
    /// </summary>
    private static string? SecureUrl(JsonElement element, string name) =>
        Text(element, name) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? text
            : null;

    /// <summary>A positive whole number of seconds, from a JSON number that may still arrive as a fraction.</summary>
    private static int? PositiveInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number <= 0)
        {
            return null;
        }

        // Rounded up: waiting a fraction of a second too long is harmless, polling early is not.
        return (int)Math.Min(Math.Ceiling(number), int.MaxValue);
    }

    /// <summary>The interval to wait, never below <paramref name="floor"/>.</summary>
    private static int Interval(JsonElement element, int floor) =>
        PositiveInt(element, "interval") is { } named ? Math.Max(floor, named) : floor;
}
