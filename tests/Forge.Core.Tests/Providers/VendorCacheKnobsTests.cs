using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins what each vendor's cache knob looks like on the wire, because caching strategies are not portable: OpenAI takes a routing
/// key and a retention, xAI a conversation id, and a vendor that documents nothing must not be sent either.
/// </summary>
[Collection("anthropic-wire")]
public sealed class VendorCacheKnobsTests
{
    private static string Done() => TestData.Sse(
        """{"id":"c","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}""",
        """{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        "[DONE]");

    private static ChatRequest Request(string? ttl) => TestData.Request(cache: new CacheHint(true, null, ttl)) with
    {
        CacheRoutingKey = "lineage-root",
        SessionId = "session-7",
    };

    private static async Task<FakeHttpMessageHandler> SendAsync(
        ProviderOptions options,
        string? ttl,
        string baseUrl = "https://api.openai.com/v1",
        Func<FakeHttpMessageHandler, FakeHttpMessageHandler>? prepare = null)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Done());
        prepare?.Invoke(handler);
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(baseUrl),
            handler.CreateClient(),
            options: options);
        await provider.CollectAsync(Request(ttl));
        return handler;
    }

    [Fact]
    public async Task OpenAis_retention_follows_the_lifetime_policy()
    {
        var shortLived = await SendAsync(new ProviderOptions { SendPromptCacheRetention = true }, "5m");
        Assert.Equal("in_memory", shortLived.Last.Root.GetProperty("prompt_cache_retention").GetString());

        var longLived = await SendAsync(new ProviderOptions { SendPromptCacheRetention = true }, "1h");
        Assert.Equal("24h", longLived.Last.Root.GetProperty("prompt_cache_retention").GetString());

        var automatic = await SendAsync(new ProviderOptions { SendPromptCacheRetention = true }, null);
        Assert.Equal("24h", automatic.Last.Root.GetProperty("prompt_cache_retention").GetString());
    }

    [Fact]
    public async Task A_vendor_without_the_knobs_is_never_sent_them()
    {
        var plain = await SendAsync(ProviderOptions.Default, "1h");

        Assert.False(plain.Last.Root.TryGetProperty("prompt_cache_retention", out _));
        Assert.False(plain.Last.Root.TryGetProperty("prompt_cache_key", out _));
        Assert.Null(plain.Last.Header("x-grok-conv-id"));
    }

    [Fact]
    public async Task Xai_gets_the_conversation_id_header_and_nothing_else()
    {
        var handler = await SendAsync(
            new ProviderOptions { SendConversationCacheHeader = true },
            "1h",
            "https://api.x.ai/v1");

        // The routing key (the lineage root), not the session id: a handover or fork keeps hitting the same cache entries.
        Assert.Equal("lineage-root", handler.Last.Header("x-grok-conv-id"));
        Assert.False(handler.Last.Root.TryGetProperty("prompt_cache_retention", out _));
    }

    [Fact]
    public async Task Copilot_gets_the_interaction_id_its_cache_is_keyed_on()
    {
        // Its API keys the prompt cache on this header; leaving it empty is why other harnesses see almost no cache hits on the Claude
        // models Copilot serves. The value is the conversation, so a handover keeps hitting the same entries.
        var handler = await SendAsync(
            new ProviderOptions { SendInteractionId = true },
            "1h",
            "https://api.individual.githubcopilot.com");

        Assert.Equal("lineage-root", handler.Last.Header("X-Interaction-Id"));
        // The integration id is a different thing (quota bucket, model allowlist) and the adapter already sends it.
        Assert.False(handler.Last.Root.TryGetProperty("prompt_cache_retention", out _));
    }

    [Fact]
    public async Task The_cache_headers_are_never_sent_after_a_downgrade()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"prompt_cache_key is not supported"}}"""),
            })
            .EnqueueSse(Done());
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig("https://api.x.ai/v1"),
            handler.CreateClient(),
            options: new ProviderOptions { SendConversationCacheHeader = true, SendPromptCacheKey = true });

        await provider.CollectAsync(Request("1h"));

        Assert.Equal("lineage-root", handler.Requests[0].Header("x-grok-conv-id"));
        Assert.Null(handler.Requests[1].Header("x-grok-conv-id"));
        Assert.False(handler.Requests[1].Root.TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task DeepSeek_is_left_out_whatever_the_options_say()
    {
        var handler = await SendAsync(
            new ProviderOptions { SendPromptCacheKey = true, SendPromptCacheRetention = true, SendConversationCacheHeader = true },
            "1h",
            "https://api.deepseek.com/v1");

        Assert.False(handler.Last.Root.TryGetProperty("prompt_cache_key", out _));
        Assert.False(handler.Last.Root.TryGetProperty("prompt_cache_retention", out _));
        Assert.Null(handler.Last.Header("x-grok-conv-id"));
    }

    [Fact]
    public async Task A_model_that_refuses_the_cache_fields_is_resent_without_them()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Unrecognized request argument supplied: prompt_cache_retention"}}"""),
            })
            .EnqueueSse(Done());
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(),
            handler.CreateClient(),
            options: new ProviderOptions { SendPromptCacheRetention = true, SendPromptCacheKey = true });

        var events = await provider.CollectAsync(Request("1h"));

        Assert.Equal(2, handler.Calls);
        Assert.False(handler.Requests[1].Root.TryGetProperty("prompt_cache_retention", out _));
        Assert.False(handler.Requests[1].Root.TryGetProperty("prompt_cache_key", out _));
        Assert.False(string.IsNullOrEmpty(events.Completed().Notice));
    }
}
