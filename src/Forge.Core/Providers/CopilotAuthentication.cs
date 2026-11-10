using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// One pending GitHub device-code authorization: what the shell shows the user and what the next poll needs. The device
/// code is a bearer credential for the poll call, so it stays out of <see cref="ToString"/>.
/// </summary>
public sealed record CopilotDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"CopilotDeviceCode(userCode={UserCode}, verificationUri={VerificationUri}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// A short-lived Copilot API session: the token that goes in the <c>Authorization</c> header and the host it is valid for.
/// The host is part of the session because Copilot mints it per account, and the token is bound to the
/// <c>Copilot-Integration-Id</c> header it was issued under, so the two travel together. Never printed with the token in it.
/// </summary>
public sealed record CopilotSession(string Token, string ApiBaseUrl, DateTimeOffset ExpiresAt)
{
    /// <summary>A minute of slack: a request that starts just before expiry would otherwise be rejected mid-stream.</summary>
    private static readonly TimeSpan ValidityMargin = TimeSpan.FromSeconds(60);

    /// <summary>Whether a request started now is worth sending with this session.</summary>
    public bool IsValidAt(DateTimeOffset now) =>
        // Added rather than subtracted: an unknown lifetime is stored as DateTimeOffset.MinValue, which would overflow.
        !string.IsNullOrEmpty(Token) && now + ValidityMargin < ExpiresAt;

    public override string ToString() => $"CopilotSession(apiBaseUrl={ApiBaseUrl}, expiresAt={ExpiresAt:O})";
}

/// <summary>
/// GitHub's device-code authorization, the way the Copilot extensions sign in: no browser redirect, no client secret, and
/// no token the user has to paste. The flow is three calls — start (get a user code), poll (until the user approved it in
/// the browser), exchange (trade the GitHub <em>user</em> token for a short-lived Copilot session token).
/// </summary>
/// <remarks>
/// Two GitHub quirks shape this class. The device-code endpoints want their JSON response negotiated explicitly
/// (<c>Accept: application/json</c>); without it they answer form-encoded. And the minted Copilot token is HMAC-bound to the
/// <c>Copilot-Integration-Id</c> header, so <see cref="IntegrationId"/> must be sent identically on the exchange and on every
/// later API call, which is why it is a published constant rather than a local literal.
/// </remarks>
public sealed class CopilotAuthentication
{
    /// <summary>The public GitHub OAuth app the Copilot clients authenticate as; the device flow needs no secret with it.</summary>
    public const string ClientId = "Iv1.b507a08c87ecfe98";

    /// <summary>Where the minted session token is used unless the account says otherwise.</summary>
    public static string DefaultApiHost { get; } = "https://api.githubcopilot.com";

    /// <summary>The integration id the Copilot token is bound to; must match on the exchange and on every API call.</summary>
    public static string IntegrationId { get; } = "copilot-chat";

    /// <summary>There is no enterprise override yet; a null host uses github.com.</summary>
    private const string GitHubHost = "https://github.com";

    private const string TokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>What the Copilot clients send; GitHub gates the device flow on a plausible editor identity.</summary>
    private const string EditorVersion = "Forge/1.0";

    private const string EditorPluginVersion = "forge/1.0";

    /// <summary>GitHub's default poll interval; used when the response omits it.</summary>
    private const int DefaultIntervalSeconds = 5;

    /// <summary>The default device-code lifetime; used when the response omits it.</summary>
    private const int DefaultExpiresInSeconds = 900;

    /// <summary>Values below this are epoch seconds, above it epoch milliseconds (see <see cref="CopilotAuthentication"/>).</summary>
    private const double EpochSecondsThreshold = 100_000_000_000d;

    private const long MaxUnixSeconds = 253_402_300_799L;

    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    /// <summary>An error body is only shown to explain a failure, never in full.</summary>
    private const int MaxErrorBodyLength = 1024;

    private const string ProviderId = "github-copilot";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    /// <param name="http">Must be the same client the API calls use, so its handler pool is shared.</param>
    /// <param name="time">Clock for the device-code and session lifetimes; tests inject a manual one instead of sleeping.</param>
    public CopilotAuthentication(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Asks GitHub for a user code the user types into <see cref="CopilotDeviceCode.VerificationUri"/>.</summary>
    /// <param name="enterpriseHost">
    /// Reserved for a GitHub Enterprise host; it must be a scheme and host with no path. Null means github.com — the flow
    /// itself is not implemented for enterprise accounts yet, so the fallback is the only working value.
    /// </param>
    public async Task<CopilotDeviceCode> StartAsync(string? enterpriseHost, CancellationToken ct)
    {
        var host = ResolveHost(enterpriseHost);
        using var message = new HttpRequestMessage(HttpMethod.Post, host + "/login/device/code")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", "read:user"),
            ]),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
        message.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);

        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, "device code request", includeBody: true, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var deviceCode = ProviderJson.Str(root, "device_code");
        var userCode = ProviderJson.Str(root, "user_code");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
        {
            throw new ProviderException(
                "GitHub: the device code response carried no device_code/user_code.",
                retryable: false);
        }

        var interval = ProviderJson.Int(root, "interval") ?? DefaultIntervalSeconds;
        var expiresIn = ProviderJson.Int(root, "expires_in") ?? DefaultExpiresInSeconds;
        return new CopilotDeviceCode(
            deviceCode,
            userCode,
            ProviderJson.Str(root, "verification_uri") ?? string.Empty,
            interval > 0 ? interval : DefaultIntervalSeconds,
            _time.GetUtcNow() + TimeSpan.FromSeconds(expiresIn > 0 ? expiresIn : DefaultExpiresInSeconds));
    }

    /// <summary>
    /// Polls once. Returns the GitHub <em>user</em> token (a <c>ghu_…</c> token, not a Copilot session token) once the user
    /// has approved the request, or <c>null</c> while GitHub says the user has not finished, or that the caller is polling
    /// too fast (both mean "wait another interval and ask again"). Denial and expiry are terminal and throw.
    /// </summary>
    /// <returns>Null while the authorization is still pending; otherwise the GitHub user token.</returns>
    public async Task<string?> PollAsync(CopilotDeviceCode code, string? enterpriseHost, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);

        var host = ResolveHost(enterpriseHost);
        using var message = new HttpRequestMessage(HttpMethod.Post, host + "/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("device_code", code.DeviceCode),
                new KeyValuePair<string, string>(
                    "grant_type",
                    "urn:ietf:params:oauth:grant-type:device_code"),
            ]),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, "device authorization poll", includeBody: true, ct)
            .ConfigureAwait(false);
        var root = document.RootElement;

        if (ProviderJson.Str(root, "access_token") is { Length: > 0 } token)
        {
            return token;
        }

        var error = ProviderJson.Str(root, "error");
        switch (error)
        {
            case "authorization_pending":
            case "slow_down":
                return null;

            case null or "":
                throw new ProviderException(
                    "GitHub: the device authorization response carried no token and no error code.",
                    retryable: false);

            default:
                var description = ProviderJson.Str(root, "error_description");
                throw new ProviderException(
                    description is { Length: > 0 }
                        ? $"GitHub: device authorization ended with '{error}' ({description})."
                        : $"GitHub: device authorization ended with '{error}'.",
                    retryable: false);
        }
    }

    /// <summary>
    /// Trades the GitHub user token for a Copilot session token. The session is short-lived (around half an hour), so the
    /// caller is expected to cache it and check <see cref="CopilotSession.IsValidAt"/> before each use.
    /// </summary>
    /// <returns>The session; <see cref="CopilotSession.ApiBaseUrl"/> falls back to <see cref="DefaultApiHost"/> when GitHub names no endpoint.</returns>
    public async Task<CopilotSession> ExchangeAsync(string gitHubUserToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitHubUserToken);

        using var message = new HttpRequestMessage(HttpMethod.Get, TokenExchangeUrl);
        // "token", not "Bearer": the GitHub user-token API only accepts the legacy scheme.
        message.Headers.TryAddWithoutValidation("Authorization", "token " + gitHubUserToken);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
        message.Headers.TryAddWithoutValidation("Copilot-Integration-Id", IntegrationId);

        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        // No body in the error: this is the one request that carries a credential the server could echo.
        using var document = await ReadJsonAsync(response, "Copilot token exchange", includeBody: false, ct)
            .ConfigureAwait(false);
        var root = document.RootElement;

        var token = ProviderJson.Str(root, "token");
        if (string.IsNullOrEmpty(token))
        {
            throw new ProviderException(
                "GitHub: the Copilot token exchange returned no token.",
                retryable: false);
        }

        var api = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("endpoints", out var endpoints)
            && endpoints.ValueKind == JsonValueKind.Object
                ? ProviderJson.Str(endpoints, "api")
                : null;

        return new CopilotSession(
            token,
            string.IsNullOrWhiteSpace(api) ? DefaultApiHost : api.Trim().TrimEnd('/'),
            // Unknown lifetime means "already expired": the caller exchanges again rather than sending a token of unknown age.
            Epoch(root, "expires_at") ?? DateTimeOffset.MinValue);
    }

    private static string ResolveHost(string? enterpriseHost)
    {
        if (string.IsNullOrWhiteSpace(enterpriseHost))
        {
            return GitHubHost;
        }

        var trimmed = enterpriseHost.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ProviderException(
                $"GitHub: '{trimmed}' is not a valid host URL for the device flow.",
                retryable: false);
        }

        return trimmed;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("GitHub: the authorization request timed out.", retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProviderException($"GitHub: the authorization request failed: {ex.Message}", retryable: true, inner: ex);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string operation,
        bool includeBody,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryable = ProviderErrors.IsRetryableStatus(status);
            if (!includeBody)
            {
                throw new ProviderException($"GitHub: {operation} failed with HTTP {status}.", retryable, status);
            }

            var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            throw ProviderErrors.FromStatus(ProviderId, response.StatusCode, body, response.Headers.RetryAfter);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(
                $"GitHub: {operation} returned a body that is not JSON.",
                retryable: false,
                inner: ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length <= MaxErrorBodyLength ? text : text[..MaxErrorBodyLength];
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return "<unreadable body>";
        }
    }

    /// <summary>
    /// <c>expires_at</c> is documented as epoch seconds. The magnitude is still checked, because a future switch to
    /// milliseconds would otherwise turn into "valid until the year 50000" instead of an obviously wrong number.
    /// </summary>
    private static DateTimeOffset? Epoch(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number)
            || number <= 0)
        {
            return null;
        }

        if (number >= EpochSecondsThreshold)
        {
            return number <= MaxUnixMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(number) : null;
        }

        return number <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(number) : null;
    }
}
