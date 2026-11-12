using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Per-provider request headers: sent with every turn, the configured value replacing a built-in one, and a
/// <c>{session}</c> placeholder filled with the turn's session id (OpenCode Zen/Go's <c>x-opencode-session</c>).
/// </summary>
public class ProviderHeadersTests
{
    private static ProviderConfig Config(params (string Name, string Value)[] headers) =>
        TestData.OpenAiConfig() with
        {
            Headers = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public async Task Session_placeholder_is_filled_with_the_turns_session_id()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(
            Config(("x-opencode-session", "{session}"), ("x-trace", "forge-{session}-1"), ("x-static", "fixed")),
            handler.CreateClient());

        await provider.CollectAsync(TestData.Request() with { SessionId = "s-42" });

        Assert.Equal("s-42", handler.Last.Header("x-opencode-session"));
        Assert.Equal("forge-s-42-1", handler.Last.Header("x-trace"));
        Assert.Equal("fixed", handler.Last.Header("x-static"));
    }

    [Fact]
    public async Task A_header_needing_a_session_is_left_out_without_one_and_the_rest_still_go()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(
            Config(("x-opencode-session", "{session}"), ("x-static", "fixed")),
            handler.CreateClient());

        await provider.CollectAsync(TestData.Request());

        Assert.Null(handler.Last.Header("x-opencode-session"));
        Assert.Equal("fixed", handler.Last.Header("x-static"));
    }

    [Fact]
    public async Task A_configured_header_replaces_the_built_in_one_of_the_same_name()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(Config(("Authorization", "Token gateway")), handler.CreateClient());

        await provider.CollectAsync(TestData.Request());

        Assert.Equal("Token gateway", handler.Last.Header("Authorization"));
    }

    [Theory]
    [InlineData("plain", null, "plain")]
    [InlineData("plain", "s1", "plain")]
    [InlineData("{session}", "s1", "s1")]
    [InlineData("{session}", null, null)]
    [InlineData("{session}", "", null)]
    [InlineData("{session}/{session}", "s1", "s1/s1")]
    [InlineData("{Session}", null, "{Session}")]
    public void Expand_fills_or_drops_the_placeholder(string value, string? session, string? expected) =>
        Assert.Equal(expected, ProviderHeaders.Expand(value, session));

    [Fact]
    public async Task Model_listing_sends_static_headers_but_no_session_header()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(System.Net.HttpStatusCode.OK, """{"data":[{"id":"m1"}]}""");
        var headers = new Dictionary<string, string>
        {
            ["x-opencode-session"] = "{session}",
            ["x-static"] = "fixed",
        };

        var list = await ProviderModelListing.FetchAsync("https://api.example.test/v1", headers, handler.CreateClient(), default);

        Assert.Equal(["m1"], list.Ids);
        Assert.Null(handler.Last.Header("x-opencode-session"));
        Assert.Equal("fixed", handler.Last.Header("x-static"));
    }
}
