using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// One pending Kimi For Coding device-code authorization: what the dialog shows the user and what the next poll needs.
/// The device code is a bearer credential for the poll call, so it stays out of <see cref="ToString"/>.
/// </summary>
internal sealed record KimiDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"KimiDeviceCode(userCode={UserCode}, verificationUri={VerificationUriComplete}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// The Kimi For Coding OAuth bundle: the token that goes on the wire, the refresh token that rotates on every refresh, and
/// when the access token stops working. Never printed with a token in it.
/// </summary>
internal sealed record KimiTokenBundle(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>A minute of slack: a request that starts just before expiry would otherwise be rejected mid-stream.</summary>
    private static readonly TimeSpan ValidityMargin = TimeSpan.FromSeconds(60);

    /// <summary>Whether a request started now is worth sending with this access token.</summary>
    public bool IsValidAt(DateTimeOffset now) =>
        // Added rather than subtracted: an unknown lifetime is stored as DateTimeOffset.MinValue, which would overflow.
        !string.IsNullOrEmpty(AccessToken) && now + ValidityMargin < ExpiresAt;

    public override string ToString() => $"KimiTokenBundle(expiresAt={ExpiresAt:O})";
}

/// <summary>
/// One poll's answer: the bundle once the user approved, or a pending signal. <see cref="IntervalSeconds"/> is the wait the
/// caller must observe before the next poll — unchanged for <c>authorization_pending</c>, raised for <c>slow_down</c>.
/// </summary>
internal sealed record KimiPollResult(KimiTokenBundle? Bundle, int IntervalSeconds)
{
    public bool IsPending => Bundle is null;

    public static KimiPollResult Pending(int intervalSeconds) => new(null, intervalSeconds);

    public static KimiPollResult Complete(KimiTokenBundle bundle, int intervalSeconds) => new(bundle, intervalSeconds);
}

/// <summary>
/// Kimi For Coding's OAuth device-code flow, the way the reference client signs in: no browser redirect, no client secret,
/// and nothing the user has to paste. It is three calls — start (a user code), poll (until the user approved it in the
/// browser) and refresh (the rotated token bundle) — all form-encoded against the same OAuth host, and all three run on
/// the shared <see cref="ProviderDeviceCode"/> skeleton with only Kimi's names, endpoints and error spellings passed in.
/// </summary>
/// <remarks>
/// The poll cadence is the server's to decide: the start response carries an interval and the poll can raise it with
/// <c>slow_down</c>, so the interval never drops below <see cref="MinimumIntervalSeconds"/> and never shrinks by itself.
/// <see cref="PollAsync"/> is a single request on purpose — waiting belongs to the caller, which can drive it from the
/// injected <see cref="TimeProvider"/> instead of a real clock.
/// </remarks>
internal sealed class KimiAuthentication
{
    /// <summary>The public OAuth client the Kimi clients authenticate as; the device flow needs no secret with it.</summary>
    public const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

    /// <summary>Where the flow runs unless an environment variable points it at a staging or self-hosted host.</summary>
    public const string DefaultOAuthHost = "https://auth.kimi.com";

    /// <summary>The first non-empty override wins, so a user can repoint the flow without editing the app.</summary>
    internal const string OAuthHostVariable = "KIMI_CODE_OAUTH_HOST";

    internal const string OAuthHostFallbackVariable = "KIMI_OAUTH_HOST";

    /// <summary>Values below this would hammer the token endpoint; it is also the reference client's own default interval.</summary>
    internal const int MinimumIntervalSeconds = 5;

    private const int DefaultExpiresInSeconds = 900;

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <summary>How a finished poll answer is read, and which error spellings end the login; built once, so polling allocates nothing new.</summary>
    private static readonly DeviceCodePoll<KimiPollResult> PollProfile = new(
        Complete: static (element, time, waited) =>
            ProviderSignInJson.Text(element, "access_token") is { Length: > 0 }
                ? KimiPollResult.Complete(Bundle(time, element, "poll"), waited)
                : null,
        Pending: static waited => KimiPollResult.Pending(waited),
        DeniedCodes: ["access_denied"],
        Detail: static element => ProviderSignInJson.Text(element, "error_description"),
        NullDetail: null,
        NoAnswerPhrase: "returned no token and no error code",
        NoAnswerWithDetailPhrase: "returned no token");

    /// <summary>How a start answer becomes the pending authorization Kimi's dialog shows.</summary>
    private static readonly Func<JsonElement, DateTimeOffset, KimiDeviceCode> ParseStart =
        static (element, now) =>
        {
            var deviceCode = ProviderSignInJson.Text(element, "device_code");
            var userCode = ProviderSignInJson.Text(element, "user_code");
            var verificationUri = ProviderDeviceCode.TrustedUrl(element, "verification_uri");
            var verificationUriComplete = ProviderDeviceCode.TrustedUrl(element, "verification_uri_complete");
            if (deviceCode is null || userCode is null || verificationUri is null || verificationUriComplete is null)
            {
                throw new ProviderException(
                    "Kimi: the device authorization response carried no device_code, user_code or verification URL.",
                    retryable: false);
            }

            var expiresIn = ProviderSignInJson.PositiveInt(element, "expires_in") ?? DefaultExpiresInSeconds;
            return new KimiDeviceCode(
                deviceCode,
                userCode,
                verificationUri,
                verificationUriComplete,
                ProviderSignInJson.Interval(element, MinimumIntervalSeconds),
                now + TimeSpan.FromSeconds(expiresIn));
        };

    /// <summary>How a refresh answer becomes the rotated bundle.</summary>
    private static readonly Func<JsonElement, string, TimeProvider, KimiTokenBundle> ParseRefresh =
        static (element, refreshToken, time) => Bundle(time, element, "refresh");

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the code and token lifetimes and the refresh backoff; tests inject one instead of sleeping.</param>
    public KimiAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The OAuth host in use: <c>KIMI_CODE_OAUTH_HOST</c>, then <c>KIMI_OAUTH_HOST</c>, then
    /// <see cref="DefaultOAuthHost"/>; trailing slashes are trimmed so the endpoint paths concatenate cleanly.
    /// </summary>
    public static string ResolveHost()
    {
        var overridden = FirstEnvironmentValue(OAuthHostVariable) ?? FirstEnvironmentValue(OAuthHostFallbackVariable);
        return (overridden ?? DefaultOAuthHost).Trim().TrimEnd('/');
    }

    /// <summary>Asks the OAuth host for a user code the user types into <see cref="KimiDeviceCode.VerificationUri"/>.</summary>
    public async Task<KimiDeviceCode> StartAsync(CancellationToken ct)
    {
        using var message = ProviderDeviceCode.FormRequest(
            ResolveHost() + "/api/oauth/device_authorization",
            [new KeyValuePair<string, string>("client_id", ClientId)]);
        return await ProviderDeviceCode.StartAsync(http, time, "Kimi", message, "device authorization", ParseStart, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Polls once. Returns the pending signal while the user has not finished authorising and the token bundle once they
    /// have. A denial, an expired code and an unrecognised answer are terminal and throw; polling too fast raises the interval.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    public async Task<KimiPollResult> PollAsync(KimiDeviceCode code, int intervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var message = ProviderDeviceCode.FormRequest(
            ResolveHost() + "/api/oauth/token",
            [
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("device_code", code.DeviceCode),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ]);
        return await ProviderDeviceCode.PollAsync(http, time, "Kimi", message, MinimumIntervalSeconds, intervalSeconds, PollProfile, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Trades the refresh token for a new bundle; the refresh token rotates, so the caller must store the answer. A transient
    /// answer (429 or a 5xx) is retried with a 1s/2s/4s backoff, because the access token is already dead and a refresh that
    /// gives up early costs the user a full sign-in.
    /// </summary>
    /// <exception cref="ProviderException">
    /// Status 401 when the refresh token is refused — the user has to sign in again — and the failing status otherwise.
    /// </exception>
    public async Task<KimiTokenBundle> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return await ProviderDeviceCode.RefreshAsync(
            http,
            time,
            "Kimi",
            ResolveHost() + "/api/oauth/token",
            [
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
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
    /// The bundle a token response carries. The access token alone is not enough: without a rotating refresh token the next
    /// expiry would force a full sign-in, and without a positive lifetime the token would have to count as already dead.
    /// </summary>
    private static KimiTokenBundle Bundle(TimeProvider time, JsonElement root, string operation)
    {
        var access = ProviderSignInJson.Text(root, "access_token");
        var refresh = ProviderSignInJson.Text(root, "refresh_token");
        var expiresIn = ProviderSignInJson.PositiveInt(root, "expires_in");
        if (access is null || refresh is null || expiresIn is null)
        {
            throw new ProviderException(
                $"Kimi: the token {operation} response carried no access_token, refresh_token and expires_in.",
                retryable: false);
        }

        return new KimiTokenBundle(access, refresh, time.GetUtcNow() + TimeSpan.FromSeconds(expiresIn.Value));
    }

    private static string? FirstEnvironmentValue(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && value.Trim().Length > 0 ? value : null;
}
