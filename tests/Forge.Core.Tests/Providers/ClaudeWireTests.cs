using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the claimed-identity wire against a request captured from the official client: the billing block is the first
/// system block, the identity sentence is the second, the entrypoint appears again in the user agent, and the beta list and
/// transport headers ride along. Also pins the off switch: with no claim the request is byte-for-byte what it was before.
/// </summary>
[Collection("anthropic-wire")]
public sealed class ClaudeWireTests
{
    public ClaudeWireTests() => AnthropicProvider.ForgetDegradations();

    private static readonly ClaudeWire Wire = new(
        "2.1.317",
        "68a",
        "claude-desktop-3p",
        AnthropicWire.Betas);

    [Fact]
    public async Task The_claim_puts_the_billing_block_first_and_the_official_identity_second()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = WiredProvider(Wire, handler.CreateClient());

        await provider.CollectAsync(TestData.Request());

        using var body = JsonDocument.Parse(handler.Last.Body!);
        var blocks = body.RootElement.GetProperty("system").EnumerateArray().ToArray();
        Assert.Equal(3, blocks.Length);
        Assert.Equal(
            "x-anthropic-billing-header: cc_version=2.1.317.68a; cc_entrypoint=claude-desktop-3p;",
            blocks[0].GetProperty("text").GetString());
        Assert.False(blocks[0].TryGetProperty("cache_control", out _));
        Assert.Equal(AnthropicWire.Identity, blocks[1].GetProperty("text").GetString());
        Assert.Equal("You are a Claude agent, built on Anthropic's Claude Agent SDK.", blocks[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_claim_sends_the_headers_the_official_client_sends()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = WiredProvider(Wire, handler.CreateClient());

        await provider.CollectAsync(TestData.Request() with { SessionId = "session-7" });

        var request = handler.Last;
        // .NET stores the " (external, …)" comment as its own value inside the typed User-Agent collection (the wire form
        // it writes is space-joined, verified separately by the exact-string test below); this handler joins values with ",".
        Assert.Equal("claude-cli/2.1.317,(external, claude-desktop-3p)", request.Header("user-agent"));
        Assert.Equal("cli", request.Header("x-app"));
        Assert.Equal("true", request.Header("anthropic-dangerous-direct-browser-access"));
        Assert.Equal(string.Join(',', AnthropicWire.Betas), request.Header("anthropic-beta"));
        Assert.Equal(AnthropicWire.StainlessPackageVersion, request.Header("x-stainless-package-version"));
        Assert.Equal("js", request.Header("x-stainless-lang"));
        Assert.Equal("0", request.Header("x-stainless-retry-count"));
        Assert.EndsWith("/v1/messages?beta=true", request.Uri.ToString(), StringComparison.Ordinal);
        // A uuid, because that is the shape the real client sends; the session id itself is not a uuid.
        Assert.True(Guid.TryParse(request.Header("x-claude-code-session-id"), out _));
    }

    [Fact]
    public async Task The_claim_uses_the_official_clients_cache_shape()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = WiredProvider(Wire, handler.CreateClient());

        await provider.CollectAsync(TestData.Request(
            tools: [TestData.ReadFileTool()],
            messages: [ChatMessage.User("a"), ChatMessage.Assistant("b"), ChatMessage.User("c")],
            cache: new CacheHint(CacheSystemAndTools: true, CacheThroughMessageIndex: 1)));

        using var body = JsonDocument.Parse(handler.Last.Body!);
        var blocks = body.RootElement.GetProperty("system").EnumerateArray().ToArray();

        // Billing block: no breakpoint. Identity and system: one hour, like the client being claimed. Tools: none at all.
        Assert.False(blocks[0].TryGetProperty("cache_control", out _));
        foreach (var block in blocks.Skip(1))
        {
            Assert.Equal("1h", block.GetProperty("cache_control").GetProperty("ttl").GetString());
        }

        var tools = body.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.DoesNotContain(tools, tool => tool.TryGetProperty("cache_control", out _));
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(
            messages,
            message => message.GetProperty("content").EnumerateArray()
                .Any(block => block.TryGetProperty("cache_control", out _)));
    }

    [Fact]
    public async Task The_beta_list_is_the_one_the_local_cli_announces()
    {
        // The set is version specific, so the probe's list wins over the one compiled into this program.
        string[] probed = ["claude-code-20250219", "oauth-2025-04-20", "probe-only-2026-01-01"];
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(
            Wired(new ClaudeWire("2.1.318", string.Empty, "claude-desktop-3p", AnthropicWire.Betas)),
            handler.CreateClient(),
            runtimeBuild: () => "da9",
            runtimeBetas: () => probed);

        await provider.CollectAsync(TestData.Request());

        Assert.Equal("claude-code-20250219,oauth-2025-04-20,probe-only-2026-01-01", handler.Last.Header("anthropic-beta"));
        Assert.Equal("application/json", handler.Last.Header("accept"));
    }

    [Fact]
    public async Task A_session_id_that_is_already_a_uuid_is_sent_unchanged()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = WiredProvider(Wire, handler.CreateClient());
        var id = "0f3d2c1b-4a59-4e6f-8a7b-1c2d3e4f5a6b";

        await provider.CollectAsync(TestData.Request() with { SessionId = id });

        Assert.Equal(id, handler.Last.Header("x-claude-code-session-id"));
    }

    [Fact]
    public async Task Without_the_claim_the_request_is_what_it_was_before()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request() with { SessionId = "session-7" });

        var request = handler.Last;
        Assert.Null(request.Header("user-agent"));
        Assert.Null(request.Header("x-app"));
        Assert.Null(request.Header("x-claude-code-session-id"));
        Assert.Equal("ak-test", request.Header("x-api-key"));
        Assert.Equal("https://api.anthropic.test/v1/messages", request.Uri.ToString());

        using var body = JsonDocument.Parse(request.Body!);
        var blocks = body.RootElement.GetProperty("system").EnumerateArray().ToArray();
        Assert.Single(blocks);
        Assert.DoesNotContain("x-anthropic-billing-header", blocks[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_billing_header_without_a_build_suffix_omits_the_dot()
    {
        var header = AnthropicWire.BillingHeader(new ClaudeWire("2.1.317", string.Empty, "cli", []));

        Assert.Equal("x-anthropic-billing-header: cc_version=2.1.317; cc_entrypoint=cli;", header);
        Assert.Equal("claude-cli/2.1.317 (external, cli)", AnthropicWire.UserAgent(new ClaudeWire("2.1.317", "68a", "cli", [])));
    }

    [Fact]
    public async Task A_429_keeps_both_the_reading_and_the_claimed_identity()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-5h-status", "rejected");
            return response;
        });
        var provider = WiredProvider(Wire, handler.CreateClient());

        var failure = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request() with { SessionId = "s" }));

        Assert.NotNull(failure.RateLimit);
        Assert.Equal(RateLimitStatus.Rejected, failure.RateLimit.FiveHour);
        Assert.Equal("claude-cli/2.1.317,(external, claude-desktop-3p)", handler.Last.Header("user-agent"));
    }

    private static AnthropicProvider WiredProvider(ClaudeWire wire, HttpClient http) =>
        new(Wired(wire), http, runtimeBuild: () => "da9", runtimeBetas: () => AnthropicWire.Betas);

    private static ProviderConfig Wired(ClaudeWire wire) => TestData.AnthropicConfig() with
    {
        ApiKey = "oat-token",
        AuthStyle = ProviderAuthStyle.BearerOnly,
        SystemPromptPreamble = AnthropicWire.Identity,
        Wire = wire,
    };

    private static string Answer() => string.Concat(
        TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":5,"output_tokens":0}}}"""),
        TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
        TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
}
