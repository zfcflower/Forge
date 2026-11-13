using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the one thing every provider shares: the stable head — the composed system prompt plus the tool definitions. Cache
/// strategies differ per vendor (breakpoints and lifetimes for Anthropic, automatic prefix caching and a routing key elsewhere), so
/// that head is the only part that must be identical everywhere. In particular the identity Forge claims on Anthropic's wire must
/// never leak into it: it is a vendor adaptation, not part of the prompt.
/// </summary>
public sealed class ProviderNeutralPromptHeadTests
{
    private static readonly PromptLayers Layers = new(BuiltInPrompts.System, "global agent text", "project agent text");

    [Fact]
    public void The_composed_head_carries_no_vendor_identity()
    {
        var composed = new PromptComposer().Compose(Layers, agentRole: "role text");

        Assert.DoesNotContain(AnthropicWire.Identity, composed.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("x-anthropic-billing-header", composed.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", composed.StablePrefix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_fingerprint_is_the_same_with_and_without_the_claimed_identity()
    {
        // The fingerprint decides whether two sessions can share a cache. It must depend on the head alone — flipping the claim
        // changes the vendor blocks, not the head, and must therefore not invalidate a session's replay or handover identity.
        var composed = new PromptComposer().Compose(Layers);
        ToolDefinition[] tools = [new("read", "Read a file.", """{"type":"object"}""")];

        var neutral = PromptFingerprint.Compute(composed.StablePrefix, tools);
        var withClaim = PromptFingerprint.Compute(
            composed.StablePrefix,
            tools);

        Assert.Equal(neutral, withClaim);
        Assert.Equal(32, neutral.Length);
    }

    [Fact]
    public async Task The_claim_adds_blocks_without_touching_the_head()
    {
        var neutralBody = await SendThroughAnthropicAsync(wire: null);
        var claimedBody = await SendThroughAnthropicAsync(
            new ClaudeWire("2.1.318", "da9", "claude-desktop-3p", AnthropicWire.Betas));

        using var neutral = System.Text.Json.JsonDocument.Parse(neutralBody);
        using var claimed = System.Text.Json.JsonDocument.Parse(claimedBody);

        // The application's own system text is the same bytes in both, and so is the conversation.
        var neutralBlocks = neutral.RootElement.GetProperty("system").EnumerateArray().ToArray();
        var claimedBlocks = claimed.RootElement.GetProperty("system").EnumerateArray().ToArray();
        Assert.Single(neutralBlocks);
        Assert.Equal(3, claimedBlocks.Length);
        Assert.Equal(
            neutralBlocks[0].GetProperty("text").GetString(),
            claimedBlocks[2].GetProperty("text").GetString());
        Assert.StartsWith("x-anthropic-billing-header:", claimedBlocks[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(AnthropicWire.Identity, claimedBlocks[1].GetProperty("text").GetString());
        Assert.Equal(
            neutral.RootElement.GetProperty("messages").GetRawText(),
            claimed.RootElement.GetProperty("messages").GetRawText());
        Assert.Equal(
            neutral.RootElement.GetProperty("tools").GetRawText(),
            claimed.RootElement.GetProperty("tools").GetRawText());
    }

    [Fact]
    public async Task Another_vendor_never_sees_the_claimed_identity()
    {
        // A ProviderConfig carrying the claim must still produce an ordinary OpenAI-compatible request: the adaptation belongs to
        // one adapter, not to the shared request.
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"id":"c","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}""",
            """{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]"));
        var provider = new OpenAiCompatibleProvider(
            TestData.OpenAiConfig() with
            {
                Wire = new ClaudeWire("2.1.318", "da9", "claude-desktop-3p", AnthropicWire.Betas),
            },
            handler.CreateClient());

        await provider.CollectAsync(TestData.Request(
            system: new PromptComposer().Compose(Layers).StablePrefix,
            cache: new CacheHint(CacheSystemAndTools: true)));

        var body = handler.Last.Body!;
        Assert.DoesNotContain("x-anthropic-billing-header", body, StringComparison.Ordinal);
        Assert.DoesNotContain(AnthropicWire.Identity, body, StringComparison.Ordinal);
        Assert.Null(handler.Last.Header("user-agent"));
        Assert.Null(handler.Last.Header("x-app"));
    }

    private static async Task<string> SendThroughAnthropicAsync(ClaudeWire? wire)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":5,"output_tokens":0}}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
            TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
            TestData.WithEvent("message_stop", """{"type":"message_stop"}""")));

        var config = TestData.AnthropicConfig() with
        {
            AuthStyle = wire is null ? ProviderAuthStyle.Default : ProviderAuthStyle.BearerOnly,
            ApiKey = wire is null ? "ak-test" : "oat-token",
            SystemPromptPreamble = wire is null ? null : AnthropicWire.Identity,
            Wire = wire,
        };
        var provider = new AnthropicProvider(
            config,
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => AnthropicWire.Betas);

        await provider.CollectAsync(TestData.Request(
            system: new PromptComposer().Compose(Layers).StablePrefix,
            tools: [new ToolDefinition("read", "Read a file.", """{"type":"object"}""")],
            cache: new CacheHint(CacheSystemAndTools: true)));

        return handler.Last.Body!;
    }
}
