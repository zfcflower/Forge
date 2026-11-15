using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Forge.Mcp;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The HTTP side of an MCP OAuth sign-in, in memory. One handler answers both the streamable-HTTP endpoint of a server that
/// wants a bearer token and the authorization server that issues it — protected-resource metadata, AS metadata, dynamic
/// registration and the token endpoint — so the whole app wiring (transport, 401 hook, token store, browser callback) runs
/// without a socket. The URLs are loopback ones because the OAuth flow refuses a plain-http endpoint that is not loopback; the
/// handler answers them before any connection is attempted.
/// </summary>
internal sealed class FakeMcpOAuthHttp : HttpMessageHandler
{
    public const string AccessToken = "access-1";
    public const string RefreshToken = "refresh-1";
    public const string ClientId = "client-1";
    public const string Code = "auth-code-1";

    private static readonly Uri Origin = new("http://127.0.0.1:9/");

    private readonly Lock gate = new();
    private readonly List<string?> mcpAuthorizations = [];

    public static Uri McpEndpoint => new(Origin, "mcp");

    public static Uri ProtectedResourceMetadata => new(Origin, "pr");

    /// <summary>The bearer token every accepted MCP request carried, in order.</summary>
    public IReadOnlyList<string?> McpAuthorizations
    {
        get
        {
            lock (gate)
            {
                return [.. mcpAuthorizations];
            }
        }
    }

    /// <summary>How many MCP requests were answered 401 (no usable token) — the 401 the page reacts to.</summary>
    public int Unauthorized { get; private set; }

    public int Registered { get; private set; }

    public int TokensIssued { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/mcp" when request.Method == HttpMethod.Get:
                // No server-to-client stream: the transport then leaves the stream alone instead of retrying it.
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            case "/mcp":
                return await McpAsync(request, cancellationToken).ConfigureAwait(false);
            case "/pr":
                return Json($$"""{"resource":"mcp","authorization_servers":["{{new Uri(Origin, "as")}}"]}""");
            case "/as":
                return Json($$"""
                    {"authorization_endpoint":"{{new Uri(Origin, "authorize")}}",
                     "token_endpoint":"{{new Uri(Origin, "token")}}",
                     "registration_endpoint":"{{new Uri(Origin, "register")}}"}
                    """);
            case "/register":
                Registered++;
                return Json($$"""{"client_id":"{{ClientId}}"}""");
            case "/token":
                TokensIssued++;
                return Json($$"""
                    {"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","expires_in":3600,
                     "token_type":"Bearer","scope":"mcp"}
                    """);
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private async Task<HttpResponseMessage> McpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization?.ToString();
        lock (gate)
        {
            mcpAuthorizations.Add(authorization);
        }

        if (!string.Equals(authorization, "Bearer " + AccessToken, StringComparison.Ordinal))
        {
            Unauthorized++;
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = Json("{}").Content };
            challenge.Headers.TryAddWithoutValidation(
                "WWW-Authenticate",
                $"Bearer resource_metadata=\"{ProtectedResourceMetadata}\", resource=\"mcp\"");
            return challenge;
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString() ?? string.Empty;
        if (!root.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        var result = method switch
        {
            McpProtocol.Initialize => ScriptedMcpServer.InitializeWithResourcesJson,
            McpProtocol.ToolsList => """{"tools":[]}""",
            McpProtocol.ResourcesList => ResourcesJson,
            McpProtocol.ResourcesSubscribe or McpProtocol.ResourcesUnsubscribe => "{}",
            McpProtocol.Ping => "{}",
            _ => null,
        };
        if (result is null)
        {
            return Json("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"method not found\"}}");
        }

        return Json("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}");
    }

    public const string ResourcesJson =
        """{"resources":[{"uri":"res://one","name":"One"},{"uri":"res://two","name":"Two"}]}""";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// The user's browser behind <see cref="IMcpOAuthUi"/>: nothing is opened, but the authorization URL is followed the one way
/// that matters — the client's loopback listener is asked for the code with the state the client sent, on a background task,
/// exactly like a browser tab coming back.
/// </summary>
internal sealed class LoopbackOAuthBrowser : IMcpOAuthUi
{
    private static readonly HttpClient CallbackClient = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false });

    private readonly ConcurrentQueue<Uri> opened = new();

    public IReadOnlyList<Uri> Opened => opened.ToArray();

    public int OpenedCount => opened.Count;

    /// <summary>The browser-side task, so a test can await it after the sign-in returned.</summary>
    public Task Visit { get; private set; } = Task.CompletedTask;

    public void OpenBrowser(Uri authorizationUri)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        opened.Enqueue(authorizationUri);
        Visit = Task.Run(async () =>
        {
            var query = Query(authorizationUri);
            var code = FakeMcpOAuthHttp.Code;
            var state = Uri.EscapeDataString(query.GetValueOrDefault("state") ?? string.Empty);
            using var response = await CallbackClient
                .GetAsync($"{query["redirect_uri"]}?code={code}&state={state}")
                .ConfigureAwait(false);
        });
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0)
            {
                query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return query;
    }
}
