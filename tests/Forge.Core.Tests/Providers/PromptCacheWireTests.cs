using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>Section 8.3.3, 8.3.4 and 8.3.6 on the wire: prompt_cache_key, DeepSeek usage, the TTL field.</summary>
public class PromptCacheWireTests
{
    private const string DeepSeekUrl = "https://api.deepseek.com/v1";

    private static string Done(string usageJson) => TestData.Sse(
        """{"id":"c","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}""",
        """{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        $$"""{"id":"c","choices":[],"usage":{{usageJson}}}""",
        "[DONE]");

    private static HttpClient Unbounded(FakeHttpMessageHandler handler)
    {
        var client = handler.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static ChatRequest Keyed(string? key) => TestData.Request() with { CacheRoutingKey = key };

    private static async Task<RecordedRequest> SendAsync(IChatProvider provider, FakeHttpMessageHandler handler, ChatRequest request)
    {
        await provider.CollectAsync(request);
        return handler.Last;
    }

    [Fact]
    public async Task Prompt_cache_key_is_off_by_default()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Done("{}"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var sent = await SendAsync(provider, handler, Keyed("lineage-1"));

        Assert.False(sent.Root.TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task Prompt_cache_key_carries_the_routing_key_when_enabled()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Done("{}")).EnqueueSse(Done("{}"));
        var provider = ProviderFactory.Create(
            TestData.OpenAiConfig(),
            Unbounded(handler),
            new ProviderOptions { SendPromptCacheKey = true });

        var keyed = await SendAsync(provider, handler, Keyed("lineage-1"));
        Assert.Equal("lineage-1", keyed.Root.GetProperty("prompt_cache_key").GetString());

        var unkeyed = await SendAsync(provider, handler, Keyed(null));
        Assert.False(unkeyed.Root.TryGetProperty("prompt_cache_key", out _));
    }

    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://API.DeepSeek.com/beta")]
    public async Task DeepSeek_never_gets_a_prompt_cache_key(string baseUrl)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Done("{}"));
        var provider = ProviderFactory.Create(
            TestData.OpenAiConfig(baseUrl),
            Unbounded(handler),
            new ProviderOptions { SendPromptCacheKey = true });

        var sent = await SendAsync(provider, handler, Keyed("lineage-1"));

        Assert.False(sent.Root.TryGetProperty("prompt_cache_key", out _));
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1", true)]
    [InlineData("https://deepseek.internal.example/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void DeepSeek_is_recognized_by_host(string? baseUrl, bool expected) =>
        Assert.Equal(expected, OpenAiCompatibleProvider.IsDeepSeek(baseUrl));

    [Theory]
    // DeepSeek alone: hit = cache read, hit + miss = input (input includes cached, like prompt_tokens).
    [InlineData("""{"completion_tokens":7,"prompt_cache_hit_tokens":300,"prompt_cache_miss_tokens":40}""", 340, 7, 300)]
    [InlineData("""{"prompt_tokens":340,"completion_tokens":7,"prompt_cache_hit_tokens":300,"prompt_cache_miss_tokens":40}""", 340, 7, 300)]
    [InlineData("""{"completion_tokens":7,"prompt_cache_miss_tokens":40}""", 40, 7, 0)]
    // cached_tokens present as well: it wins, wherever it is.
    [InlineData("""{"prompt_tokens":340,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":256},"prompt_cache_hit_tokens":300,"prompt_cache_miss_tokens":40}""", 340, 7, 256)]
    [InlineData("""{"prompt_tokens":340,"completion_tokens":7,"cached_tokens":128,"prompt_cache_hit_tokens":300}""", 340, 7, 128)]
    [InlineData("""{"prompt_tokens":340,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":0},"prompt_cache_hit_tokens":300}""", 340, 7, 0)]
    // Plain OpenAI shape is unchanged.
    [InlineData("""{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":64}}""", 100, 20, 64)]
    [InlineData("""{"prompt_tokens":100,"completion_tokens":20}""", 100, 20, 0)]
    public async Task Usage_maps_deepseek_cache_fields(string usage, int input, int output, int cacheRead)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Done(usage));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(DeepSeekUrl), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal(new TokenUsage(input, output, cacheRead, 0), events.Completed().Usage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1h")]
    public async Task Anthropic_sends_no_ttl_field_until_the_ttl_ordering_question_is_decided(string? ttl)
    {
        // Pinned on purpose: section 8.3.6 asks for "1h" on the message breakpoint only, but the prompt caching docs
        // require longer-TTL entries to come before shorter ones (tools -> system -> messages), so the provider keeps
        // sending the 5-minute default everywhere until the design is settled.
        var (provider, handler) = TestData.AnthropicProvider();
        var request = TestData.Request(
            tools: [TestData.ReadFileTool()],
            messages: [ChatMessage.User("a"), ChatMessage.Assistant("b"), ChatMessage.User("c")],
            cache: new CacheHint(CacheSystemAndTools: true, CacheThroughMessageIndex: 1, MessageTtl: ttl));

        await provider.CollectAsync(request);

        Assert.DoesNotContain("\"ttl\"", handler.Last.Body, StringComparison.Ordinal);
        Assert.Null(handler.Last.Header("anthropic-beta"));
    }
}
