using System.Collections.Concurrent;
using System.Collections.Generic;
using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Manager;

public sealed class McpServerManagerOAuthTests
{
    private static McpServerConfig HttpServer(string name, string url) => new() { Name = name, Url = url };

    private static Dictionary<string, string> ChallengeTo(FakeAuthorizationServer auth) => new()
    {
        ["WWW-Authenticate"] = $"Bearer resource_metadata=\"{auth.ProtectedResourceMetadataUri.AbsoluteUri}\", resource=\"mcp\"",
    };

    private static string TokenKey(Uri endpoint, string? resource) =>
        endpoint.GetLeftPart(UriPartial.Authority) + '\u0000' + (resource ?? string.Empty);

    [Fact]
    public async Task A_401_marks_the_server_needing_sign_in_and_sign_in_recovers_it()
    {
        await using var auth = new FakeAuthorizationServer();
        await using var mcp = new FakeMcpHttpServer();
        mcp.Override = request =>
        {
            if (request.Method != "POST")
            {
                return null;
            }

            if (!string.Equals(request.Header("Authorization"), "Bearer " + FakeAuthorizationServer.AccessToken, StringComparison.Ordinal))
            {
                return new HttpReply(401, "", "application/json", ChallengeTo(auth));
            }

            return null;
        };

        var store = new MemoryTokenStore();
        var browser = new FakeOAuthBrowser(DirectHttp.Client);
        await using var manager = new McpServerManager(
            new McpConfig([HttpServer("srv", mcp.Endpoint.AbsoluteUri)], []),
            new McpServerManagerOptions
            {
                HttpClient = DirectHttp.Client,
                TokenStore = store,
                OAuthUi = browser,
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
            });

        await Assert.ThrowsAsync<McpOAuthRequiredException>(() => manager.StartAsync("srv", CancellationToken.None));

        var state = manager.GetState("srv");
        Assert.Equal(McpServerStatus.Failed, state.Status);
        Assert.True(state.NeedsSignIn);
        Assert.Equal(McpServerManager.SignInRequiredMessage, state.LastError);
        Assert.False(manager.IsSignedIn("srv"));

        await manager.SignInAsync("srv", CancellationToken.None);

        Assert.True(manager.IsSignedIn("srv"));
        Assert.True(manager.GetState("srv").IsReady);
        Assert.False(manager.GetState("srv").NeedsSignIn);
        Assert.Equal(1, browser.OpenedCount);
        // The stored token carries the registration it was issued to: without it the next restart re-registers and the
        // server may reject the refresh token as issued to another client.
        var stored = Assert.Single(store.Tokens).Value;
        Assert.Equal(FakeAuthorizationServer.AccessToken, stored.AccessToken);
        Assert.Equal(FakeAuthorizationServer.ClientId, stored.ClientId);
        Assert.Equal(auth.TokenUri.AbsoluteUri, stored.TokenEndpoint);
    }

    [Fact]
    public async Task Sign_out_clears_the_token_and_stops_the_server()
    {
        await using var auth = new FakeAuthorizationServer();
        await using var mcp = new FakeMcpHttpServer();
        mcp.Override = request =>
        {
            if (request.Method != "POST")
            {
                return null;
            }

            if (!string.Equals(request.Header("Authorization"), "Bearer " + FakeAuthorizationServer.AccessToken, StringComparison.Ordinal))
            {
                return new HttpReply(401, "", "application/json", ChallengeTo(auth));
            }

            return null;
        };

        var store = new MemoryTokenStore();
        await using var manager = new McpServerManager(
            new McpConfig([HttpServer("srv", mcp.Endpoint.AbsoluteUri)], []),
            new McpServerManagerOptions
            {
                HttpClient = DirectHttp.Client,
                TokenStore = store,
                OAuthUi = new FakeOAuthBrowser(DirectHttp.Client),
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
            });

        await manager.SignInAsync("srv", CancellationToken.None);
        Assert.True(manager.IsSignedIn("srv"));

        await manager.SignOutAsync("srv", CancellationToken.None);

        Assert.False(manager.IsSignedIn("srv"));
        Assert.Equal(McpServerStatus.Stopped, manager.GetState("srv").Status);
        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task An_expired_stored_token_refreshes_with_its_persisted_registration_and_keeps_the_browser_closed()
    {
        await using var auth = new FakeAuthorizationServer();
        await using var mcp = new FakeMcpHttpServer();
        mcp.Override = request =>
        {
            if (request.Method != "POST")
            {
                return null;
            }

            if (!string.Equals(request.Header("Authorization"), "Bearer " + FakeAuthorizationServer.AccessToken, StringComparison.Ordinal))
            {
                return new HttpReply(401, "", "application/json", ChallengeTo(auth));
            }

            return null;
        };

        var store = new MemoryTokenStore();
        var key = TokenKey(mcp.Endpoint, "mcp");
        await store.SaveAsync(key, new McpTokenSet(
            "stale-access",
            FakeAuthorizationServer.RefreshToken,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "Bearer",
            "mcp",
            "mcp",
            FakeAuthorizationServer.ClientId,
            auth.TokenUri.AbsoluteUri));
        var browser = new FakeOAuthBrowser(DirectHttp.Client);
        await using var manager = new McpServerManager(
            new McpConfig([HttpServer("srv", mcp.Endpoint.AbsoluteUri)], []),
            new McpServerManagerOptions
            {
                HttpClient = DirectHttp.Client,
                TokenStore = store,
                OAuthUi = browser,
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
            });

        await manager.StartAsync("srv", CancellationToken.None);

        Assert.True(manager.GetState("srv").IsReady);
        Assert.Equal(0, auth.CountRequests("POST", "/register"));
        Assert.Equal(0, auth.CountRequests("GET", "/pr"));
        Assert.Equal(1, auth.CountRequests("POST", "/token"));
        Assert.Equal(FakeAuthorizationServer.ClientId, auth.Requests.Last(request => request.Path == "/token").Form.GetValueOrDefault("client_id"));
        Assert.Equal(0, browser.OpenedCount);
        Assert.Equal(key, Assert.Single(store.LoadedKeys));

        // The refreshed token keeps the registration, so the next restart refreshes without registering again.
        var saved = store.Tokens[key];
        Assert.Equal(FakeAuthorizationServer.AccessToken, saved.AccessToken);
        Assert.Equal(FakeAuthorizationServer.ClientId, saved.ClientId);
        Assert.Equal(auth.TokenUri.AbsoluteUri, saved.TokenEndpoint);
    }

    /// <summary>
    /// The token can stop being accepted while the session stays up (it expired, it was revoked, the server rotated its keys).
    /// The request still fails, but the state has to say "sign in again": the settings page shows its sign-in action from that
    /// flag, and without it the only thing the user sees is a red line on a server that claims to be ready.
    /// </summary>
    [Fact]
    public async Task A_401_after_the_handshake_marks_the_server_needing_sign_in()
    {
        await using var auth = new FakeAuthorizationServer();
        await using var mcp = new FakeMcpHttpServer();
        mcp.Override = request =>
        {
            if (request.Method != "POST")
            {
                return null;
            }

            var authorized = string.Equals(
                request.Header("Authorization"),
                "Bearer " + FakeAuthorizationServer.AccessToken,
                StringComparison.Ordinal);
            if (!authorized || request.Body.Contains("\"tools/call\"", StringComparison.Ordinal))
            {
                // The handshake gets through, the tool call does not: the 401 lands mid-session.
                return new HttpReply(401, "", "application/json", ChallengeTo(auth));
            }

            return null;
        };

        var store = new MemoryTokenStore();
        await store.SaveAsync(
            TokenKey(mcp.Endpoint, "mcp"),
            new McpTokenSet(FakeAuthorizationServer.AccessToken, null, null, "Bearer", "mcp", "mcp"));
        await using var manager = new McpServerManager(
            new McpConfig([HttpServer("srv", mcp.Endpoint.AbsoluteUri)], []),
            new McpServerManagerOptions
            {
                HttpClient = DirectHttp.Client,
                TokenStore = store,
                OAuthUi = new FakeOAuthBrowser(DirectHttp.Client),
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
            });

        await manager.StartAsync("srv", CancellationToken.None);
        Assert.True(manager.GetState("srv").IsReady);
        var tool = Assert.Single(manager.Tools);

        var result = await tool.InvokeAsync(
            new ToolCall("call-1", tool.ExposedName, "{}"),
            new ToolContext("session-1", Path.GetTempPath()),
            CancellationToken.None);

        Assert.True(result.IsError);
        var state = manager.GetState("srv");
        Assert.True(state.NeedsSignIn);
        Assert.Equal(McpServerManager.SignInRequiredMessage, state.LastError);
        // The session itself is untouched: the tools are still known, only the credential behind them is not accepted.
        Assert.True(state.IsReady);
        Assert.Single(manager.Tools);
    }

    /// <summary>
    /// Two calls that both run into the same expired access token refresh once, not twice: a rotating refresh token (RFC 6749 §6)
    /// is invalidated by the first refresh, so a second concurrent one is rejected as invalid_grant and the call it belonged to
    /// comes back as "sign in again" although the credential is fine. The token is seeded to expire 31 s out and the tool calls
    /// wait for it to fall inside the refresh window, so the two 401s really are in flight together.
    /// </summary>
    [Fact]
    public async Task Concurrent_401s_refresh_the_token_once_and_both_calls_succeed()
    {
        await using var auth = new FakeAuthorizationServer { RotateRefreshTokens = true, TokenDelay = TimeSpan.FromMilliseconds(300) };
        await using var mcp = new FakeMcpHttpServer();
        mcp.Override = request =>
        {
            if (request.Method != "POST")
            {
                return null;
            }

            var token = request.Header("Authorization");
            // The warm token opens the session; only the tool calls push it past its expiry and need the refreshed one.
            var acceptable = request.Body.Contains("\"tools/call\"", StringComparison.Ordinal)
                ? "Bearer " + FakeAuthorizationServer.AccessToken
                : "Bearer warm-access";
            return string.Equals(token, acceptable, StringComparison.Ordinal)
                ? null
                : new HttpReply(401, "", "application/json", ChallengeTo(auth));
        };

        var store = new MemoryTokenStore();
        await store.SaveAsync(
            TokenKey(mcp.Endpoint, "mcp"),
            new McpTokenSet(
                "warm-access",
                FakeAuthorizationServer.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(31),
                "Bearer",
                "mcp",
                "mcp",
                FakeAuthorizationServer.ClientId,
                auth.TokenUri.AbsoluteUri));
        await using var manager = new McpServerManager(
            new McpConfig([HttpServer("srv", mcp.Endpoint.AbsoluteUri)], []),
            new McpServerManagerOptions
            {
                HttpClient = DirectHttp.Client,
                TokenStore = store,
                OAuthUi = new FakeOAuthBrowser(DirectHttp.Client),
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(10),
            });

        await manager.StartAsync("srv", CancellationToken.None);
        var tool = Assert.Single(manager.Tools);
        // 30 s is the refresh window; the warm token was seeded just outside it.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var results = await Task.WhenAll(
            tool.InvokeAsync(new ToolCall("call-1", tool.ExposedName, "{}"), new ToolContext("session-1", Path.GetTempPath()), CancellationToken.None).AsTask(),
            tool.InvokeAsync(new ToolCall("call-2", tool.ExposedName, "{}"), new ToolContext("session-1", Path.GetTempPath()), CancellationToken.None).AsTask());

        Assert.All(results, result => Assert.False(result.IsError, "both calls must survive the refresh"));
        Assert.Equal(1, auth.CountRequests("POST", "/token"));
        Assert.False(manager.GetState("srv").NeedsSignIn);
    }

    [Fact]
    public async Task A_stdio_server_never_touches_the_oauth_wiring()
    {
        var scripted = new ScriptedServer();
        var transports = new ConcurrentQueue<LoopbackTransport>();
        var store = new MemoryTokenStore();
        var browser = new FakeOAuthBrowser(DirectHttp.Client);
        await using var manager = new McpServerManager(
            new McpConfig([new McpServerConfig { Name = "local", Command = "fake-local", Args = [] }], []),
            new McpServerManagerOptions
            {
                TokenStore = store,
                OAuthUi = browser,
                MaxReconnectAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
                TransportFactory = config =>
                {
                    var transport = new LoopbackTransport(config.Name) { Responder = scripted.Respond };
                    transports.Enqueue(transport);
                    return transport;
                },
            });

        await manager.StartAsync("local", CancellationToken.None);

        Assert.True(manager.GetState("local").IsReady);
        Assert.True(manager.IsOAuthAvailable);
        Assert.Empty(store.LoadedKeys);
        Assert.Equal(0, browser.OpenedCount);
        Assert.False(manager.IsSignedIn("local"));
        Assert.Equal("local", Assert.Single(transports).Name);
    }

    private sealed class MemoryTokenStore : IMcpTokenStore
    {
        private readonly ConcurrentDictionary<string, McpTokenSet> tokens = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> loads = new();

        public IReadOnlyDictionary<string, McpTokenSet> Tokens => tokens;

        public IReadOnlyCollection<string> LoadedKeys => loads.ToArray();

        public ValueTask<McpTokenSet?> LoadAsync(string serverKey, CancellationToken cancellationToken = default)
        {
            loads.Enqueue(serverKey);
            return ValueTask.FromResult(tokens.TryGetValue(serverKey, out var token) ? token : null);
        }

        public ValueTask SaveAsync(string serverKey, McpTokenSet tokens, CancellationToken cancellationToken = default)
        {
            this.tokens[serverKey] = tokens;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string serverKey, CancellationToken cancellationToken = default)
        {
            tokens.TryRemove(serverKey, out _);
            return ValueTask.CompletedTask;
        }
    }
}
