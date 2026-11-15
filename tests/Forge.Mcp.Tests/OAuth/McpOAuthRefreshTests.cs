using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.OAuth;

/// <summary>
/// The refresh path after a restart: the tokens carry the RFC 7591 registration they were issued to, so a refresh reuses that
/// client instead of registering a second one, a legacy token without it still re-registers, and a rotating refresh token
/// replaces the stored one for good.
/// </summary>
public sealed class McpOAuthRefreshTests
{
    private static McpOAuthChallenge ChallengeOf(FakeAuthorizationServer server) =>
        new(server.ProtectedResourceMetadataUri.AbsoluteUri, "mcp");

    [Fact]
    public async Task Refreshing_with_the_persisted_registration_skips_discovery_and_registration()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client);
        var stored = new McpTokenSet(
            "stale",
            FakeAuthorizationServer.RefreshToken,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "Bearer",
            "mcp",
            "mcp",
            FakeAuthorizationServer.ClientId,
            server.TokenUri.AbsoluteUri);

        var refreshed = await oauth.RefreshAsync(stored, ChallengeOf(server), CancellationToken.None);

        Assert.Equal(0, server.CountRequests("POST", "/register"));
        Assert.Equal(0, server.CountRequests("GET", "/pr"));
        Assert.Equal(0, server.CountRequests("GET", "/as"));
        Assert.Equal(FakeAuthorizationServer.ClientId, server.Requests.Single(request => request.Path == "/token").Form.GetValueOrDefault("client_id"));
        Assert.Equal(FakeAuthorizationServer.AccessToken, refreshed.AccessToken);
        // The refreshed token keeps the registration, so a second restart refreshes without registering either.
        Assert.Equal(FakeAuthorizationServer.ClientId, refreshed.ClientId);
        Assert.Equal(server.TokenUri.AbsoluteUri, refreshed.TokenEndpoint);
    }

    [Fact]
    public async Task A_token_without_a_persisted_registration_registers_once_and_keeps_it()
    {
        await using var server = new FakeAuthorizationServer();
        var oauth = new McpOAuth(DirectHttp.Client);
        var legacy = new McpTokenSet("stale", FakeAuthorizationServer.RefreshToken, null, "Bearer", "mcp", "mcp");

        var refreshed = await oauth.RefreshAsync(legacy, ChallengeOf(server), CancellationToken.None);

        Assert.Equal(1, server.CountRequests("POST", "/register"));
        Assert.Equal(1, server.CountRequests("GET", "/pr"));
        Assert.Equal(FakeAuthorizationServer.ClientId, refreshed.ClientId);
        Assert.Equal(server.TokenUri.AbsoluteUri, refreshed.TokenEndpoint);
    }

    [Fact]
    public async Task A_rotated_refresh_token_replaces_the_stored_one_and_the_old_one_is_dead()
    {
        await using var server = new FakeAuthorizationServer { RotateRefreshTokens = true };
        var oauth = new McpOAuth(DirectHttp.Client);
        var challenge = ChallengeOf(server);
        var first = new McpTokenSet(
            "stale",
            FakeAuthorizationServer.RefreshToken,
            null,
            "Bearer",
            "mcp",
            "mcp",
            FakeAuthorizationServer.ClientId,
            server.TokenUri.AbsoluteUri);

        var second = await oauth.RefreshAsync(first, challenge, CancellationToken.None);

        Assert.NotEqual(FakeAuthorizationServer.RefreshToken, second.RefreshToken);
        Assert.Equal(server.TokenUri.AbsoluteUri, second.TokenEndpoint);

        // Replaying the rotated-away grant is refused, and the client surfaces it instead of looping.
        await Assert.ThrowsAsync<McpOAuthException>(() => oauth.RefreshAsync(first, challenge, CancellationToken.None));
        Assert.Equal(0, server.CountRequests("POST", "/register"));

        // The token the client handed back is the live one.
        var third = await oauth.RefreshAsync(second, challenge, CancellationToken.None);
        Assert.NotEqual(second.RefreshToken, third.RefreshToken);
        Assert.Equal(FakeAuthorizationServer.AccessToken, third.AccessToken);
    }
}
