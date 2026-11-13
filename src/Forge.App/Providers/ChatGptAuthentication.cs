using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// One pending ChatGPT device-code authorization: what the dialog shows the user and what the next poll needs.
/// The device-auth id is a bearer credential for the poll call, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record ChatGptDeviceCode(
    string DeviceAuthId,
    string UserCode,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"ChatGptDeviceCode(userCode={UserCode}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// One poll's answer: the authorization code and the PKCE verifier once the user approved, or a pending signal.
/// Both values are single-use credentials for the token exchange, so they stay out of <see cref="ToString"/>.
/// </summary>
internal sealed record ChatGptPollResult(string? AuthorizationCode, string? CodeVerifier, int IntervalSeconds)
{
    public bool IsPending => AuthorizationCode is null;

    public static ChatGptPollResult Pending(int intervalSeconds) => new(null, null, intervalSeconds);

    public static ChatGptPollResult Complete(string authorizationCode, string codeVerifier, int intervalSeconds) =>
        new(authorizationCode, codeVerifier, intervalSeconds);

    public override string ToString() =>
        IsPending
            ? $"ChatGptPollResult(pending, interval={IntervalSeconds})"
            : $"ChatGptPollResult(complete, interval={IntervalSeconds})";
}

/// <summary>
/// A signed-in ChatGPT plan: the access token that goes on the wire, the refresh token that renews it, the account the plan
/// is drawn from (which every request has to name) and when the access token stops working. Never printed with a token in it.
/// </summary>
internal sealed record ChatGptTokenBundle(
    string AccessToken,
    string RefreshToken,
    string AccountId,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Five minutes of slack: a turn that starts just before expiry would otherwise be rejected mid-stream, and a refresh
    /// inside the margin costs a round trip instead of the user's sign-in.
    /// </summary>
    public static readonly TimeSpan ValidityMargin = TimeSpan.FromMinutes(5);

    /// <summary>Whether a request started now is worth sending with this access token.</summary>
    public bool IsValidAt(DateTimeOffset now) =>
        // Added rather than subtracted: an unknown lifetime is stored as DateTimeOffset.MinValue, which would overflow.
        !string.IsNullOrEmpty(AccessToken) && now + ValidityMargin < ExpiresAt;

    public override string ToString() => $"ChatGptTokenBundle(account={AccountId}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// The ChatGPT plan sign-in, the way the Codex client does it: a device code on <c>auth.openai.com</c>, polled until the user
/// approves (403/404 is "not yet"), then one OAuth exchange for an access and refresh token. The account id lives inside the
/// access token as a JWT claim and every model request has to repeat it in a header, which is why it is read out once and
/// stored with the bundle.
/// </summary>
/// <remarks>
/// The poll cadence is the server's to decide: the user-code answer carries an interval and the poll can raise it with
/// <c>slow_down</c>, so the interval never drops below <see cref="MinimumIntervalSeconds"/> and never shrinks by itself.
/// <see cref="PollAsync"/> is a single request on purpose — waiting belongs to the caller, which can drive it from the
/// injected <see cref="TimeProvider"/> instead of a real clock.
/// </remarks>
internal sealed class ChatGptAuthentication
{
    /// <summary>The public client the Codex CLI authenticates as; the device flow needs no secret with it.</summary>
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>Where the device flow and the token endpoint run; both are under the same host.</summary>
    public const string AuthHost = "https://auth.openai.com";

    internal const string UserCodePath = "/api/accounts/deviceauth/usercode";

    internal const string DeviceTokenPath = "/api/accounts/deviceauth/token";

    internal const string TokenPath = "/oauth/token";

    /// <summary>The page that accepts the user code; fixed, because the user-code answer does not carry a URL.</summary>
    public const string VerificationUrl = "https://auth.openai.com/codex/device";

    /// <summary>The redirect the device flow pretends to use; the exchange is refused without it.</summary>
    internal const string RedirectUri = "https://auth.openai.com/deviceauth/callback";

    /// <summary>The error code the token poll answers with while nobody has approved yet.</summary>
    internal const string AuthorizationPendingCode = "deviceauth_authorization_pending";

    /// <summary>A device login that nobody approves is given up after fifteen minutes; the code is dead by then anyway.</summary>
    public const int DeadlineSeconds = 900;

    /// <summary>Values below this would hammer the token endpoint; it is also the reference client's own default interval.</summary>
    internal const int MinimumIntervalSeconds = 5;

    private const int SlowDownIntervalIncrementSeconds = 5;

    /// <summary>The claim that carries the ChatGPT account; every model request has to name it in <c>chatgpt-account-id</c>.</summary>
    internal const string AuthClaim = "https://api.openai.com/auth";

    internal const string AccountIdClaim = "chatgpt_account_id";

    /// <summary>The Responses endpoint behind a ChatGPT plan is experimental, and the server says so in a header.</summary>
    public const string OpenAiBetaValue = "responses=experimental";

    public const string AcceptValue = "text/event-stream";

    /// <summary>The user-code request body: fixed, so it is written once instead of serialized.</summary>
    private static readonly string UserCodeBody = "{\"client_id\":\"" + ClientId + "\"}";

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the code lifetime and the token expiry; tests inject one instead of sleeping.</param>
    public ChatGptAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Puts the four headers a ChatGPT request needs onto <paramref name="headers"/>: the bearer token, the account the plan
    /// is drawn from, the experimental Responses opt-in and the event-stream accept. Both the chat path and the listing path
    /// go through here so the two cannot drift apart.
    /// </summary>
    public static void ApplyHeaders(IDictionary<string, string> headers, string accessToken, string accountId)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers["Authorization"] = "Bearer " + accessToken;
        headers["chatgpt-account-id"] = accountId;
        headers["OpenAI-Beta"] = OpenAiBetaValue;
        headers["accept"] = AcceptValue;
    }

    /// <summary>Asks for a user code the user types on <see cref="VerificationUrl"/>.</summary>
    /// <exception cref="ProviderException">
    /// Status 404 when this server has no device login at all, which is a configuration problem and not a retry.
    /// </exception>
    public async Task<ChatGptDeviceCode> StartAsync(CancellationToken ct)
    {
        using var message = JsonRequest(UserCodePath, UserCodeBody);
        var (status, root, body) = await SendForJsonAsync(message, "device code", ct).ConfigureAwait(false);
        using var document = root;

        if (status == 404)
        {
            throw new ProviderException(
                "ChatGPT: device login is not enabled for this server (HTTP 404)" + Describe(body),
                retryable: false,
                status);
        }

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                "ChatGPT: the device code request failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        var element = document!.RootElement;
        var deviceAuthId = ProviderSignInJson.Text(element, "device_auth_id");
        var userCode = ProviderSignInJson.Text(element, "user_code");
        if (deviceAuthId is null || userCode is null)
        {
            throw new ProviderException(
                "ChatGPT: the device code response carried no device_auth_id and user_code.",
                retryable: false);
        }

        return new ChatGptDeviceCode(
            deviceAuthId,
            userCode,
            Interval(element, MinimumIntervalSeconds),
            time.GetUtcNow() + TimeSpan.FromSeconds(DeadlineSeconds));
    }

    /// <summary>
    /// Polls once. Returns the pending signal until the user approves, and the authorization code plus its PKCE verifier once
    /// they have. A <c>slow_down</c> raises the interval; anything else that is not a documented "not yet" is terminal.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    public async Task<ChatGptPollResult> PollAsync(ChatGptDeviceCode code, int intervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        var waited = Math.Max(MinimumIntervalSeconds, intervalSeconds);

        using var message = JsonRequest(
            DeviceTokenPath,
            "{\"device_auth_id\":\"" + JsonEncodedText.Encode(code.DeviceAuthId) +
            "\",\"user_code\":\"" + JsonEncodedText.Encode(code.UserCode) + "\"}");
        var (status, root, body) = await SendForJsonAsync(message, "device token", ct).ConfigureAwait(false);
        using var document = root;

        // The endpoint answers "not yet" with 403/404 as well as with a body code, so the status is read before the body:
        // a 404 here is the normal pending answer, not a missing route.
        if (status is 403 or 404)
        {
            return ChatGptPollResult.Pending(waited);
        }

        // A server-side fault is not an answer about this authorization: it fails the login instead of looking like "pending".
        if (status >= 500)
        {
            throw new ProviderException(
                "ChatGPT: the device token request failed with HTTP " + status + Describe(body),
                retryable: true,
                status);
        }

        var element = document?.RootElement ?? default;
        if (status is >= 200 and < 300)
        {
            var authorizationCode = ProviderSignInJson.Text(element, "authorization_code");
            var codeVerifier = ProviderSignInJson.Text(element, "code_verifier");
            if (authorizationCode is not null && codeVerifier is not null)
            {
                return ChatGptPollResult.Complete(authorizationCode, codeVerifier, waited);
            }

            // An approval without both halves cannot be exchanged, so it is a failure and not a silent "keep waiting".
            if (authorizationCode is not null || codeVerifier is not null)
            {
                throw new ProviderException(
                    "ChatGPT: the device token response carried no authorization_code and code_verifier.",
                    retryable: false,
                    status);
            }
        }

        switch (ErrorCode(element))
        {
            case AuthorizationPendingCode:
                return ChatGptPollResult.Pending(waited);

            case "slow_down":
                return ChatGptPollResult.Pending(
                    PositiveInt(element, "interval") is { } named
                        ? Math.Max(MinimumIntervalSeconds, named)
                        : waited + SlowDownIntervalIncrementSeconds);

            case null:
                // No error code still has to say what the server said, or the failure reads as a bare status number.
                var unexplained = Detail(element);
                throw new ProviderException(
                    unexplained is null
                        ? "ChatGPT: the device token request returned neither an authorization code nor an error code (HTTP " +
                          status + ")"
                        : "ChatGPT: the device token request returned no authorization code (HTTP " + status + "): " +
                          unexplained,
                    retryable: false,
                    status);

            case var error:
                var detail = Detail(element);
                throw new ProviderException(
                    detail is null
                        ? $"ChatGPT: the device token request ended with '{error}' (HTTP {status})"
                        : $"ChatGPT: the device token request ended with '{error}': {detail}",
                    retryable: false,
                    status);
        }
    }

    /// <summary>
    /// Exchanges the authorization code for the token bundle. The account id is read out of the access token here, because a
    /// token without it cannot address a plan and would only fail later as an unauthenticated turn.
    /// </summary>
    public async Task<ChatGptTokenBundle> ExchangeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        using var message = FormRequest(
            TokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("code", authorizationCode),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            ]);
        var (status, root, body) = await SendForJsonAsync(message, "token exchange", ct).ConfigureAwait(false);
        using var document = root;

        if (status is 400 or 401 or 403)
        {
            throw new ProviderException(
                "ChatGPT: the authorization code was refused (HTTP " + status + "). Please sign in again.",
                retryable: false,
                status);
        }

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                "ChatGPT: the token exchange failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        return Bundle(document!.RootElement, "exchange");
    }

    /// <summary>
    /// Trades the refresh token for a fresh bundle. The account id is read again from the new access token: the token that
    /// carries it is the one that must be named on every request, so a stale id would be sent with a live token.
    /// </summary>
    /// <exception cref="ProviderException">
    /// Status 401 when the refresh token is refused — the user has to sign in again — and the failing status otherwise.
    /// </exception>
    public async Task<ChatGptTokenBundle> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var message = FormRequest(
            TokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            ]);
        var (status, root, body) = await SendForJsonAsync(message, "token refresh", ct).ConfigureAwait(false);
        using var document = root;

        if (status is 400 or 401 or 403)
        {
            throw new ProviderException(
                "ChatGPT: the token refresh was rejected (HTTP " + status + "). Please sign in again.",
                retryable: false,
                status is 401 or 403 ? status : 401);
        }

        if (status is < 200 or >= 300)
        {
            throw new ProviderException(
                "ChatGPT: the token refresh failed with HTTP " + status + Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        return Bundle(document!.RootElement, "refresh");
    }

    /// <summary>Waits one poll interval on the injected clock, so a test drives the wait instead of sleeping through it.</summary>
    internal Task WaitAsync(int intervalSeconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0, intervalSeconds)), time, ct);

    /// <summary>
    /// The bundle a token answer carries. Access token, refresh token and lifetime are all required; the account id comes out
    /// of the access token's own claims.
    /// </summary>
    private ChatGptTokenBundle Bundle(JsonElement root, string operation)
    {
        var access = ProviderSignInJson.Text(root, "access_token");
        var refresh = ProviderSignInJson.Text(root, "refresh_token");
        var expiresIn = PositiveInt(root, "expires_in");
        if (access is null || refresh is null || expiresIn is null)
        {
            throw new ProviderException(
                $"ChatGPT: the token {operation} response carried no access_token, refresh_token and expires_in.",
                retryable: false);
        }

        var accountId = AccountId(access);
        if (accountId is null)
        {
            throw new ProviderException(
                "ChatGPT: the access token carries no " + AuthClaim + " -> " + AccountIdClaim +
                " claim, so requests cannot name the ChatGPT account. Please sign in again.",
                retryable: false);
        }

        return new ChatGptTokenBundle(access, refresh, accountId, time.GetUtcNow() + TimeSpan.FromSeconds(expiresIn.Value));
    }

    /// <summary>
    /// The ChatGPT account id inside an access token. The token is a JWT and the id lives under the OpenAI auth claim; a token
    /// that is not a readable JWT, or has no such claim, returns null and the caller refuses the login.
    /// </summary>
    internal static string? AccountId(string accessToken)
    {
        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(AuthClaim, out var auth)
                   && ProviderSignInJson.Text(auth, AccountIdClaim) is { Length: > 0 } accountId
                ? accountId
                : null;
        }
    }

    /// <summary>base64url to bytes: JWT segments replace <c>+</c> and <c>/</c> and drop the padding, which .NET rejects.</summary>
    private static byte[] DecodeBase64Url(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='));
    }

    private static HttpRequestMessage JsonRequest(string path, string body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, AuthHost + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    private static HttpRequestMessage FormRequest(string path, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, AuthHost + path)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return message;
    }

    /// <summary>
    /// Sends one authorization request and reads its body once; <see cref="ProviderSignInHttp" /> owns the timeout, the error
    /// mapping and the single body read, this class only names the provider and the step.
    /// </summary>
    private Task<(int Status, JsonDocument? Root, string Body)> SendForJsonAsync(
        HttpRequestMessage message,
        string operation,
        CancellationToken ct) =>
        ProviderSignInHttp.SendForJsonAsync(http, message, "ChatGPT", "authorization", operation, ct);

    /// <summary>The tail of a failed request's error message: what the server said, if it said anything.</summary>
    private static string Describe(string body) => body.Length == 0 ? string.Empty : ": " + body;

    /// <summary>
    /// The error code a poll answer carries. The endpoint spells it either as a bare <c>error</c> string or inside an
    /// object, so both shapes are read.
    /// </summary>
    private static string? ErrorCode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("error", out var error))
        {
            return null;
        }

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Object => ProviderSignInJson.Text(error, "code") ?? ProviderSignInJson.Text(error, "type"),
            _ => null,
        };
    }

    /// <summary>What the server said about a failure, from whichever field it filled.</summary>
    private static string? Detail(JsonElement element) =>
        ProviderSignInJson.Text(element, "error_description")
        ?? ProviderSignInJson.Text(element, "detail")
        ?? ProviderSignInJson.Text(element, "message")
        ?? (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
                ? ProviderSignInJson.Text(error, "message")
                : null);

    /// <summary>
    /// A positive whole number of seconds. The device-code answer sends <c>interval</c> as a string often enough that both
    /// shapes have to be read; waiting a fraction too long is harmless, polling early is not.
    /// </summary>
    private static int? PositiveInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out var parsed):
                number = parsed;
                break;
            case JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedText):
                number = parsedText;
                break;
            default:
                return null;
        }

        if (!double.IsFinite(number) || number <= 0)
        {
            return null;
        }

        return (int)Math.Min(Math.Ceiling(number), int.MaxValue);
    }

    /// <summary>The interval to wait, never below <paramref name="floor"/>.</summary>
    private static int Interval(JsonElement element, int floor) =>
        PositiveInt(element, "interval") is { } named ? Math.Max(floor, named) : floor;
}
