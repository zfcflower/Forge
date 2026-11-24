using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The retry ladder: which failures are worth how many attempts, how long the waits between them are, and what the caller
/// is told before each one. Pinned because the numbers are the difference between riding out an endpoint that is shedding
/// load and giving up inside its recovery window — a 429 that used the transport ladder (three attempts, 1.5s of waiting)
/// is exactly the silent failure this replaces, and nothing else in the code would notice it coming back.
/// </summary>
public sealed class RetryLadderTests
{
    private static readonly ChatRequest Request = new("system", [], [ChatMessage.User("hi")], new ModelSettings("model"));

    [Fact]
    public async Task An_overloaded_429_walks_the_whole_overload_ladder()
    {
        var inner = new RefusingProvider(429, "The inference service is overloaded. Please try again later.");
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(5, inner.Calls);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)],
            harness.Delays);

        // One event per retry, before the wait it describes, and it names the attempt that is about to be sent.
        Assert.Equal([2, 3, 4, 5], harness.Waits.Select(static wait => wait.Attempt));
        Assert.All(harness.Waits, static wait => Assert.Equal(5, wait.Total));
        Assert.All(harness.Waits, static wait => Assert.Equal(RetryReason.Overloaded, wait.Reason));
    }

    [Fact]
    public async Task A_rate_limited_429_says_so_instead_of_claiming_overload()
    {
        // Same ladder (the account's minute budget and the endpoint's load are one problem to the retry layer), different word:
        // the transcript has to say "rate limited" or the user reads the wrong cause.
        var inner = new RefusingProvider(429, "Rate limit reached for requests per min: limit 20, used 20.");
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(5, inner.Calls);
        Assert.All(harness.Waits, static wait => Assert.Equal(RetryReason.RateLimited, wait.Reason));
    }

    [Fact]
    public async Task A_server_failure_keeps_the_short_ladder()
    {
        var inner = new RefusingProvider(503, "upstream unavailable");
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(3, inner.Calls);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], harness.Delays);
        Assert.All(harness.Waits, static wait => Assert.Equal(RetryReason.Server, wait.Reason));
    }

    [Fact]
    public async Task A_retry_after_inside_the_budget_beats_the_ladder()
    {
        // The server's own number is the only one that is not a guess: 10 seconds, not the 1-second first step of the ladder.
        var inner = new RefusingProvider(429, "slow down", TimeSpan.FromSeconds(10));
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(TimeSpan.FromSeconds(10), harness.Delays[0]);
        Assert.Equal(2, harness.Waits[0].Attempt);
    }

    [Fact]
    public async Task A_dropped_connection_keeps_the_short_ladder_and_says_transport()
    {
        var inner = new DroppingProvider();
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.DrainAsync());

        Assert.Equal(3, inner.Calls);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], harness.Delays);
        Assert.All(harness.Waits, static wait => Assert.Equal(RetryReason.Transport, wait.Reason));
    }

    [Fact]
    public async Task A_retry_after_longer_than_one_request_may_sleep_is_not_slept_through()
    {
        // 90 seconds is under the quota threshold (five minutes), so this is the shape that used to freeze a turn for a
        // minute and a half: the request gives up at once, keeps the server's own delay on the failure, and the turn says so.
        var inner = new RefusingProvider(429, "slow down", TimeSpan.FromSeconds(90));
        var harness = new Harness(inner);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(1, inner.Calls);
        Assert.Empty(harness.Delays);
        Assert.Empty(harness.Waits);
        Assert.True(ProviderErrors.TryGetRetryAfter(failure, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(90), retryAfter);
    }

    [Fact]
    public async Task A_quota_429_is_never_retried()
    {
        var inner = new RefusingProvider(429, "You exceeded your current quota, please check your plan.");
        var harness = new Harness(inner);

        await Assert.ThrowsAsync<ProviderException>(() => harness.DrainAsync());

        Assert.Equal(1, inner.Calls);
        Assert.Empty(harness.Waits);
    }

    /// <summary>One request, an always-refusing endpoint, and a delay that records instead of sleeping.</summary>
    private sealed class Harness(IChatProvider inner)
    {
        public List<TimeSpan> Delays { get; } = [];

        public List<RetryWait> Waits { get; } = [];

        public async Task DrainAsync()
        {
            var provider = new RetryingChatProvider(
                inner,
                delayAsync: (delay, _) =>
                {
                    Delays.Add(delay);
                    return ValueTask.CompletedTask;
                },
                jitter: static ceiling => ceiling);

            await foreach (var chunk in provider.StreamAsync(Request, CancellationToken.None))
            {
                if (chunk is RetryWait wait)
                {
                    Waits.Add(wait);
                }
            }
        }
    }

    /// <summary>An endpoint whose connection dies before anything arrives: no status code, so the reason is transport.</summary>
    private sealed class DroppingProvider : IChatProvider
    {
        public int Calls { get; private set; }

        public string Id => "gw";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            Calls++;
            yield return Drop();
        }

        private static ChatStreamEvent Drop() => throw new HttpRequestException("The connection was reset by the peer.");
    }

    /// <summary>An endpoint that rejects every attempt before it produces anything: the only shape the retry layer owns.</summary>
    private sealed class RefusingProvider(int statusCode, string body, TimeSpan? retryAfter = null) : IChatProvider
    {        public int Calls { get; private set; }

        public string Id => "gw";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            Calls++;

            // Yielded from a call that only throws: an iterator needs a yield it can reach, and the attempt still has to fail
            // before producing anything.
            yield return Refuse();
        }

        private ChatStreamEvent Refuse()
        {
            var failure = new ProviderException($"gw returned {statusCode}: {body}", retryable: true, statusCode);
            if (retryAfter is { } delay)
            {
                failure.Data[ProviderErrors.RetryAfterKey] = delay;
            }

            throw failure;
        }
    }
}
