using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The Exa and DeepSeek backends read shapes no shared parser covers: Exa answers REST JSON when a key is present and a
/// <c>Title:/URL:/Highlights:</c> text listing from its MCP endpoint when none is, DeepSeek answers its own JSON with a
/// long <c>summary</c>. Each test pins the mapping, so a vendor format change fails here instead of silently returning
/// nothing to the model.
/// </summary>
public sealed class WebSearchBackendTests
{
    [Fact]
    public async Task ExaWithKeyReadsRestResultsAndSendsTheKey()
    {
        const string Body =
            """{"results":[{"title":"Avalonia <b>12</b>","url":"https://example.com/a","text":"Full text A"},{"title":"B","url":"https://example.com/b","summary":"Summary B"}]}""";
        var handler = new Stub(HttpStatusCode.OK, Body);
        using var http = new HttpClient(handler);
        var backend = new ExaSearchBackend(http, "exa-key");

        var results = await backend.SearchAsync("avalonia", 2, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Avalonia 12", results[0].Title);
        Assert.Equal("https://example.com/a", results[0].Url);
        Assert.Equal("Full text A", results[0].Snippet);
        Assert.Equal("Summary B", results[1].Snippet);
        Assert.Equal(ExaSearchBackend.RestUrl, handler.Uri!.ToString());
        Assert.Equal("exa-key", handler.ApiKey);
    }

    [Fact]
    public async Task ExaWithoutKeyReadsTheMcpTextListing()
    {
        const string Listing = """
            Title: First
            URL: https://example.com/1
            Published: N/A
            Author: N/A
            Highlights:
            line one
            line two

            ---

            Title: Second
            URL: https://example.com/2
            Highlights:
            second body
            """;
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new { content = new[] { new { type = "text", text = Listing } } },
        });
        var handler = new Stub(HttpStatusCode.OK, "event: ping\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/ping\"}\n\nevent: message\ndata: " + payload + "\n\n");
        using var http = new HttpClient(handler);
        var backend = new ExaSearchBackend(http, apiKey: null);

        var results = await backend.SearchAsync("avalonia", 5, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("First", results[0].Title);
        Assert.Equal("https://example.com/1", results[0].Url);
        Assert.Equal("line one line two", results[0].Snippet);
        Assert.Equal("Second", results[1].Title);
        Assert.Equal("second body", results[1].Snippet);
        Assert.Equal(ExaSearchBackend.McpUrl, handler.Uri!.ToString());
        Assert.Null(handler.ApiKey);
        Assert.Contains("web_search_exa", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeekPrefersTheSummaryAndSendsABearerToken()
    {
        const string Body =
            """{"results":[{"title":"T1","url":"https://example.com/1","snippet":"short","summary":"long summary"},{"title":"T2","url":"https://example.com/2","snippet":"only snippet"}]}""";
        var handler = new Stub(HttpStatusCode.OK, Body);
        using var http = new HttpClient(handler);
        var backend = new DeepSeekSearchBackend(http, "sk-deepseek");

        var results = await backend.SearchAsync("avalonia", 3, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("long summary", results[0].Snippet);
        Assert.Equal("only snippet", results[1].Snippet);
        Assert.Equal("Bearer sk-deepseek", handler.Authorization);
        Assert.Contains("\"count\":3", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"need_content\":false", handler.Body, StringComparison.Ordinal);
    }

    /// <summary>Answers one canned response and keeps what the backend sent, so the assertions can check the request too.</summary>
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? ApiKey { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? string.Join(",", values) : null;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
