using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Mcp.Tests.Fixtures;

internal sealed record RecordedAuthRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Form,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// An in-process RFC 9728/8414/7591 authorization server on 127.0.0.1. It serves protected-resource metadata, AS metadata,
/// dynamic registration, an authorize endpoint that redirects the code back to the client's loopback, and a token endpoint
/// that enforces PKCE S256 and issues/refreshes tokens. <see cref="RejectRefreshToken"/> makes a specific refresh fail.
/// </summary>
internal sealed class FakeAuthorizationServer : IAsyncDisposable
{
    public const string IssuedCode = "auth-code-1";
    public const string ClientId = "client-1";
    public const string AccessToken = "access-1";
    public const string RefreshToken = "refresh-1";

    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<RecordedAuthRequest> requests = new();
    private readonly Task acceptLoop;
    private int disposed;

    public FakeAuthorizationServer()
    {
        var port = FreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseUri.AbsoluteUri);
        listener.Start();
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }

    public Uri ProtectedResourceMetadataUri => new(BaseUri, "pr");

    public Uri AuthorizationServerMetadataUri => new(BaseUri, "as");

    public Uri RegistrationUri => new(BaseUri, "register");

    public Uri AuthorizationUri => new(BaseUri, "authorize");

    public Uri TokenUri => new(BaseUri, "token");

    /// <summary>When set, a refresh carrying this token is answered 400 invalid_grant instead of new tokens.</summary>
    public string? RejectRefreshToken { get; set; }

    /// <summary>When set, the token endpoint waits this long before answering, so a cancelled call can observe it.</summary>
    public TimeSpan? TokenDelay { get; set; }

    /// <summary>When set, the AS metadata omits <c>registration_endpoint</c> (an AS without RFC 7591 support).</summary>
    public bool OmitRegistrationEndpoint { get; set; }

    /// <summary>
    /// When set, every refresh issues the next <c>refresh-N</c> and rejects every earlier one, the RFC 6749 §6 rotation a
    /// server may enforce; the default keeps handing back <see cref="RefreshToken"/>.
    /// </summary>
    public bool RotateRefreshTokens { get; set; }

    public string? LastCodeChallenge { get; private set; }

    public string? LastCodeVerifier { get; private set; }

    private readonly Lock refreshGate = new();
    private int refreshIssued = 1;

    private string CurrentRefreshToken => "refresh-" + Volatile.Read(ref refreshIssued).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<RecordedAuthRequest> Requests => requests.ToArray();

    public int CountRequests(string method, string path) =>
        requests.Count(request => request.Method == method && request.Path == path);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        listener.Close();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var form = await ReadFormAsync(context.Request).ConfigureAwait(false);
            var query = ReadQuery(context.Request);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    headers[key] = context.Request.Headers[key] ?? string.Empty;
                }
            }

            requests.Enqueue(new RecordedAuthRequest(context.Request.HttpMethod, path, form, query, headers));

            switch (context.Request.HttpMethod)
            {
                case "GET" when path == "/pr":
                    await JsonAsync(context, $$"""{"resource":"mcp","authorization_servers":["{{AuthorizationServerMetadataUri.AbsoluteUri}}"]}""").ConfigureAwait(false);
                    return;
                case "GET" when path == "/as":
                    var metadata = new System.Text.StringBuilder();
                    metadata.Append("{\"authorization_endpoint\":\"").Append(AuthorizationUri.AbsoluteUri)
                        .Append("\",\"token_endpoint\":\"").Append(TokenUri.AbsoluteUri).Append('"');
                    if (!OmitRegistrationEndpoint)
                    {
                        metadata.Append(",\"registration_endpoint\":\"").Append(RegistrationUri.AbsoluteUri).Append('"');
                    }

                    metadata.Append('}');
                    await JsonAsync(context, metadata.ToString()).ConfigureAwait(false);
                    return;
                case "POST" when path == "/register":
                    await JsonAsync(context, $$"""{"client_id":"{{ClientId}}"}""").ConfigureAwait(false);
                    return;
                case "GET" when path == "/authorize":
                    LastCodeChallenge = query.GetValueOrDefault("code_challenge");
                    var redirect = query["redirect_uri"] + "?code=" + IssuedCode + "&state=" + Uri.EscapeDataString(query.GetValueOrDefault("state") ?? string.Empty);
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = redirect;
                    context.Response.Close();
                    return;
                case "POST" when path == "/token":
                    if (TokenDelay is { } delay)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }

                    if (form.GetValueOrDefault("grant_type") == "refresh_token")
                    {
                        if (RejectRefreshToken is { } rejected && form.GetValueOrDefault("refresh_token") == rejected)
                        {
                            await JsonAsync(context, """{"error":"invalid_grant","error_description":"refresh token rejected"}""", 400).ConfigureAwait(false);
                            return;
                        }

                        // A rotating server accepts each refresh token exactly once. The check and the rotation happen under one
                        // lock, like a real server's store would: without it two concurrent refreshes both "win" and a client
                        // that refreshes twice would look healthy.
                        string issued;
                        lock (refreshGate)
                        {
                            if (RotateRefreshTokens && form.GetValueOrDefault("refresh_token") != CurrentRefreshToken)
                            {
                                issued = string.Empty;
                            }
                            else
                            {
                                if (RotateRefreshTokens)
                                {
                                    Interlocked.Increment(ref refreshIssued);
                                }

                                issued = CurrentRefreshToken;
                            }
                        }

                        if (issued.Length == 0)
                        {
                            await JsonAsync(context, """{"error":"invalid_grant","error_description":"refresh token was rotated"}""", 400).ConfigureAwait(false);
                            return;
                        }

                        await JsonAsync(context, $$"""{"access_token":"access-1","refresh_token":"{{issued}}","expires_in":3600,"token_type":"Bearer","scope":"mcp"}""").ConfigureAwait(false);
                        return;
                    }

                    LastCodeVerifier = form.GetValueOrDefault("code_verifier");
                    if (form.GetValueOrDefault("code") != IssuedCode)
                    {
                        await JsonAsync(context, """{"error":"invalid_grant","error_description":"bad code"}""", 400).ConfigureAwait(false);
                        return;
                    }

                    if (LastCodeChallenge is { } challenge && !string.Equals(ChallengeOf(LastCodeVerifier ?? string.Empty), challenge, StringComparison.Ordinal))
                    {
                        await JsonAsync(context, """{"error":"invalid_grant","error_description":"PKCE failed"}""", 400).ConfigureAwait(false);
                        return;
                    }

                    await JsonAsync(context, $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","expires_in":3600,"token_type":"Bearer","scope":"mcp"}""").ConfigureAwait(false);
                    return;
                default:
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
            }
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private static string ChallengeOf(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpListenerRequest request)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!request.HasEntityBody)
        {
            return form;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            form[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return form;
    }

    private static Dictionary<string, string> ReadQuery(HttpListenerRequest request) =>
        request.QueryString.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => request.QueryString[key!] ?? string.Empty, StringComparer.Ordinal);

    private static async Task JsonAsync(HttpListenerContext context, string json, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}
