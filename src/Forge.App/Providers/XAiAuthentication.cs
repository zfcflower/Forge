using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// One pending xAI device-code authorization: what the dialog shows the user and what the next poll needs.
/// The device code is a bearer credential for the poll call, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record XAiDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"XAiDeviceCode(userCode={UserCode}, verificationUri={VerificationUriComplete}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// The xAI OAuth bundle: the token that goes on the wire, the refresh token that normally rotates on every refresh, and
/// when the access token stops working. Never printed with a token in it.
/// </summary>
internal sealed record XAiTokenBundle(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
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

    public override string ToString() => $"XAiTokenBundle(expiresAt={ExpiresAt:O})";
}

/// <summary>
/// One poll's answer: the bundle once the user approved, or a pending signal. <see cref="IntervalSeconds"/> is the wait the
/// caller must observe before the next poll — unchanged for <c>authorization_pending</c>, raised for <c>slow_down</c>.
/// </summary>
internal sealed record XAiPollResult(XAiTokenBundle? Bundle, int IntervalSeconds)
{
    public bool IsPending => Bundle is null;

    public static XAiPollResult Pending(int intervalSeconds) => new(null, intervalSeconds);

    public static XAiPollResult Complete(XAiTokenBundle bundle, int intervalSeconds) => new(bundle, intervalSeconds);
}

/// <summary>
/// xAI's OAuth device-code flow: no browser redirect, no client secret and nothing the user has to paste. It is three calls
/// — start (a user code), poll (until the user approved it in the browser) and refresh (the rotated token bundle) — all
/// form-encoded against the same OAuth host.
/// </summary>
/// <remarks>
/// The poll cadence is the server's to decide: the start response carries an interval and the poll can raise it with
/// <c>slow_down</c>, so the interval never drops below <see cref="MinimumIntervalSeconds"/> and never shrinks by itself.
/// <see cref="PollAsync"/> is a single request on purpose — waiting belongs to the caller, which can drive it from the
/// injected <see cref="TimeProvider"/> instead of a real clock.
/// </remarks>
internal sealed class XAiAuthentication
{
    /// <summary>The public OAuth client the xAI CLI authenticates as; the device flow needs no secret with it.</summary>
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";

    /// <summary>The scopes the reference client asks for: identity, a refresh token that survives the session, and API access.</summary>
    public const string Scope = "openid profile email offline_access grok-cli:access api:access";

    /// <summary>Tells the authorization server which client surface is asking; the reference client sends it verbatim.</summary>
    internal const string Referrer = "pi";

    /// <summary>Where the flow runs; the token endpoint is the same host under <see cref="TokenPath"/>.</summary>
    public const string OAuthHost = "https://auth.x.ai";

    internal const string DeviceCodePath = "/oauth2/device/code";

    internal const string TokenPath = "/oauth2/token";

    /// <summary>Values below this would hammer the token endpoint; it is also the reference client's own default interval.</summary>
    internal const int MinimumIntervalSeconds = 5;

    /// <summary>A refresh answer may leave <c>expires_in</c> out; an hour is the documented lifetime when it does.</summary>
    private const int DefaultRefreshLifetimeSeconds = 3600;

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <summary>How a finished poll answer is read, and which error spellings end the login; built once, so polling allocates nothing new.</summary>
    private static readonly DeviceCodePoll<XAiPollResult> PollProfile = new(
        Complete: static (element, time, waited) =>
            ProviderSignInJson.Text(element, "access_token") is { Length: > 0 }
                ? XAiPollResult.Complete(Bundle(time, element, "device", previousRefreshToken: null), waited)
                : null,
        Pending: static waited => XAiPollResult.Pending(waited),
        // The authorization server spells a refusal either way; both mean the user said no.
        DeniedCodes: ["access_denied", "authorization_denied"],
        Detail: static element => ProviderSignInJson.Text(element, "error_description"),
        NullDetail: null,
        NoAnswerPhrase: "returned no token and no error code",
        NoAnswerWithDetailPhrase: "returned no token");

    /// <summary>How a start answer becomes the pending authorization xAI's dialog shows.</summary>
    private static readonly Func<JsonElement, DateTimeOffset, XAiDeviceCode> ParseStart =
        static (element, now) =>
        {
            var deviceCode = ProviderSignInJson.Text(element, "device_code");
            var userCode = ProviderSignInJson.Text(element, "user_code");
            var verificationUri = ProviderDeviceCode.SecureUrl(element, "verification_uri");

            // Without a positive lifetime the code would count as already expired, so the answer is unusable.
            var expiresIn = ProviderSignInJson.PositiveInt(element, "expires_in");
            if (deviceCode is null || userCode is null || verificationUri is null || expiresIn is null)
            {
                throw new ProviderException(
                    "xAI: the device authorization response carried no device_code, user_code, https verification URL or expires_in.",
                    retryable: false);
            }

            // The complete URL is optional: without it the user can still type the code on the plain page.
            var verificationUriComplete = ProviderDeviceCode.SecureUrl(element, "verification_uri_complete") ?? verificationUri;

            return new XAiDeviceCode(
                deviceCode,
                userCode,
                verificationUri,
                verificationUriComplete,
                ProviderSignInJson.Interval(element, MinimumIntervalSeconds),
                now + TimeSpan.FromSeconds(expiresIn.Value));
        };

    /// <summary>How a refresh answer becomes the bundle that keeps or rotates the tokens.</summary>
    private static readonly Func<JsonElement, string, TimeProvider, XAiTokenBundle> ParseRefresh =
        static (element, refreshToken, time) => Bundle(time, element, "refresh", refreshToken);

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the code and token lifetimes and the refresh backoff; tests inject one instead of sleeping.</param>
    public XAiAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Asks xAI for a user code the user types into <see cref="XAiDeviceCode.VerificationUri"/>.</summary>
    public async Task<XAiDeviceCode> StartAsync(CancellationToken ct)
    {
        using var message = ProviderDeviceCode.FormRequest(
            OAuthHost + DeviceCodePath,
            [
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", Scope),
                new KeyValuePair<string, string>("referrer", Referrer),
            ]);
        return await ProviderDeviceCode.StartAsync(http, time, "xAI", message, "device authorization", ParseStart, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Polls once. Returns the pending signal while the user has not finished authorising and the token bundle once they
    /// have. A denial, an expired code and an unrecognised answer are terminal and throw; polling too fast raises the interval.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    public async Task<XAiPollResult> PollAsync(XAiDeviceCode code, int intervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var message = ProviderDeviceCode.FormRequest(
            OAuthHost + TokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("device_code", code.DeviceCode),
            ]);
        return await ProviderDeviceCode.PollAsync(http, time, "xAI", message, MinimumIntervalSeconds, intervalSeconds, PollProfile, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Trades the refresh token for a new bundle. The answer may omit the rotating refresh token, in which case the one that
    /// was just used stays valid and has to be kept. A transient answer (429 or a 5xx) is retried with a 1s/2s/4s backoff,
    /// because the access token is already dead and a refresh that gives up early costs the user a full sign-in.
    /// </summary>
    /// <exception cref="ProviderException">
    /// Status 401 when the refresh token is refused — the user has to sign in again — and the failing status otherwise.
    /// </exception>
    public async Task<XAiTokenBundle> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return await ProviderDeviceCode.RefreshAsync(
            http,
            time,
            "xAI",
            OAuthHost + TokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            ],
            refreshToken,
            ParseRefresh,
            ct).ConfigureAwait(false);
    }

    /// <summary>Waits one poll interval on the injected clock, so a test drives the wait instead of sleeping through it.</summary>
    internal Task WaitAsync(int intervalSeconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0, intervalSeconds)), time, ct);

    /// <summary>
    /// The bundle a token response carries. The access token alone is not enough: without a refresh token the next expiry
    /// would force a full sign-in, and without a positive lifetime the token would have to count as already dead. A refresh
    /// answer may omit the rotating token and the lifetime, which then come from what the caller already holds.
    /// </summary>
    private static XAiTokenBundle Bundle(TimeProvider time, JsonElement root, string operation, string? previousRefreshToken)
    {
        var access = ProviderSignInJson.Text(root, "access_token");
        var refresh = ProviderSignInJson.Text(root, "refresh_token") ?? previousRefreshToken;
        var expiresIn = ProviderSignInJson.PositiveInt(root, "expires_in")
                        ?? (previousRefreshToken is null ? null : DefaultRefreshLifetimeSeconds);
        if (access is null || refresh is null || expiresIn is null)
        {
            throw new ProviderException(
                $"xAI: the token {operation} response carried no access_token, refresh_token and expires_in.",
                retryable: false);
        }

        return new XAiTokenBundle(access, refresh, time.GetUtcNow() + TimeSpan.FromSeconds(expiresIn.Value));
    }
}
