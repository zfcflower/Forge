using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>
/// The 401 challenge of RFC 9728: the protected-resource metadata URL the resource server pointed at and the optional
/// RFC 8707 resource indicator. <see cref="ResourceMetadataUrl"/> is the one required field; without it the client cannot
/// discover the authorization server, so the transport treats the 401 as plain "not authorized".
/// </summary>
public sealed record McpOAuthChallenge(string? ResourceMetadataUrl, string? Resource)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(ResourceMetadataUrl);
}

/// <summary>A registered public OAuth client: the id the authorization server issued and the endpoint that mints tokens.</summary>
public sealed record McpOAuthRegistration(string ClientId, string TokenEndpoint);

/// <summary>What the RFC 8414 authorization-server metadata advertised, flattened to the fields the flow needs.</summary>
public sealed record McpOAuthDiscovery(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    string? Resource,
    IReadOnlyList<string> ScopesSupported);

/// <summary>One PKCE verifier and its S256 challenge, both base64url (RFC 7636). The verifier is never logged.</summary>
internal sealed record McpPkce(string Verifier, string Challenge)
{
    public override string ToString() => "McpPkce(verifier=<hidden>)";
}

/// <summary>An OAuth step failed with an answer the user cannot fix by retrying (a rejected exchange, a missing field).</summary>
public sealed class McpOAuthException(string message, Exception? inner = null) : McpException(message, inner);

/// <summary>
/// MCP OAuth for streamable HTTP servers: RFC 9728 protected-resource discovery → RFC 8414 authorization-server metadata →
/// RFC 7591 dynamic client registration → authorization code + PKCE (S256) with a loopback redirect (BCL
/// <see cref="HttpListener"/>, random port) → token exchange and refresh. The browser side is injected through
/// <see cref="IMcpOAuthUi"/>; this class never opens one on its own, and <see cref="RefreshAsync"/> never opens one at all.
/// <para>This PKCE/loopback helper is deliberately duplicated from the app's OpenRouter sign-in: the library cannot reference
/// the app's provider helpers without breaking its Abstractions + BCL boundary.</para>
/// </summary>
public sealed class McpOAuth
{
    /// <summary>The one path the loopback listener answers; the code arrives as its <c>code</c> query parameter.</summary>
    internal const string CallbackPath = "/callback";

    /// <summary>The whole sign-in gives up here, so a tab left open cannot hold the listener forever.</summary>
    internal static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one OAuth step (metadata, registration, token) may take. The client the host injects carries no timeout of its
    /// own, so each step gets this budget and a stalled connection cannot outlive the sign-in it belongs to.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A probed port can be taken between the probe and the bind, so a few candidates are tried.</summary>
    private const int PortAttempts = 8;

    internal const string CallbackPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Forge</title></head>" +
        "<body style=\"font-family:sans-serif;margin:3rem\"><h1>Signed in</h1>" +
        "<p>The MCP server is connected. You can close this tab and return to Forge.</p></body></html>";

    internal const string CallbackProblemPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Forge</title></head>" +
        "<body style=\"font-family:sans-serif;margin:3rem\"><h1>Sign-in incomplete</h1>" +
        "<p>This page carried no usable authorization code. Close it and start the sign-in in Forge again.</p></body></html>";

    private static readonly HttpClient SharedClient = CreateSharedClient();

    private readonly HttpClient http;
    private readonly IMcpOAuthUi? ui;
    private readonly TimeProvider time;

    /// <summary>
    /// The client ids minted this session, keyed by (resource metadata URL, resource). A refresh reuses the id the refresh
    /// token was issued to; after a restart the cache is empty and the flow re-registers (see the report's known gap).
    /// </summary>
    private readonly ConcurrentDictionary<string, McpOAuthRegistration> registrations = new(StringComparer.Ordinal);

    public McpOAuth(HttpClient http, IMcpOAuthUi? ui = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.ui = ui;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The shared client for hosts that did not inject one; owned by this type, like the transport's.</summary>
    public static HttpClient DefaultClient => SharedClient;

    /// <summary>
    /// Runs the whole interactive flow and returns the tokens. Registration happens after the listener is bound, because the
    /// random loopback port is part of the redirect URI the registration advertises.
    /// </summary>
    public async Task<McpTokenSet> SignInAsync(McpOAuthChallenge challenge, CancellationToken ct)
    {
        if (ui is null)
        {
            throw new McpOAuthException("No browser UI was wired, so the OAuth sign-in cannot start.");
        }

        var pkce = CreatePkce();
        var state = RandomState();
        using var listener = Listen(out var port);
        var redirectUri = CallbackUrl(port).AbsoluteUri;
        var discovery = await DiscoverAsync(challenge, ct).ConfigureAwait(false);
        var registration = await RegisterAsync(discovery, redirectUri, ct).ConfigureAwait(false);
        CacheRegistration(challenge, registration);

        var authorize = AuthorizeUri(
            discovery.AuthorizationEndpoint,
            registration.ClientId,
            redirectUri,
            pkce.Challenge,
            state,
            challenge.Resource ?? discovery.Resource);
        try
        {
            ui.OpenBrowser(authorize);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpOAuthException($"Could not open the browser for sign-in: {exception.Message}", exception);
        }

        var code = await WaitForCodeAsync(listener, state, ct).ConfigureAwait(false);
        return await ExchangeAsync(
            registration,
            code,
            pkce.Verifier,
            redirectUri,
            challenge.Resource ?? discovery.Resource,
            ct).ConfigureAwait(false);
    }

    /// <summary>RFC 9728 → RFC 8414: fetch both metadata documents and flatten them.</summary>
    public async Task<McpOAuthDiscovery> DiscoverAsync(McpOAuthChallenge challenge, CancellationToken ct)
    {
        if (challenge.IsEmpty)
        {
            throw new McpOAuthException("The 401 challenge carried no resource_metadata URL.");
        }

        using var protectedResource = await GetJsonAsync(challenge.ResourceMetadataUrl!, "protected resource metadata", ct).ConfigureAwait(false);
        var protectedRoot = protectedResource.RootElement;
        var authorizationServers = StringArray(protectedRoot, "authorization_servers");
        if (authorizationServers.Count == 0)
        {
            throw new McpOAuthException("The protected resource metadata listed no authorization servers.");
        }

        using var serverMetadata = await GetJsonAsync(authorizationServers[0], "authorization server metadata", ct).ConfigureAwait(false);
        var serverRoot = serverMetadata.RootElement;
        var tokenEndpoint = Text(serverRoot, "token_endpoint")
            ?? throw new McpOAuthException("The authorization server metadata carried no token_endpoint.");
        var authorizationEndpoint = Text(serverRoot, "authorization_endpoint")
            ?? throw new McpOAuthException("The authorization server metadata carried no authorization_endpoint.");
        var registrationEndpoint = Text(serverRoot, "registration_endpoint");
        // The discovery output is what every later step talks to, so the scheme is checked here once, before anything
        // carrying a code or a token is sent to it.
        RequireSecure(tokenEndpoint, "token endpoint");
        RequireSecure(authorizationEndpoint, "authorization endpoint");
        if (registrationEndpoint is { Length: > 0 })
        {
            RequireSecure(registrationEndpoint, "registration endpoint");
        }

        var resource = challenge.Resource ?? Text(protectedRoot, "resource");
        return new McpOAuthDiscovery(
            authorizationEndpoint,
            tokenEndpoint,
            registrationEndpoint,
            resource,
            StringArray(serverRoot, "scopes_supported"));
    }

    /// <summary>RFC 7591: register this public client and return the id the server issued.</summary>
    public async Task<string> RegisterAsync(McpOAuthChallenge challenge, string redirectUri, CancellationToken ct)
    {
        var discovery = await DiscoverAsync(challenge, ct).ConfigureAwait(false);
        return (await RegisterAsync(discovery, redirectUri, ct).ConfigureAwait(false)).ClientId;
    }

    /// <summary>RFC 6749 §4.1.3: trade the authorization code for tokens with the PKCE verifier.</summary>
    public async Task<McpTokenSet> ExchangeAsync(
        string tokenEndpoint,
        string clientId,
        string code,
        string verifier,
        string redirectUri,
        string? resource,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        return await ExchangeAsync(
            new McpOAuthRegistration(clientId, tokenEndpoint),
            code,
            verifier,
            redirectUri,
            resource,
            ct).ConfigureAwait(false);
    }

    /// <summary>RFC 6749 §6: refresh an expired access token; a rotating refresh token replaces the stored one.</summary>
    public async Task<McpTokenSet> RefreshAsync(McpTokenSet tokens, McpOAuthChallenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new McpOAuthException("The stored tokens carry no refresh token.");
        }

        // A restart loses the in-process registration cache; the tokens carry the client they were issued to, so seed
        // it before refreshing instead of registering a second client the server may reject (wave W10d integration).
        if (!string.IsNullOrWhiteSpace(tokens.ClientId) && !string.IsNullOrWhiteSpace(tokens.TokenEndpoint))
        {
            registrations.TryAdd(CacheKey(challenge), new McpOAuthRegistration(tokens.ClientId, tokens.TokenEndpoint));
        }

        var registration = await GetRegistrationAsync(challenge, ct).ConfigureAwait(false);
        RequireSecure(registration.TokenEndpoint, "token endpoint");
        var form = new Dictionary<string, string>(3)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = registration.ClientId,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, registration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var document = await SendForJsonAsync(message, "token refresh", ct).ConfigureAwait(false);
        return TokenSetOf(document.RootElement, challenge.Resource ?? tokens.Resource, tokens, registration);
    }

    /// <summary>Parses the <c>Bearer</c> 401 challenge parameter (<c>resource_metadata="…", resource="…"</c>).</summary>
    public static McpOAuthChallenge? ParseChallengeParameter(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return null;
        }

        string? resourceMetadata = null;
        string? resource = null;
        foreach (var part in SplitQuoted(parameter))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals].Trim();
            var value = Unquote(part[(equals + 1)..].Trim());
            if (string.Equals(key, "resource_metadata", StringComparison.OrdinalIgnoreCase))
            {
                resourceMetadata = value;
            }
            else if (string.Equals(key, "resource", StringComparison.OrdinalIgnoreCase))
            {
                resource = value;
            }
        }

        return string.IsNullOrWhiteSpace(resourceMetadata) ? null : new McpOAuthChallenge(resourceMetadata, resource);
    }

    /// <summary>A fresh verifier (32 random bytes, base64url) and the SHA-256 challenge derived from it.</summary>
    internal static McpPkce CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new McpPkce(verifier, ChallengeOf(verifier));
    }

    /// <summary><c>BASE64URL(SHA256(ASCII(verifier)))</c>, the S256 challenge of RFC 7636.</summary>
    internal static string ChallengeOf(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>Base64 with <c>+</c>→<c>-</c>, <c>/</c>→<c>_</c> and no padding: the only alphabet PKCE allows.</summary>
    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static Uri AuthorizeUri(
        string authorizationEndpoint,
        string clientId,
        string redirectUri,
        string challenge,
        string state,
        string? resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var builder = new StringBuilder(authorizationEndpoint);
        builder.Append(authorizationEndpoint.Contains('?') ? '&' : '?');
        builder.Append("response_type=code");
        builder.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
        builder.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        builder.Append("&code_challenge=").Append(Uri.EscapeDataString(challenge));
        builder.Append("&code_challenge_method=S256");
        builder.Append("&state=").Append(Uri.EscapeDataString(state));
        if (!string.IsNullOrWhiteSpace(resource))
        {
            builder.Append("&resource=").Append(Uri.EscapeDataString(resource));
        }

        return new Uri(builder.ToString());
    }

    /// <summary>Where the browser comes back: the loopback address the listener was bound to, always over plain http.</summary>
    internal static Uri CallbackUrl(int port) => new($"http://127.0.0.1:{port}{CallbackPath}");

    private async Task<McpOAuthRegistration> RegisterAsync(McpOAuthDiscovery discovery, string redirectUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(discovery.RegistrationEndpoint))
        {
            throw new McpOAuthException("The authorization server does not support dynamic client registration (RFC 7591).");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, discovery.RegistrationEndpoint)
        {
            Content = JsonContent(RegistrationBody(redirectUri)),
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var document = await SendForJsonAsync(message, "dynamic client registration", ct).ConfigureAwait(false);
        var clientId = Text(document.RootElement, "client_id")
            ?? throw new McpOAuthException("The registration response carried no client_id.");
        return new McpOAuthRegistration(clientId, discovery.TokenEndpoint);
    }

    private async Task<McpTokenSet> ExchangeAsync(
        McpOAuthRegistration registration,
        string code,
        string verifier,
        string redirectUri,
        string? resource,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>(5)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["client_id"] = registration.ClientId,
        };
        RequireSecure(registration.TokenEndpoint, "token endpoint");
        using var message = new HttpRequestMessage(HttpMethod.Post, registration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var document = await SendForJsonAsync(message, "token exchange", ct).ConfigureAwait(false);
        return TokenSetOf(document.RootElement, resource, previous: null, registration);
    }

    private async Task<McpOAuthRegistration> GetRegistrationAsync(McpOAuthChallenge challenge, CancellationToken ct)
    {
        var key = CacheKey(challenge);
        if (registrations.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var discovery = await DiscoverAsync(challenge, ct).ConfigureAwait(false);
        // Refresh never uses the redirect URI; a valid loopback URL satisfies RFC 7591's required field.
        var registration = await RegisterAsync(discovery, LoopbackRedirectUri(), ct).ConfigureAwait(false);
        registrations[key] = registration;
        return registration;
    }

    private void CacheRegistration(McpOAuthChallenge challenge, McpOAuthRegistration registration) =>
        registrations[CacheKey(challenge)] = registration;

    private static string CacheKey(McpOAuthChallenge challenge) =>
        (challenge.ResourceMetadataUrl ?? string.Empty) + '\u0000' + (challenge.Resource ?? string.Empty);

    private McpTokenSet TokenSetOf(JsonElement root, string? resource, McpTokenSet? previous, McpOAuthRegistration registration)
    {
        var accessToken = Text(root, "access_token")
            ?? throw new McpOAuthException("The token response carried no access_token.");
        var refreshToken = Text(root, "refresh_token") ?? previous?.RefreshToken;
        var expiresAt = Number(root, "expires_in") is { } seconds
            ? time.GetUtcNow().AddSeconds(seconds)
            : (DateTimeOffset?)null;
        return new McpTokenSet(
            accessToken,
            refreshToken,
            expiresAt,
            Text(root, "token_type"),
            Text(root, "scope"),
            resource ?? Text(root, "resource"),
            registration.ClientId,
            registration.TokenEndpoint);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string operation, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await SendForJsonAsync(message, operation, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage message, string operation, CancellationToken ct)
    {
        RequireSecure(message.RequestUri?.OriginalString, operation);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpOAuthException($"The {operation} request timed out after {RequestTimeout.TotalSeconds:0}s.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new McpOAuthException($"The {operation} request failed: {exception.Message}", exception);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpOAuthException($"The {operation} request timed out after {RequestTimeout.TotalSeconds:0}s.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new McpOAuthException($"The {operation} response could not be read.", exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new McpOAuthException($"{operation} failed with HTTP {(int)response.StatusCode}{ErrorDetail(body)}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new McpOAuthException($"{operation} returned invalid JSON", exception);
            }
        }
    }

    /// <summary>
    /// Every endpoint the flow talks to must be <c>https</c> (RFC 9728 §2.1): the metadata probes, registration, the token
    /// exchange and the authorization URL all carry identifiers, the authorization code or the refresh token, so a plain-http
    /// endpoint would put them on the wire in cleartext. A loopback address is the one exception — the redirect listener lives
    /// there and so may a local authorization server (RFC 8252 §7.3).
    /// </summary>
    internal static void RequireSecure(string? url, string operation)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new McpOAuthException($"The {operation} URL '{url}' is not an absolute URI.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.IsLoopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new McpOAuthException(
            $"The {operation} URL '{url}' must be https; '{uri.Scheme}' would send the sign-in over the network in cleartext.");
    }

    private static string RegistrationBody(string redirectUri)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("client_name", "Forge");
            writer.WritePropertyName("redirect_uris");
            writer.WriteStartArray();
            writer.WriteStringValue(redirectUri);
            writer.WriteEndArray();
            writer.WriteString("token_endpoint_auth_method", "none");
            writer.WritePropertyName("grant_types");
            writer.WriteStartArray();
            writer.WriteStringValue("authorization_code");
            writer.WriteStringValue("refresh_token");
            writer.WriteEndArray();
            writer.WritePropertyName("response_types");
            writer.WriteStartArray();
            writer.WriteStringValue("code");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static HttpContent JsonContent(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> StringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static string ErrorDetail(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var detail = Text(root, "error_description") ?? Text(root, "error") ?? Text(root, "message");
            if (detail is { Length: > 0 })
            {
                return ": " + detail;
            }
        }
        catch (JsonException)
        {
        }

        return ": " + (body.Length <= 300 ? body : body[..300]);
    }

    private static IEnumerable<string> SplitQuoted(string text)
    {
        var start = 0;
        var inQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '"' && (index == 0 || text[index - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Replace("\\\"", "\"");
    }

    private static string RandomState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string LoopbackRedirectUri() => CallbackUrl(FreePort()).AbsoluteUri;

    private static HttpListener Listen(out int port)
    {
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var candidate = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new McpOAuthException("No free loopback port was available for the OAuth callback.");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var expired = Task.Delay(LoginTimeout, time, limit.Token);
        try
        {
            while (true)
            {
                var context = await NextContextAsync(listener, expired, ct).ConfigureAwait(false);
                var isCallback = string.Equals(
                    context.Request.Url?.AbsolutePath,
                    CallbackPath,
                    StringComparison.OrdinalIgnoreCase);
                var code = isCallback ? context.Request.QueryString["code"] : null;
                var returnedState = context.Request.QueryString["state"];
                if (isCallback && code is { Length: > 0 } && string.Equals(returnedState, state, StringComparison.Ordinal))
                {
                    await AnswerAsync(context.Response, ok: true, ct).ConfigureAwait(false);
                    return code;
                }

                await AnswerAsync(context.Response, ok: false, ct).ConfigureAwait(false);
                if (isCallback)
                {
                    throw new McpOAuthException("The OAuth callback carried no valid authorization code.");
                }
            }
        }
        finally
        {
            await limit.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<HttpListenerContext> NextContextAsync(
        HttpListener listener,
        Task expired,
        CancellationToken ct)
    {
        var pending = listener.GetContextAsync();
        if (await Task.WhenAny(pending, expired).ConfigureAwait(false) == pending)
        {
            return await pending.ConfigureAwait(false);
        }

        listener.Stop();
        _ = pending.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.ExecuteSynchronously);
        ct.ThrowIfCancellationRequested();
        throw new McpOAuthException("The OAuth sign-in timed out.");
    }

    private static async Task AnswerAsync(HttpListenerResponse response, bool ok, CancellationToken ct)
    {
        using (response)
        {
            var body = Encoding.UTF8.GetBytes(ok ? CallbackPage : CallbackProblemPage);
            response.StatusCode = ok ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = body.Length;
            try
            {
                await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
