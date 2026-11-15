using System.Net;
using System.Net.Http;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.OAuth;

/// <summary>
/// The attack-shaped paths of the flow, not the happy path: a callback that does not belong to this sign-in, stray loopback
/// traffic that must be answered without aborting the wait, a verifier that does not match the advertised challenge, reused
/// state/PKCE across sign-ins, and an authorization server without dynamic registration.
/// </summary>
public sealed class McpOAuthSecurityTests
{
    private static McpOAuthChallenge ChallengeOf(FakeAuthorizationServer server) =>
        new(server.ProtectedResourceMetadataUri.AbsoluteUri, "mcp");

    [Fact]
    public async Task A_callback_carrying_a_foreign_state_is_rejected_and_mints_no_token()
    {
        await using var server = new FakeAuthorizationServer();
        var browser = new FakeOAuthBrowser(DirectHttp.Client)
        {
            Visitor = (authorizationUri, _) => FollowWithForeignStateAsync(authorizationUri),
        };
        var oauth = new McpOAuth(DirectHttp.Client, browser);

        await Assert.ThrowsAsync<McpOAuthException>(() => oauth.SignInAsync(ChallengeOf(server), CancellationToken.None));
        await browser.Visit;

        Assert.Null(browser.Failure);
        Assert.Equal(1, server.CountRequests("GET", "/authorize"));
        Assert.Equal(0, server.CountRequests("POST", "/token"));
    }

    [Fact]
    public async Task A_rejected_callback_does_not_block_the_next_sign_in()
    {
        await using var server = new FakeAuthorizationServer();
        var browser = new FakeOAuthBrowser(DirectHttp.Client)
        {
            Visitor = (authorizationUri, _) => FollowWithForeignStateAsync(authorizationUri),
        };
        var oauth = new McpOAuth(DirectHttp.Client, browser);
        await Assert.ThrowsAsync<McpOAuthException>(() => oauth.SignInAsync(ChallengeOf(server), CancellationToken.None));
        await browser.Visit;

        browser.Visitor = null;
        var tokens = await oauth.SignInAsync(ChallengeOf(server), CancellationToken.None);

        Assert.Equal(FakeAuthorizationServer.AccessToken, tokens.AccessToken);
        Assert.Equal(1, server.CountRequests("POST", "/token"));
    }

    [Fact]
    public async Task Stray_loopback_requests_are_answered_and_the_wait_continues()
    {
        await using var server = new FakeAuthorizationServer();
        var strayStatus = 0;
        var browser = new FakeOAuthBrowser(DirectHttp.Client)
        {
            Visitor = async (authorizationUri, http) =>
            {
                var favicon = new Uri(CallbackOf(authorizationUri), "/favicon.ico");
                using (var stray = await http.GetAsync(favicon))
                {
                    strayStatus = (int)stray.StatusCode;
                }

                using var redirected = await http.GetAsync(authorizationUri, CancellationToken.None);
            },
        };
        var oauth = new McpOAuth(DirectHttp.Client, browser);

        var tokens = await oauth.SignInAsync(ChallengeOf(server), CancellationToken.None);

        Assert.Null(browser.Failure);
        Assert.Equal((int)HttpStatusCode.BadRequest, strayStatus);
        Assert.Equal(FakeAuthorizationServer.AccessToken, tokens.AccessToken);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task A_callback_without_a_code_aborts_the_sign_in(string method)
    {
        await using var server = new FakeAuthorizationServer();
        var browser = new FakeOAuthBrowser(DirectHttp.Client)
        {
            Visitor = async (authorizationUri, http) =>
            {
                // The loopback path with the right shape but no code: what a mismatched redirect_uri registration produces,
                // and what any local process can send. It must abort the wait instead of holding it open.
                using var request = new HttpRequestMessage(new HttpMethod(method), CallbackOf(authorizationUri));
                using var response = await http.SendAsync(request, CancellationToken.None);
            },
        };
        var oauth = new McpOAuth(DirectHttp.Client, browser);
        using var shortwait = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // The cancellation token is the guard: a client that keeps waiting instead of rejecting raises
        // OperationCanceledException, not McpOAuthException.
        await Assert.ThrowsAsync<McpOAuthException>(() => oauth.SignInAsync(ChallengeOf(server), shortwait.Token));
        await browser.Visit;

        Assert.Null(browser.Failure);
        Assert.Equal(0, server.CountRequests("POST", "/token"));
    }

    [Fact]
    public async Task An_exchange_whose_verifier_does_not_match_the_challenge_is_refused()
    {
        await using var server = new FakeAuthorizationServer();
        var advertised = Pkce.Challenge("the-advertised-verifier");
        var authorizeUrl = server.AuthorizationUri.AbsoluteUri +
                           "?code_challenge=" + Uri.EscapeDataString(advertised) +
                           "&code_challenge_method=S256&state=s&redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:1/callback");
        using (var authorize = await DirectHttp.NoRedirect.GetAsync(authorizeUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        }

        var oauth = new McpOAuth(DirectHttp.Client);
        var exception = await Assert.ThrowsAsync<McpOAuthException>(() => oauth.ExchangeAsync(
            server.TokenUri.AbsoluteUri,
            FakeAuthorizationServer.ClientId,
            FakeAuthorizationServer.IssuedCode,
            "a-different-verifier",
            "http://127.0.0.1:1/callback",
            "mcp",
            CancellationToken.None));

        Assert.Contains("PKCE", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.CountRequests("POST", "/token"));
    }

    [Fact]
    public async Task Every_sign_in_uses_a_fresh_state_and_pkce_challenge()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client, new FakeOAuthBrowser(DirectHttp.Client));

        await oauth.SignInAsync(ChallengeOf(server), CancellationToken.None);
        await oauth.SignInAsync(ChallengeOf(server), CancellationToken.None);

        var authorize = server.Requests.Where(request => request.Method == "GET" && request.Path == "/authorize").ToArray();
        Assert.Equal(2, authorize.Length);
        var states = authorize.Select(request => request.Query.GetValueOrDefault("state")).ToArray();
        var challenges = authorize.Select(request => request.Query.GetValueOrDefault("code_challenge")).ToArray();
        Assert.All(states, state => Assert.False(string.IsNullOrEmpty(state)));
        Assert.NotEqual(states[0], states[1]);
        Assert.NotEqual(challenges[0], challenges[1]);

        var verifiers = server.Requests
            .Where(request => request.Method == "POST" && request.Path == "/token")
            .Select(request => request.Form.GetValueOrDefault("code_verifier"))
            .ToArray();
        Assert.Equal(2, verifiers.Length);
        Assert.NotEqual(verifiers[0], verifiers[1]);
    }

    [Fact]
    public async Task Metadata_discovery_never_sends_the_access_token()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client);

        await oauth.DiscoverAsync(ChallengeOf(server), CancellationToken.None);

        var metadata = server.Requests.Where(request => request.Path is "/pr" or "/as").ToArray();
        Assert.Equal(2, metadata.Length);
        Assert.All(metadata, request => Assert.Null(request.Header("Authorization")));
    }

    [Fact]
    public async Task An_authorization_server_without_a_registration_endpoint_fails_before_the_browser_opens()
    {
        await using var server = new FakeAuthorizationServer { OmitRegistrationEndpoint = true };
        var browser = new FakeOAuthBrowser(DirectHttp.Client);
        var oauth = new McpOAuth(DirectHttp.Client, browser);

        var exception = await Assert.ThrowsAsync<McpOAuthException>(() => oauth.SignInAsync(ChallengeOf(server), CancellationToken.None));

        Assert.Contains("dynamic client registration", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, browser.OpenedCount);
        Assert.Equal(0, server.CountRequests("POST", "/register"));
        Assert.Equal(0, server.CountRequests("POST", "/token"));
    }

    /// <summary>
    /// RFC 9728 §2.1 allows <c>https</c> only (a loopback address aside). Every URL the flow talks to is checked, not just the
    /// one the 401 challenge named: an http metadata URL, an AS metadata document whose token endpoint is http, or an
    /// authorization URL outside the loopback would carry the authorization code and refresh token in cleartext.
    /// </summary>
    [Fact]
    public async Task Discovery_refuses_a_metadata_url_that_is_not_https()
    {
        var oauth = new McpOAuth(DirectHttp.Client);

        var exception = await Assert.ThrowsAsync<McpOAuthException>(() => oauth.DiscoverAsync(
            new McpOAuthChallenge("file:///windows/win.ini", null),
            CancellationToken.None));

        Assert.Contains("https", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fetches the authorization URL without following it, swaps the state for an attacker's, then follows that.</summary>
    private static async Task FollowWithForeignStateAsync(Uri authorizationUri)
    {
        using var authorize = await DirectHttp.NoRedirect.GetAsync(authorizationUri).ConfigureAwait(false);
        var location = authorize.Headers.Location ?? throw new InvalidOperationException("the fake AS did not redirect");
        using var callback = await DirectHttp.NoRedirect.GetAsync(ReplaceState(location.AbsoluteUri)).ConfigureAwait(false);
    }

    private static string ReplaceState(string callbackUri)
    {
        const string marker = "state=";
        var start = callbackUri.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return callbackUri;
        }

        start += marker.Length;
        var end = callbackUri.IndexOf('&', start);
        return callbackUri[..start] + "attacker" + (end < 0 ? string.Empty : callbackUri[end..]);
    }

    /// <summary>The <c>redirect_uri</c> the client put in the authorization URL, i.e. its loopback callback.</summary>
    private static Uri CallbackOf(Uri authorizationUri)
    {
        var query = authorizationUri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals] == "redirect_uri")
            {
                return new Uri(Uri.UnescapeDataString(pair[(equals + 1)..]));
            }
        }

        throw new InvalidOperationException("the authorization URL carried no redirect_uri");
    }
}
