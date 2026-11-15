using System.Security.Cryptography;
using System.Text;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.OAuth;

public sealed class McpOAuthFlowTests
{
    [Fact]
    public void ParseChallengeParameter_reads_resource_metadata_and_resource()
    {
        var challenge = McpOAuth.ParseChallengeParameter("resource_metadata=\"https://as/pr\", resource=\"mcp://thing\"");

        Assert.NotNull(challenge);
        Assert.Equal("https://as/pr", challenge.ResourceMetadataUrl);
        Assert.Equal("mcp://thing", challenge.Resource);
        Assert.False(challenge.IsEmpty);
    }

    [Fact]
    public void ParseChallengeParameter_ignores_unknown_fields_and_returns_null_without_metadata()
    {
        Assert.Null(McpOAuth.ParseChallengeParameter("scope=\"mcp\", error=\"x\""));
        Assert.Null(McpOAuth.ParseChallengeParameter(null));
    }

    [Fact]
    public async Task DiscoverAsync_reads_both_metadata_documents()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client);

        var discovery = await oauth.DiscoverAsync(
            new McpOAuthChallenge(server.ProtectedResourceMetadataUri.AbsoluteUri, null),
            CancellationToken.None);

        Assert.Equal(server.AuthorizationUri.AbsoluteUri, discovery.AuthorizationEndpoint);
        Assert.Equal(server.TokenUri.AbsoluteUri, discovery.TokenEndpoint);
        Assert.Equal(server.RegistrationUri.AbsoluteUri, discovery.RegistrationEndpoint);
        Assert.Equal("mcp", discovery.Resource);
    }

    [Fact]
    public async Task SignInAsync_runs_registration_pkce_loopback_and_exchange()
    {
        await using var server = new FakeAuthorizationServer();
        var ui = new FakeOAuthBrowser(DirectHttp.Client);
        var oauth = new McpOAuth(DirectHttp.Client, ui);

        var tokens = await oauth.SignInAsync(
            new McpOAuthChallenge(server.ProtectedResourceMetadataUri.AbsoluteUri, "mcp"),
            CancellationToken.None);

        Assert.Equal(FakeAuthorizationServer.AccessToken, tokens.AccessToken);
        Assert.Equal(FakeAuthorizationServer.RefreshToken, tokens.RefreshToken);
        Assert.Equal("mcp", tokens.Resource);
        Assert.Equal(1, server.CountRequests("POST", "/register"));
        Assert.Equal(1, server.CountRequests("POST", "/token"));
        // The flow sent a verifier whose S256 challenge is exactly what the authorize step advertised.
        Assert.NotNull(server.LastCodeVerifier);
        Assert.NotNull(server.LastCodeChallenge);
        Assert.Equal(server.LastCodeChallenge, ChallengeOf(server.LastCodeVerifier!));
    }

    [Fact]
    public async Task RefreshAsync_mints_a_new_access_token()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client);
        var challenge = new McpOAuthChallenge(server.ProtectedResourceMetadataUri.AbsoluteUri, "mcp");

        var refreshed = await oauth.RefreshAsync(
            new McpTokenSet("stale", FakeAuthorizationServer.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(-1), "Bearer", "mcp", "mcp"),
            challenge,
            CancellationToken.None);

        Assert.Equal(FakeAuthorizationServer.AccessToken, refreshed.AccessToken);
        Assert.Equal(FakeAuthorizationServer.RefreshToken, refreshed.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_rejects_a_revoked_refresh_token()
    {
        await using var server = new FakeAuthorizationServer { RejectRefreshToken = "revoked" };
        var oauth = new McpOAuth(DirectHttp.Client);
        var challenge = new McpOAuthChallenge(server.ProtectedResourceMetadataUri.AbsoluteUri, "mcp");

        await Assert.ThrowsAsync<McpOAuthException>(() => oauth.RefreshAsync(
            new McpTokenSet("stale", "revoked", null, "Bearer", "mcp", "mcp"),
            challenge,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeAsync_observes_cancellation()
    {
        await using var server = new FakeAuthorizationServer { TokenDelay = TimeSpan.FromSeconds(5) };
        var oauth = new McpOAuth(DirectHttp.Client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oauth.ExchangeAsync(
            server.TokenUri.AbsoluteUri,
            FakeAuthorizationServer.ClientId,
            FakeAuthorizationServer.IssuedCode,
            "any-verifier",
            "http://127.0.0.1:1/callback",
            "mcp",
            cts.Token));
    }

    private static string ChallengeOf(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
