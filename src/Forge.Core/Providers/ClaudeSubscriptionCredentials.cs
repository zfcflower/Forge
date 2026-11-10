using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The Anthropic subscription the local <c>claude</c> CLI is signed in to: the OAuth access token that the Messages API
/// accepts in place of an API key, plus what the shell needs to show which plan it spends. Usage on this token bills the
/// user's Claude subscription instead of a Console API key, so it is a credential in its own right and never leaves this
/// object as anything but the <c>Authorization</c> header.
/// </summary>
/// <remarks>
/// The token itself is omitted from <see cref="ToString"/>, which is what a record would otherwise print.
/// </remarks>
public sealed record ClaudeSubscription(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    IReadOnlyList<string> Scopes)
{
    /// <summary>Nobody is signed in: no file, no OAuth block, or an empty access token.</summary>
    public static ClaudeSubscription None { get; } = new(string.Empty, null, null, null, []);

    /// <summary>Whether requests can be made at all; an expired token still counts, because it can be refreshed.</summary>
    public bool IsSignedIn => !string.IsNullOrEmpty(AccessToken);

    public override string ToString() =>
        $"ClaudeSubscription(signedIn={IsSignedIn}, expiresAt={ExpiresAt:O}, subscriptionType={SubscriptionType}, scopes={Scopes.Count})";
}

/// <summary>
/// Reads, refreshes and rewrites <c>~/.claude/.credentials.json</c>, the file the <c>claude</c> CLI keeps its OAuth login
/// in. Forge does not own that file — it is shared with the CLI, which keeps writing it — so the read is tolerant (a
/// missing or half-empty file is simply "not signed in") and the write-back preserves every field Forge does not own.
/// </summary>
/// <remarks>
/// <para>
/// The access token is short-lived (the CLI refreshes it too), so a token that is within five minutes of expiry is
/// refreshed before it is used. An <em>unknown</em> expiry also counts as due: the refresh response always carries a
/// lifetime, so the worst case is one extra refresh that then makes the expiry known again. Not refreshing costs the
/// user a turn that fails at the server with a 401.
/// </para>
/// <para>
/// The refresh request needs two peculiar headers. The OAuth endpoint rate-limits (429) callers whose <c>User-Agent</c>
/// starts with <c>claude-code/</c>, so it is sent as the CLI's own <c>axios/1.8.4</c>; and the <c>client_id</c> is the
/// public Claude Code OAuth client, not a Forge-registered one.
/// </para>
/// </remarks>
public sealed class ClaudeSubscriptionCredentials
{
    /// <summary>The JSON property Forge reads and rewrites; everything else in the file is left byte-for-byte alone.</summary>
    private const string OAuthProperty = "claudeAiOauth";

    /// <summary>Refresh this long before expiry, so a token does not die between the check and the request.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>The refresh must not outlive the turn it was needed for.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Values below this are epoch seconds (year 5138 in seconds, 1973 in milliseconds); above, epoch milliseconds.</summary>
    private const double EpochSecondsThreshold = 100_000_000_000d;

    private const long MinUnixMilliseconds = -62_135_596_800_000L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _credentialsPath;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public ClaudeSubscriptionCredentials(string credentialsPath, HttpClient http, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPath);
        ArgumentNullException.ThrowIfNull(http);
        _credentialsPath = credentialsPath;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The CLI's credential file for the current user.</summary>
    public static string DefaultCredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    /// <summary>Where an expired access token is traded for a fresh one.</summary>
    public static string TokenUrl { get; } = "https://platform.claude.com/v1/oauth/token";

    /// <summary>The public Claude Code OAuth client; the token is only accepted for this client id.</summary>
    public static string ClientId { get; } = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>
    /// The identity Anthropic expects as the first system block of an OAuth request. Requests whose first system block is
    /// anything else are rejected for every model but Haiku, and requests without it do not get the subscription's
    /// prompt-cache and computer-use entitlements.
    /// </summary>
    public static string IdentityPreamble { get; } = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>
    /// The <c>anthropic-beta</c> features an OAuth request must opt into: <c>claude-code-20250219</c> admits the Claude Code
    /// client identity, <c>oauth-2025-04-20</c> admits an OAuth bearer token instead of an API key, and
    /// <c>fine-grained-tool-streaming-2025-05-14</c> keeps tool arguments streaming as the code expects.
    /// </summary>
    public static IReadOnlyList<string> Betas { get; } =
    [
        "claude-code-20250219",
        "oauth-2025-04-20",
        "fine-grained-tool-streaming-2025-05-14",
    ];

    /// <summary>
    /// Returns the stored subscription, refreshing it first when it is expired or about to expire and a refresh token is
    /// available. Never returns <c>null</c>: "not signed in" is <see cref="ClaudeSubscription.None"/>.
    /// </summary>
    /// <exception cref="ProviderException">
    /// The file exists but cannot be read or parsed, or the refresh failed. Always non-retryable, so a turn fails fast
    /// instead of retrying into the same wall, and never carries the token in its message.
    /// </exception>
    public async Task<ClaudeSubscription> ReadAsync(CancellationToken ct)
    {
        var root = await ReadDocumentAsync(ct).ConfigureAwait(false);
        if (root?[OAuthProperty] is not JsonObject oauth)
        {
            return ClaudeSubscription.None;
        }

        var current = Parse(oauth);
        if (!current.IsSignedIn)
        {
            return ClaudeSubscription.None;
        }

        var now = _time.GetUtcNow();
        var due = current.ExpiresAt is not { } expiresAt || expiresAt - now <= RefreshMargin;
        if (!due || string.IsNullOrEmpty(current.RefreshToken))
        {
            return current;
        }

        return await RefreshAsync(root, oauth, current, now, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject?> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _credentialsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the existence check and the open: the user signed out, not a failure.
            return null;
        }
        catch (JsonException ex)
        {
            throw new ProviderException(
                $"Claude subscription: '{_credentialsPath}' is not valid JSON.",
                retryable: false,
                inner: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                $"Claude subscription: '{_credentialsPath}' could not be read ({ex.GetType().Name}).",
                retryable: false,
                inner: ex);
        }
    }

    private async Task<ClaudeSubscription> RefreshAsync(
        JsonObject root,
        JsonObject oauth,
        ClaudeSubscription current,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var request = new ClaudeOAuthRefreshRequest
        {
            GrantType = "refresh_token",
            RefreshToken = current.RefreshToken!,
            ClientId = ClientId,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            request,
            ClaudeCredentialsJsonContext.Default.ClaudeOAuthRefreshRequest);

        ClaudeSubscription updated;
        using (var message = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new ByteArrayContent(payload),
        })
        {
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            // The OAuth endpoint answers 429 to User-Agents that start with "claude-code/"; the CLI's own axios UA is the
            // one it expects, and no other header identifies the caller.
            message.Headers.TryAddWithoutValidation("User-Agent", "axios/1.8.4");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RefreshTimeout);

            try
            {
                using var response = await _http
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Never the body: a token endpoint's error can echo the credential back.
                    throw new ProviderException(
                        $"Claude subscription: token refresh was rejected with HTTP {(int)response.StatusCode}.",
                        retryable: false,
                        (int)response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                updated = ParseRefreshResponse(document.RootElement, current, now);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(
                    "Claude subscription: token refresh timed out.",
                    retryable: false,
                    inner: ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ProviderException(
                    $"Claude subscription: token refresh failed: {ex.Message}",
                    retryable: false,
                    inner: ex);
            }
            catch (JsonException ex)
            {
                throw new ProviderException(
                    "Claude subscription: token refresh returned a malformed body.",
                    retryable: false,
                    inner: ex);
            }
        }

        Persist(root, oauth, updated);
        return updated;
    }

    private static ClaudeSubscription ParseRefreshResponse(JsonElement root, ClaudeSubscription current, DateTimeOffset now)
    {
        var accessToken = ProviderJson.Str(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new ProviderException(
                "Claude subscription: token refresh returned no access token.",
                retryable: false);
        }

        var refreshToken = ProviderJson.Str(root, "refresh_token");
        var expiresIn = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("expires_in", out var lifetime)
            && lifetime.ValueKind == JsonValueKind.Number
            && lifetime.TryGetInt64(out var seconds)
                ? seconds
                : (long?)null;

        return current with
        {
            AccessToken = accessToken,
            RefreshToken = string.IsNullOrEmpty(refreshToken) ? current.RefreshToken : refreshToken,
            ExpiresAt = expiresIn is { } window ? now + TimeSpan.FromSeconds(window) : null,
        };
    }

    /// <summary>
    /// Writes the refreshed token back so the CLI and the next launch see it. Deliberately best-effort: a read-only home
    /// directory must not fail the turn, and the trace line names the file but never the tokens.
    /// </summary>
    private void Persist(JsonObject root, JsonObject oauth, ClaudeSubscription updated)
    {
        try
        {
            oauth["accessToken"] = updated.AccessToken;
            if (updated.RefreshToken is { Length: > 0 } refreshToken)
            {
                oauth["refreshToken"] = refreshToken;
            }

            if (updated.ExpiresAt is { } expiresAt)
            {
                // Modern CLI builds store epoch milliseconds; seconds are only accepted on read.
                oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
            }

            var json = root.ToJsonString(WriteOptions);
            var temporary = _credentialsPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _credentialsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning(
                $"Claude subscription: the refreshed token was not written back to '{_credentialsPath}' ({ex.GetType().Name}).");
        }
    }

    private static ClaudeSubscription Parse(JsonObject oauth) => new(
        AccessToken: Text(oauth["accessToken"]) ?? string.Empty,
        RefreshToken: Text(oauth["refreshToken"]),
        ExpiresAt: Instant(oauth["expiresAt"]),
        SubscriptionType: Text(oauth["subscriptionType"]),
        Scopes: Scopes(oauth["scopes"]));

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// The CLI has written <c>expiresAt</c> in both milliseconds and seconds over its history, so the unit is derived from
    /// the magnitude: no plausible epoch-seconds value reaches 1e11, and no plausible epoch-milliseconds value is below it.
    /// </summary>
    private static DateTimeOffset? Instant(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        double number;
        if (value.TryGetValue<long>(out var integer))
        {
            number = integer;
        }
        else if (!value.TryGetValue<double>(out number))
        {
            return null;
        }

        if (!double.IsFinite(number) || number <= 0)
        {
            return null;
        }

        var milliseconds = number >= EpochSecondsThreshold ? number : number * 1000d;
        if (milliseconds is < MinUnixMilliseconds or > MaxUnixMilliseconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static IReadOnlyList<string> Scopes(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var scopes = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (Text(item) is { Length: > 0 } scope)
            {
                scopes.Add(scope);
            }
        }

        return scopes.Count == 0 ? [] : scopes;
    }
}

/// <summary>The refresh grant the token endpoint expects; serialised through a source-generated context so the AOT build never reflects.</summary>
internal sealed class ClaudeOAuthRefreshRequest
{
    [JsonPropertyName("grant_type")]
    public string GrantType { get; init; } = "refresh_token";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = string.Empty;
}

[JsonSerializable(typeof(ClaudeOAuthRefreshRequest))]
internal sealed partial class ClaudeCredentialsJsonContext : JsonSerializerContext;
