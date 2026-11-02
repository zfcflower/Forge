using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class ProviderTimeoutRegressionTests
{
    [Theory]
    [InlineData(ProviderFactory.OpenAiCompatibleKind)]
    [InlineData(ProviderFactory.AnthropicKind)]
    public void Factory_rejects_an_http_client_with_a_finite_timeout(string kind)
    {
        using var http = new HttpClient(new FakeHttpMessageHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        var config = new ProviderConfig("p", kind, "https://example.test", "key");

        var exception = Assert.Throws<ArgumentException>(() => ProviderFactory.Create(config, http));

        Assert.Equal("httpClient", exception.ParamName);
        Assert.Contains("ProviderHttpClient", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_accepts_the_provider_http_client_for_both_kinds()
    {
        using var http = ProviderHttpClient.Create(new FakeHttpMessageHandler());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        Assert.IsType<OpenAiCompatibleProvider>(ProviderFactory.Create(TestData.OpenAiConfig(), http));
        Assert.IsType<AnthropicProvider>(ProviderFactory.Create(TestData.AnthropicConfig(), http));
    }

    [Fact]
    public async Task Stalled_stream_before_any_event_surfaces_as_a_retryable_provider_exception()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ScriptedStream([], hangWhenExhausted: true)));
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(),
            handler.CreateClient(),
            idleTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.True(exception.Retryable);
        Assert.Contains("idle timeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stalled_stream_after_events_surfaces_as_a_retryable_provider_exception()
    {
        var first = System.Text.Encoding.UTF8.GetBytes(string.Concat(
            TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}""")));
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ScriptedStream([first], hangWhenExhausted: true)));
        var provider = new AnthropicProvider(
            TestData.AnthropicConfig(),
            handler.CreateClient(),
            idleTimeout: TimeSpan.FromMilliseconds(100));

        var observed = new List<ChatStreamEvent>();
        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var evt in provider.StreamAsync(TestData.Request(), CancellationToken.None))
            {
                observed.Add(evt);
            }
        });

        Assert.True(exception.Retryable);
        Assert.Equal("partial", Assert.IsType<TextDelta>(Assert.Single(observed)).Text);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_stall_stays_operation_canceled()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ScriptedStream([], hangWhenExhausted: true)));
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(),
            handler.CreateClient(),
            idleTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CollectAsync(TestData.Request(), cts.Token));
    }

    [Fact]
    public async Task Slow_but_steady_stream_survives_longer_than_the_idle_timeout()
    {
        var sse = TestData.Sse(
            """{"choices":[{"index":0,"delta":{"content":"steady "}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"output"},"finish_reason":"stop"}]}""",
            "[DONE]");
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            ScriptedStream.FromText(sse, chunkSize: 8, delay: TimeSpan.FromMilliseconds(20))));
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(),
            handler.CreateClient(),
            idleTimeout: TimeSpan.FromMilliseconds(300));

        var started = DateTimeOffset.UtcNow;
        var events = await provider.CollectAsync(TestData.Request());

        Assert.True(DateTimeOffset.UtcNow - started > TimeSpan.FromMilliseconds(300));
        Assert.Equal("steady output", events.Text());
        Assert.Equal(StopReason.EndTurn, events.Completed().Stop);
    }

    [Fact]
    public void Idle_timeout_rejects_zero_and_negative_values()
    {
        var handler = new FakeHttpMessageHandler();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AnthropicProvider(
            TestData.AnthropicConfig(), handler.CreateClient(), idleTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiCompatibleProvider(
            TestData.OpenAiConfig(), handler.CreateClient(), idleTimeout: TimeSpan.FromSeconds(-1)));
    }
}
