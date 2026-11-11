using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class RetryingChatProviderRegressionTests
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Sixty_attempts_never_overflow_and_every_backoff_is_capped()
    {
        var inner = new FailingProvider(() => new ProviderException("connection refused", retryable: true));
        var delays = new List<TimeSpan>();
        var provider = Wrap(inner, delays, maxAttempts: 60);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.True(exception.Retryable);
        Assert.Equal(60, inner.Calls);
        Assert.Equal(59, delays.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[1]);
        Assert.All(delays, delay => Assert.True(delay <= MaxDelay, $"delay {delay} exceeds cap"));
        Assert.Equal(MaxDelay, delays[^1]);
    }

    [Fact]
    public async Task Retry_after_longer_than_the_configured_ceiling_is_capped()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromMinutes(4))
            .EnqueueSse("data: [DONE]\n\n");
        var delays = new List<TimeSpan>();
        var provider = new RetryingChatProvider(
            new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()),
            maxAttempts: 3,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            jitter: delay => delay,
            maxRetryAfter: TimeSpan.FromMinutes(2));

        await provider.CollectAsync(TestData.Request());

        Assert.Equal(new[] { TimeSpan.FromMinutes(2) }, delays);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Non_provider_exceptions_are_not_retried()
    {
        var inner = new FailingProvider(() => new InvalidOperationException("programming error"));
        var delays = new List<TimeSpan>();
        var provider = Wrap(inner, delays, maxAttempts: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Failure_after_the_first_yielded_event_propagates_without_retry()
    {
        var inner = new EventThenFailureProvider();
        var delays = new List<TimeSpan>();
        var provider = Wrap(inner, delays, maxAttempts: 5);

        var observed = new List<ChatStreamEvent>();
        var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var evt in provider.StreamAsync(TestData.Request(), CancellationToken.None))
            {
                observed.Add(evt);
            }
        });

        Assert.True(exception.Retryable);
        Assert.Equal("half", Assert.IsType<TextDelta>(Assert.Single(observed)).Text);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Stream_with_zero_events_is_treated_as_truncated_and_retried()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(string.Empty)
            .EnqueueSse(TestData.Sse("""{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}""", "[DONE]"));
        var delays = new List<TimeSpan>();
        var provider = Wrap(new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient()), delays, maxAttempts: 3);

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal(2, handler.Calls);
        Assert.Single(delays);
        Assert.Equal("ok", events.Text());
        Assert.Equal(StopReason.EndTurn, events.Completed().Stop);
    }

    private static RetryingChatProvider Wrap(IChatProvider inner, List<TimeSpan> delays, int maxAttempts) =>
        new(
            inner,
            maxAttempts: maxAttempts,
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: MaxDelay,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            jitter: delay => delay);

    private sealed class FailingProvider(Func<Exception> failure) : IChatProvider
    {
        public string Id => "failing";

        public int Calls { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            if (Calls > 0)
            {
                throw failure();
            }

            yield break;
        }
    }

    private sealed class EventThenFailureProvider : IChatProvider
    {
        public string Id => "event-then-failure";

        public int Calls { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            yield return new TextDelta("half");
            throw new ProviderException("connection reset", retryable: true);
        }
    }
}
