using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Http;

public sealed class StreamableHttpTransportOAuthTests
{
    private const string Challenge = "Bearer resource_metadata=\"http://auth.example/pr\", resource=\"mcp\"";

    private static StreamableHttpTransport Transport(
        FakeMcpHttpServer server,
        string? bearerToken = null,
        Func<McpOAuthChallenge, CancellationToken, ValueTask<string?>>? provider = null) =>
        new(new McpHttpOptions
        {
            Endpoint = server.Endpoint,
            HttpClient = DirectHttp.Client,
            BearerToken = bearerToken,
            OAuthTokenProvider = provider,
        });

    [Fact]
    public async Task Bearer_token_is_injected_on_every_request()
    {
        await using var server = new FakeMcpHttpServer();
        await using var client = new McpClient(Transport(server, bearerToken: "tok"), Clients.DefaultOptions());

        await client.ConnectAsync(CancellationToken.None);
        await client.ListToolsAsync(CancellationToken.None);

        Assert.All(server.Requests.Where(request => request.Method == "POST"), request => Assert.Equal("Bearer tok", request.Header("Authorization")));
    }

    [Fact]
    public async Task A_401_with_a_challenge_retries_once_with_the_provided_token()
    {
        await using var server = new FakeMcpHttpServer();
        var challenges = new List<McpOAuthChallenge>();
        server.Override = request =>
        {
            if (request.Method == "POST" && request.Header("Authorization") is null)
            {
                return new HttpReply(401, "", "application/json", new Dictionary<string, string> { ["WWW-Authenticate"] = Challenge });
            }

            return null;
        };

        var transport = Transport(server, provider: (challenge, _) =>
        {
            challenges.Add(challenge);
            return ValueTask.FromResult<string?>("fresh");
        });
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        await client.ConnectAsync(CancellationToken.None);

        Assert.Single(challenges);
        Assert.Equal("http://auth.example/pr", challenges[0].ResourceMetadataUrl);
        Assert.Equal("mcp", challenges[0].Resource);
        var initialize = server.Requests.Where(request => request.Method == "POST" && request.Body.Contains("\"initialize\"", StringComparison.Ordinal))
            .First(request => request.Header("Authorization") is not null);
        Assert.Equal("Bearer fresh", initialize.Header("Authorization"));
    }

    [Fact]
    public async Task A_401_with_no_token_available_raises_oauth_required()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "POST"
            ? new HttpReply(401, "", "application/json", new Dictionary<string, string> { ["WWW-Authenticate"] = Challenge })
            : null;

        var transport = Transport(server, provider: (_, _) => ValueTask.FromResult<string?>(null));
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        var exception = await Assert.ThrowsAsync<McpOAuthRequiredException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.NotNull(exception.Challenge);
        Assert.Equal("mcp", exception.Challenge.Resource);
    }

    [Fact]
    public async Task A_plain_401_without_a_challenge_stays_a_transport_error()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "POST" ? new HttpReply(401, "nope") : null;
        await using var client = new McpClient(Transport(server), Clients.DefaultOptions());

        await Assert.ThrowsAsync<McpTransportException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_second_401_after_the_provider_token_stops_after_one_retry()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "POST"
            ? new HttpReply(401, "", "application/json", new Dictionary<string, string> { ["WWW-Authenticate"] = Challenge })
            : null;
        var provided = 0;
        var transport = Transport(server, provider: (_, _) =>
        {
            Interlocked.Increment(ref provided);
            return ValueTask.FromResult<string?>("fresh");
        });
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        await Assert.ThrowsAsync<McpOAuthRequiredException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(1, provided);
        // The original POST plus exactly one retry: a server that keeps 401ing must not become a request loop.
        Assert.Equal(2, server.CountRequests("POST", McpProtocol.Initialize));
    }

    /// <summary>
    /// The GET server stream retries a 401 whose challenge yields a token at most once, exactly like a POST does: the retry is
    /// counted as a stream failure and waits out the backoff. Without that bound, a server that answers the stream GET 401 for
    /// any reason other than a missing token — while the manager keeps handing back the same cached access token — turns the
    /// stream loop into an unbounded request storm (measured at 728 requests in 300 ms).
    /// </summary>
    [Fact]
    public async Task The_server_stream_retries_a_401_at_most_once()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "GET"
            ? new HttpReply(401, "", "application/json", new Dictionary<string, string> { ["WWW-Authenticate"] = Challenge })
            : null;
        var transport = Transport(server, provider: (_, _) => ValueTask.FromResult<string?>("fresh"));
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        await client.ConnectAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.InRange(server.Requests.Count(request => request.Method == "GET"), 1, 2);
    }
}
