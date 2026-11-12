using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Tests.Providers;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.Core.Tests.Tools.Web;

public sealed class WebSearchTests
{
    private static readonly ToolContext Context = new("s", ".");

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ToolCall Call(string args) => new("1", WebSearchTool.Name, args);

    [Fact]
    public async Task Brave_sends_token_header_and_parses_results()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json(
            """{"web":{"results":[{"title":"A &amp; B","url":"https://a.test/","description":"the <strong>best</strong> one"},{"title":"C","url":"https://c.test/","description":"x"}]}}"""));
        var results = await new BraveSearchBackend(handler.CreateClient(), "KEY").SearchAsync("hello world&x", 3, default);

        Assert.Equal("KEY", handler.Last.Header("X-Subscription-Token"));
        Assert.Equal("api.search.brave.com", handler.Last.Uri.Host);
        Assert.Equal("/res/v1/web/search", handler.Last.Uri.AbsolutePath);
        Assert.Contains("q=hello%20world%26x", handler.Last.Uri.Query);
        Assert.Contains("count=3", handler.Last.Uri.Query);
        Assert.Equal(2, results.Count);
        Assert.Equal(new WebSearchResult("A & B", "https://a.test/", "the best one"), results[0]);
    }

    [Fact]
    public async Task Brave_without_web_section_is_empty()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json("{}"));
        Assert.Empty(await new BraveSearchBackend(handler.CreateClient(), "K").SearchAsync("q", 5, default));
    }

    [Fact]
    public async Task Tavily_posts_json_with_bearer_key()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json(
            """{"results":[{"title":"T","url":"https://t.test/","content":"snip","score":0.9}]}"""));
        var results = await new TavilySearchBackend(handler.CreateClient(), "TVLY").SearchAsync("forge", 4, default);

        Assert.Equal("Bearer TVLY", handler.Last.Header("Authorization"));
        Assert.Equal("https://api.tavily.com/search", handler.Last.Uri.ToString());
        using var doc = handler.Last.Json();
        Assert.Equal("forge", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("max_results").GetInt32());
        Assert.DoesNotContain("TVLY", handler.Last.Body);
        Assert.Equal(new WebSearchResult("T", "https://t.test/", "snip"), Assert.Single(results));
    }

    [Fact]
    public async Task Searxng_uses_json_format_and_trims_to_count()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json(
            """{"results":[{"title":"1","url":"https://1/","content":"a"},{"title":"2","url":"https://2/","content":"b"},{"title":"3","url":"https://3/","content":"c"}]}"""));
        var results = await new SearxngSearchBackend(handler.CreateClient(), "http://searx.local:8080/").SearchAsync("a b", 2, default);

        Assert.Equal("http://searx.local:8080/search?q=a%20b&format=json", handler.Last.Uri.AbsoluteUri);
        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Backends_throw_a_clean_exception_on_error_status(HttpStatusCode status)
    {
        foreach (var make in new Func<HttpClient, IWebSearchBackend>[]
        {
            c => new BraveSearchBackend(c, "SECRETKEY"),
            c => new TavilySearchBackend(c, "SECRETKEY"),
            c => new SearxngSearchBackend(c, "http://s"),
        })
        {
            var handler = new FakeHttpMessageHandler().EnqueueStatus(status);
            var exception = await Assert.ThrowsAsync<WebSearchException>(() => make(handler.CreateClient()).SearchAsync("q", 5, default));
            Assert.Contains(((int)status).ToString(), exception.Message);
            Assert.DoesNotContain("SECRETKEY", exception.Message);
        }
    }

    [Fact]
    public async Task Invalid_json_response_is_a_search_exception()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Json("<html>"));
        await Assert.ThrowsAsync<WebSearchException>(() => new SearxngSearchBackend(handler.CreateClient(), "http://s").SearchAsync("q", 5, default));
    }

    [Fact]
    public async Task Tool_formats_a_numbered_list_and_defaults_count_to_five()
    {
        var backend = new FakeBackend([new("Title one", "https://one/", "snippet one"), new("Two", "https://two/", "")]);
        var result = await new WebSearchTool(backend).InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);

        Assert.False(result.IsError);
        Assert.Equal("1. Title one — https://one/\n   snippet one\n2. Two — https://two/", result.Content);
        Assert.Equal(5, backend.LastCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 10)]
    [InlineData(7, 7)]
    public async Task Count_is_clamped(int requested, int expected)
    {
        var backend = new FakeBackend([]);
        await new WebSearchTool(backend).InvokeAsync(Call("{\"query\":\"x\",\"count\":" + requested + "}"), Context, default);
        Assert.Equal(expected, backend.LastCount);
    }

    [Fact]
    public async Task Tool_turns_backend_failure_into_an_error_result()
    {
        var backend = new FakeBackend(null) { Failure = new WebSearchException("Brave Search returned HTTP 401.") };
        var result = await new WebSearchTool(backend).InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("401", result.Content);
    }

    [Fact]
    public async Task Empty_query_is_an_error_without_calling_the_backend()
    {
        var backend = new FakeBackend([]);
        var result = await new WebSearchTool(backend).InvokeAsync(Call("{\"query\":\"  \"}"), Context, default);
        Assert.True(result.IsError);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Backend_factory_is_read_on_every_call()
    {
        FakeBackend? current = null;
        var tool = new WebSearchTool(() => current);

        var unconfigured = await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.True(unconfigured.IsError);
        Assert.Contains(WebSearchTool.NotConfiguredMessage, unconfigured.Content);

        var first = new FakeBackend([new("One", "https://one/", "")]);
        current = first;
        var result = await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.False(result.IsError);
        Assert.Equal(1, first.Calls);

        var second = new FakeBackend([new("Two", "https://two/", "")]);
        current = second;
        result = await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.Contains("https://two/", result.Content);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);

        current = null;
        Assert.True((await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default)).IsError);
    }

    [Fact]
    public async Task Not_configured_result_tells_the_model_to_ask_the_user()
    {
        var calls = 0;
        var tool = new WebSearchTool(() =>
        {
            calls++;
            return null;
        });
        var result = await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("no search backend is configured", result.Content);
        Assert.Equal(1, calls);
        Assert.Contains("no search backend is configured", tool.Definition.Description);
    }

    [Fact]
    public async Task Tool_timeout_is_an_error_but_caller_cancellation_propagates()
    {
        var slow = new FakeBackend([]) { Hang = true };
        var tool = new WebSearchTool(slow, TimeSpan.FromMilliseconds(50));
        var result = await tool.InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new WebSearchTool(slow).InvokeAsync(Call("{\"query\":\"x\"}"), Context, cts.Token));
    }

    [Fact]
    public async Task No_results_message()
    {
        var result = await new WebSearchTool(new FakeBackend([])).InvokeAsync(Call("{\"query\":\"x\"}"), Context, default);
        Assert.Equal("No results.", result.Content);
    }

    private sealed class FakeBackend(IReadOnlyList<WebSearchResult>? results) : IWebSearchBackend
    {
        public string Id => "fake";

        public int LastCount { get; private set; }

        public int Calls { get; private set; }

        public Exception? Failure { get; init; }

        public bool Hang { get; init; }

        public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Calls++;
            LastCount = count;
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Failure is null ? results! : throw Failure;
        }
    }
}
