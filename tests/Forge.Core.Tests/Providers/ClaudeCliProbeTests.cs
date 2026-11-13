using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins how the claimed build suffix is obtained: parsed out of a request the local CLI builds, never invented. Measured facts
/// behind the design are in the provider spec — the same binary and credential produced different suffixes through different
/// destinations, so the value is read at request time and omitted when it cannot be read.
/// </summary>
public sealed class ClaudeCliProbeTests
{
    [Fact]
    public void The_suffix_is_read_from_the_billing_block_the_cli_writes()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"model":"x","system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.317.d83; cc_entrypoint=cli;"}]}""");

        Assert.Equal("d83", ClaudeCliProbe.ExtractBuild(body));
    }

    [Fact]
    public void A_body_without_the_billing_block_reports_no_suffix()
    {
        Assert.Null(ClaudeCliProbe.ExtractBuild(Encoding.UTF8.GetBytes("""{"system":[{"type":"text","text":"hello"}]}""")));
        Assert.Null(ClaudeCliProbe.ExtractBuild([]));
    }

    [Fact]
    public void A_version_without_a_dot_part_is_not_mistaken_for_a_suffix()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.317; cc_entrypoint=cli;"}]}""");

        Assert.Null(ClaudeCliProbe.ExtractBuild(body));
    }

    [Fact]
    public void Probing_without_a_cli_answers_nothing_and_returns_immediately()
    {
        var started = DateTimeOffset.UtcNow;

        Assert.Null(ClaudeCliProbe.ReadVersion(null));
        Assert.Null(ClaudeCliProbe.TryReadBuild(null));
        Assert.Null(ClaudeCliProbe.ReadVersion(@"Z:\nope\claude.exe", TimeSpan.FromMilliseconds(200)));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5), "a missing CLI must not cost a wait");
    }

    // ---------------------------------------------------------------- what the provider does with it

    [Fact]
    public async Task The_runtime_suffix_goes_into_the_billing_block()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(Wired(new ClaudeWire("2.1.317", string.Empty, "claude-desktop-3p", [])), handler.CreateClient(), runtimeBuild: () => "d83");

        await provider.CollectAsync(TestData.Request());

        Assert.Equal(
            "x-anthropic-billing-header: cc_version=2.1.317.d83; cc_entrypoint=claude-desktop-3p;",
            FirstSystemBlock(handler));
    }

    [Fact]
    public async Task Without_a_suffix_the_block_omits_the_dot_part()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(Wired(new ClaudeWire("2.1.317", string.Empty, "claude-desktop-3p", [])), handler.CreateClient(), runtimeBuild: () => null);

        await provider.CollectAsync(TestData.Request());

        Assert.Equal(
            "x-anthropic-billing-header: cc_version=2.1.317; cc_entrypoint=claude-desktop-3p;",
            FirstSystemBlock(handler));
    }

    [Fact]
    public async Task A_pinned_suffix_wins_and_the_cli_is_not_asked()
    {
        var asked = false;
        var handler = new FakeHttpMessageHandler().EnqueueSse(Answer());
        var provider = new AnthropicProvider(
            Wired(new ClaudeWire("2.1.317", "abc", "claude-desktop-3p", [])),
            handler.CreateClient(),
            runtimeBuild: () => { asked = true; return "zzz"; });

        await provider.CollectAsync(TestData.Request());

        Assert.False(asked, "a pinned value means there is nothing to probe");
        Assert.EndsWith(
            "cc_version=2.1.317.abc; cc_entrypoint=claude-desktop-3p;",
            FirstSystemBlock(handler),
            StringComparison.Ordinal);
    }

    private static string? FirstSystemBlock(FakeHttpMessageHandler handler)
    {
        using var document = System.Text.Json.JsonDocument.Parse(handler.Last.Body!);
        return document.RootElement.GetProperty("system")[0].GetProperty("text").GetString();
    }

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
