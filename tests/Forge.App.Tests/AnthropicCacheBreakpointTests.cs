using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Where the Anthropic request puts its cache breakpoints once the history is the managed context: the system block (which
/// covers the tools), the stable boundary at the end of the managed history, and the tail of the turn in flight. The middle
/// one is the point of this file - it is the only breakpoint whose bytes never move between turns, so without it a new turn
/// has nothing stable to read back.
/// </summary>
[Collection("provider-degradations")]
public sealed class AnthropicCacheBreakpointTests : IDisposable
{
    private const string BaseUrl = "https://gateway.example/v1";

    private readonly string id = "p-" + Guid.NewGuid().ToString("n");

    public void Dispose() => ProviderDegradations.Clear();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return FakeSse.Ok(FakeSse.Body(
            [
                FakeSse.Frame("""{"type":"message_start","message":{"usage":{"input_tokens":5}}}"""),
                FakeSse.Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                FakeSse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
                FakeSse.Frame("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"""),
                FakeSse.Frame("""{"type":"message_stop"}"""),
            ]));
        }
    }

    /// <summary>Managed history of two entries plus the turn in flight, the shape a real request carries: each finished turn is
    /// one user message (its task) followed by one assistant message (its report).</summary>
    private static ChatRequest Request(int? stableBoundary) => new(
        "sys",
        [],
        [
            ChatMessage.User("one"),
            ChatMessage.Assistant("**结论**:\n\ndone one"),
            ChatMessage.User("two"),
            ChatMessage.Assistant("**结论**:\n\ndone two"),
            ChatMessage.User("the turn in flight"),
            ChatMessage.User("and its latest result"),
        ],
        new ModelSettings("claude-sonnet-5"),
        new CacheHint(CacheSystemAndTools: true, CacheThroughMessageIndex: 4, Ttl: "1h") { StableBoundaryIndex = stableBoundary });

    /// <summary>The texts of the content blocks that carry a breakpoint; adjacent same-role messages merge into one wire message, so the wire index says nothing about which history message it was.</summary>
    private static List<string> BreakpointTexts(string body)
    {
        using var document = JsonDocument.Parse(body);
        var seen = new List<string>();
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            foreach (var block in message.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("cache_control", out var control)
                    && control.GetProperty("type").GetString() == "ephemeral")
                {
                    seen.Add(block.GetProperty("text").GetString()!);
                }
            }
        }

        return seen;
    }

    [Fact]
    public async Task The_managed_boundary_and_the_tail_each_carry_a_breakpoint()
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(new ProviderConfig(id, "anthropic", BaseUrl, "k"), new HttpClient(handler));

        await DrainAsync(provider, Request(stableBoundary: 3));

        // The end of the managed history is stable across turns; the tail covers this turn's own exchange. Two message
        // breakpoints plus the system one stay inside Anthropic's limit of four.
        var texts = BreakpointTexts(handler.Body);
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, text => text.StartsWith("**结论**:\n\ndone two", StringComparison.Ordinal));
        Assert.Contains("the turn in flight", texts);
        Assert.Contains("\"ttl\":\"1h\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_managed_history_only_the_tail_breakpoint_is_written()
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(new ProviderConfig(id, "anthropic", BaseUrl, "k"), new HttpClient(handler));

        await DrainAsync(provider, Request(stableBoundary: null));

        Assert.Equal(["the turn in flight"], BreakpointTexts(handler.Body));
    }

    private static async Task DrainAsync(IChatProvider provider, ChatRequest request)
    {
        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None))
        {
        }
    }
}
