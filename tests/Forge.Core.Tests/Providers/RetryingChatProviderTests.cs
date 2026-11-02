using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class RetryingChatProviderTests
{
    private static RetryingChatProvider Wrap(
        IChatProvider inner,
        List<TimeSpan> delays,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null) =>
        new(
            inner,
            maxAttempts: maxAttempts,
            baseDelay: baseDelay ?? TimeSpan.FromMilliseconds(100),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            jitter: delay => delay);

    private static string Completion =>
        TestData.Sse("""{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}""", "[DONE]");

    [Fact]
    public async Task Retries_retryable_status_before_any_event()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down")
            .EnqueueSse(Completion);

        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays);

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal(2, handler.Calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100) }, delays);
        Assert.Equal("ok", events.Text());
    }

    [Fact]
    public async Task Honors_retry_after_header()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromSeconds(4))
            .EnqueueSse(Completion);

        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays);

        await provider.CollectAsync(TestData.Request());

        Assert.Equal(new[] { TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError, "boom")
            .EnqueueStatus(HttpStatusCode.InternalServerError, "boom");

        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays, maxAttempts: 2);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.True(exception.Retryable);
        Assert.Equal(2, handler.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Does_not_retry_non_retryable_failure()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest, "nope");

        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays);

        await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Does_not_retry_once_events_reached_the_caller()
    {
        var firstEvent = TestData.Sse("""{"choices":[{"index":0,"delta":{"content":"partial"}}]}""");
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => FakeHttpMessageHandler.StreamResponse(new FaultingStream(Encoding.UTF8.GetBytes(firstEvent))))
            .EnqueueSse(Completion);

        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays);

        var observed = new List<ChatStreamEvent>();
        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var evt in provider.StreamAsync(TestData.Request(), CancellationToken.None))
            {
                observed.Add(evt);
            }
        });

        var delta = Assert.IsType<TextDelta>(Assert.Single(observed));
        Assert.Equal("partial", delta.Text);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Stops_when_cancelled_during_backoff()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down");
        using var cts = new CancellationTokenSource();

        var provider = new RetryingChatProvider(
            new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()),
            maxAttempts: 3,
            delayAsync: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CollectAsync(TestData.Request(), cts.Token));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Streams_untouched_when_first_attempt_succeeds()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Completion);
        var inner = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var delays = new List<TimeSpan>();
        var provider = Wrap(inner, delays);

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal(inner.Id, provider.Id);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delays);
        Assert.Equal("ok", events.Text());
    }

    [Fact]
    public async Task Retries_transport_failures()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Completion);
        var delays = new List<TimeSpan>();
        var provider = Wrap(new FailingProvider(times: 1), delays);

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Single(delays);
        Assert.Equal("recovered", events.Text());
    }

    private sealed class FailingProvider(int times) : IChatProvider
    {
        private int _remaining = times;

        public string Id => "flaky";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (_remaining-- > 0)
            {
                throw new ProviderException("connection reset", retryable: true);
            }

            await Task.Yield();
            yield return new TextDelta("recovered");
            yield return new MessageCompleted(ChatMessage.Assistant("recovered"), TokenUsage.Empty, StopReason.EndTurn);
        }
    }
}
