using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the downgrade path: an account or gateway that refuses a beta gets one resent request with a reduced shape, never a failed
/// turn, and never a second downgrade.
/// </summary>
[Collection("anthropic-wire")]
public sealed class ClaudeWireDegradationTests : IDisposable
{
    private static readonly ClaudeWire Wire = new("2.1.318", "da9", "claude-desktop-3p", AnthropicWire.Betas);

    public ClaudeWireDegradationTests() => AnthropicProvider.ForgetDegradations();

    public void Dispose() => AnthropicProvider.ForgetDegradations();

    [Fact]
    public async Task A_refused_beta_is_resent_with_the_required_betas_and_the_short_cache()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Refusal("""{"type":"error","error":{"type":"invalid_request_error","message":"anthropic-beta: unknown value extended-cache-ttl-2025-04-11"}}"""))
            .EnqueueSse(Answer());
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas);

        var events = await provider.CollectAsync(
            TestData.Request(cache: new CacheHint(CacheSystemAndTools: true)));

        Assert.Equal(2, handler.Calls);
        // The rejected request asked for the announced list and the hour; the resent one asks for the minimum.
        Assert.Equal(string.Join(',', AnthropicWire.Betas), handler.Requests[0].Header("anthropic-beta"));
        Assert.Equal(string.Join(',', ClaudeSubscriptionCredentials.Betas), handler.Requests[1].Header("anthropic-beta"));
        Assert.Contains("\"ttl\"", handler.Requests[0].Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ttl\"", handler.Requests[1].Body!, StringComparison.Ordinal);

        var completed = events.Completed();
        Assert.False(string.IsNullOrEmpty(completed.Notice), "the downgrade has to be visible, not silent");
        Assert.Contains("beta", completed.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_api_key_request_drops_its_only_beta_before_resending()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Refusal("""{"type":"error","error":{"type":"invalid_request_error","message":"unknown anthropic-beta"}}"""))
            .EnqueueSse(Answer());
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(cache: new CacheHint(CacheSystemAndTools: true)));

        Assert.Equal(AnthropicWire.ExtendedCacheTtlBeta, handler.Requests[0].Header("anthropic-beta"));
        Assert.Null(handler.Requests[1].Header("anthropic-beta"));
        Assert.DoesNotContain("\"ttl\"", handler.Requests[1].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_that_does_not_name_a_beta_is_not_downgraded()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Refusal("""{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"}}"""));
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas);

        var failure = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Equal(400, failure.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_downgrade_happens_once_so_a_second_refusal_is_a_real_error()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Refusal("""{"type":"error","error":{"message":"anthropic-beta: nope"}}"""))
            .Enqueue(() => Refusal("""{"type":"error","error":{"message":"anthropic-beta: still nope"}}"""));
        var provider = new AnthropicProvider(
            Subscription(),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas);

        await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Equal(2, handler.Calls);
    }

    private static HttpResponseMessage Refusal(string body) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(body) };

    private static ProviderConfig Subscription() => TestData.AnthropicConfig() with
    {
        ApiKey = "oat-token",
        AuthStyle = ProviderAuthStyle.BearerOnly,
        SystemPromptPreamble = AnthropicWire.Identity,
        Wire = Wire,
    };

    private static string Answer() => string.Concat(
        TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":5,"output_tokens":0}}}"""),
        TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
        TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
}
