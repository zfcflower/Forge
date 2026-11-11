using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class QuotaRetryAndProbeTests
{
    private sealed class ScriptedProvider(Func<int, Exception?> failureForCall) : IChatProvider
    {
        public string Id => "scripted";

        public int Calls { get; private set; }

        public ChatRequest? Last { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            Last = request;
            await Task.Yield();
            if (failureForCall(Calls) is { } failure)
            {
                throw failure;
            }

            yield return new MessageCompleted(ChatMessage.Assistant("pong"), TokenUsage.Empty, StopReason.EndTurn);
        }
    }

    private static ProviderException Status(int status, string body) =>
        new($"scripted returned {status}: {body}", status is 429 or >= 500, status);

    private static (RetryingChatProvider Provider, List<TimeSpan> Delays) Retrying(IChatProvider inner)
    {
        var delays = new List<TimeSpan>();
        var provider = new RetryingChatProvider(
            inner,
            maxAttempts: 3,
            delayAsync: (d, _) =>
            {
                delays.Add(d);
                return ValueTask.CompletedTask;
            },
            jitter: d => d);
        return (provider, delays);
    }

    private static ChatRequest Request() =>
        new(string.Empty, [], [ChatMessage.User("hi")], new ModelSettings("m"));

    private static async Task Drain(IChatProvider provider)
    {
        await foreach (var _ in provider.StreamAsync(Request(), CancellationToken.None))
        {
        }
    }

    [Fact]
    public async Task Quota_failure_is_not_retried()
    {
        var inner = new ScriptedProvider(_ => Status(429, "insufficient_quota"));
        var (provider, delays) = Retrying(inner);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Drain(provider));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Payment_required_is_not_retried()
    {
        var inner = new ScriptedProvider(_ => Status(402, "pay up"));
        var (provider, _) = Retrying(inner);

        await Assert.ThrowsAsync<ProviderException>(() => Drain(provider));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Long_retry_after_is_not_waited_out()
    {
        var failure = Status(429, "rate limited");
        failure.Data[ProviderErrors.RetryAfterKey] = TimeSpan.FromHours(2);
        var inner = new ScriptedProvider(_ => failure);
        var (provider, delays) = Retrying(inner);

        await Assert.ThrowsAsync<ProviderException>(() => Drain(provider));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Plain_429_is_still_retried()
    {
        var inner = new ScriptedProvider(call => call == 1 ? Status(429, "slow down") : null);
        var (provider, delays) = Retrying(inner);

        await Drain(provider);

        Assert.Equal(2, inner.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Server_error_is_still_retried()
    {
        var inner = new ScriptedProvider(call => call == 1 ? Status(503, "overloaded") : null);
        var (provider, _) = Retrying(inner);

        await Drain(provider);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Probe_returns_true_and_sends_minimal_request()
    {
        var inner = new ScriptedProvider(_ => null);
        var probe = new ProviderQuotaProbe((_, _) => new ValueTask<IChatProvider>(inner));

        var ok = await probe.ProbeAsync(new ModelSettings("m", ThinkingEffort.High, 4096, "p"), CancellationToken.None);

        Assert.True(ok);
        var request = inner.Last!;
        Assert.Equal(1, request.Model.MaxOutputTokens);
        Assert.Equal(ThinkingEffort.Off, request.Model.Thinking);
        Assert.Equal("p", request.Model.ProviderId);
        Assert.Empty(request.Tools);
        Assert.Equal(string.Empty, request.SystemPrompt);
        var message = Assert.Single(request.Messages);
        Assert.Equal(MessageRole.User, message.Role);
    }

    [Fact]
    public async Task Probe_returns_false_while_quota_is_exhausted()
    {
        var inner = new ScriptedProvider(_ => Status(429, "insufficient_quota"));
        var probe = new ProviderQuotaProbe((_, _) => new ValueTask<IChatProvider>(inner));

        Assert.False(await probe.ProbeAsync(new ModelSettings("m"), CancellationToken.None));
    }

    [Fact]
    public async Task Probe_rethrows_other_failures()
    {
        var inner = new ScriptedProvider(_ => Status(401, "bad key"));
        var probe = new ProviderQuotaProbe((_, _) => new ValueTask<IChatProvider>(inner));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => probe.ProbeAsync(new ModelSettings("m"), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
    }
}
