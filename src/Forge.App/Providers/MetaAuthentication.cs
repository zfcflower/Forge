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

    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <summary>How a finished poll answer is read, and which error spellings end the login; built once, so polling allocates nothing new.</summary>
    private static readonly DeviceCodePoll<MetaPollResult> PollProfile = new(
        // The completed answer is the identity token, not the API credential: the caller still has to mint a key with it.
        Complete: static (element, time, waited) =>
            ProviderSignInJson.Text(element, "access_token") is { Length: > 0 } identityToken
                ? MetaPollResult.Complete(identityToken, waited)
                : null,
        Pending: static waited => MetaPollResult.Pending(waited),
        DeniedCodes: ["access_denied", "authorization_denied"],
        Detail: Detail,
        NullDetail: Detail,
        NoAnswerPhrase: "returned no identity token and no error code",
        NoAnswerWithDetailPhrase: "returned no identity token");

    /// <summary>How a start answer becomes the pending authorization Meta's dialog shows.</summary>
    private static readonly Func<JsonElement, DateTimeOffset, MetaDeviceCode> ParseStart =
        static (element, now) =>
        {
            var deviceCode = ProviderSignInJson.Text(element, "device_code");
            var userCode = ProviderSignInJson.Text(element, "user_code");
            if (deviceCode is null || userCode is null)
            {
                throw new ProviderException(
                    "Meta: the device authorization response carried no device_code and user_code.",
                    retryable: false);
            }

            // Both URLs are optional and the complete one is preferred: it carries the code, so the user confirms instead of
            // typing it. A plain http page is never offered, because the code is typed there.
            var verificationUri = ProviderDeviceCode.SecureUrl(element, "verification_uri");
            var verificationUriComplete = ProviderDeviceCode.SecureUrl(element, "verification_uri_complete") ?? verificationUri;

            return new MetaDeviceCode(
                deviceCode,
                userCode,
                verificationUri,
                verificationUriComplete,
                ProviderSignInJson.Interval(element, MinimumIntervalSeconds),
                now + TimeSpan.FromSeconds(ProviderSignInJson.PositiveInt(element, "expires_in") ?? DefaultExpiresInSeconds));
        };

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
        using var message = ProviderDeviceCode.FormRequest(
            AuthorizationHost + DeviceAuthorizationPath,
            [new KeyValuePair<string, string>("client_id", ClientId)]);
        return await ProviderDeviceCode.StartAsync(http, time, "Meta", message, "device authorization", ParseStart, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Polls once. Returns the pending signal while the user has not finished authorising and the identity token once they
    /// have. A denial, an expired code and an unrecognised answer are terminal and throw; polling too fast raises the interval.
    /// </summary>
    /// <param name="intervalSeconds">The interval the caller just waited; it is the floor of the interval that comes back.</param>
    public async Task<MetaPollResult> PollAsync(MetaDeviceCode code, int intervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var message = ProviderDeviceCode.FormRequest(
            AuthorizationHost + DeviceTokenPath,
            [
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new KeyValuePair<string, string>("device_code", code.DeviceCode),
                new KeyValuePair<string, string>("client_id", ClientId),
            ]);
        return await ProviderDeviceCode.PollAsync(http, time, "Meta", message, MinimumIntervalSeconds, intervalSeconds, PollProfile, ct)
            .ConfigureAwait(false);
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

        var (status, root, body) = await ProviderDeviceCode.SendForJsonAsync(http, message, "Meta", "key mint", ct)
            .ConfigureAwait(false);
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
                "Meta: enabling API access failed with HTTP " + status + ProviderDeviceCode.Describe(body),
                ProviderErrors.IsRetryableStatus(status),
                status);
        }

        var element = document!.RootElement;
        if (ProviderSignInJson.Text(element, "api_key") is { Length: > 0 } apiKey)
        {
            return new MetaCredentialBundle(apiKey, identityToken, time.GetUtcNow() + KeyLifetime);
        }

        // The account is not set up yet: Meta answers with the page that finishes the job, and that page is the only useful
        // thing to tell the user — without it the failure reads like a broken server.
        var actionUrl = ProviderSignInJson.Text(element, "action_url");
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

    /// <summary>
    /// What the server said about a failure. Meta's error shape is not one name: depending on the step it fills
    /// <c>error_description</c>, <c>detail</c>, <c>message</c> or only <c>error</c>, so the first one present is the sentence.
    /// </summary>
    private static string? Detail(JsonElement element) =>
        ProviderSignInJson.Text(element, "error_description")
        ?? ProviderSignInJson.Text(element, "detail")
        ?? ProviderSignInJson.Text(element, "message")
        ?? ProviderSignInJson.Text(element, "error");
}
