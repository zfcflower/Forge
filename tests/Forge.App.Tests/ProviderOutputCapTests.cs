using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The one number that couples the model metadata cell, compaction's output reserve and the provider's reply cap: what a
/// user types in 「最大输出」 reaches the wire as <c>max_tokens</c>, so the reserve compaction subtracts from the window is
/// exactly what the request allows — and a reply that hits it stops as <see cref="StopReason.MaxTokens"/> instead of
/// running on. These tests read the request body the adapters actually send (a local handler, no network).
/// </summary>
public sealed class ProviderOutputCapTests : IDisposable
{
    private const string BaseUrl = "https://gateway.example/v1";
    private readonly string id = "p-" + Guid.NewGuid().ToString("n");

    public void Dispose() => ProviderDegradations.Clear();

    /// <summary>Serves a canned stream and keeps the request body the adapter sent.</summary>
    private sealed class CapturingHandler(IReadOnlyList<string> frames) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return FakeSse.Ok(FakeSse.Body(frames));
        }
    }

    private static ChatRequest Request(int? maxOutputTokens) =>
        new("sys", [], [ChatMessage.User("hi")], new ModelSettings("m", MaxOutputTokens: maxOutputTokens));

    private static IReadOnlyList<string> AnthropicStream(string stopReason) =>
    [
        FakeSse.Frame("""{"type":"message_start","message":{"usage":{"input_tokens":5}}}"""),
        FakeSse.Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        FakeSse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"cut"}}"""),
        FakeSse.Frame(
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stopReason + "\"},\"usage\":{\"output_tokens\":3}}"),
        FakeSse.Frame("""{"type":"message_stop"}"""),
    ];

    private static IReadOnlyList<string> CompatibleStream() =>
    [
        FakeSse.Frame("""{"choices":[{"delta":{"content":"ok"}}]}"""),
        FakeSse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}"""),
        FakeSse.Done,
    ];

    private static IReadOnlyList<string> ResponsesStream() =>
    [
        FakeSse.Frame("""{"type":"response.output_text.delta","delta":"ok"}"""),
        FakeSse.Frame("""{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":3}}}"""),
    ];

    private AnthropicProvider Anthropic(CapturingHandler handler) =>
        new(new ProviderConfig(id, "anthropic", BaseUrl, "k"), new HttpClient(handler));

    private OpenAiCompatibleProvider Compatible(CapturingHandler handler) =>
        new(new ProviderConfig(id, "openai-compatible", BaseUrl, "k"), new HttpClient(handler));

    private OpenAiResponsesProvider Responses(CapturingHandler handler) =>
        new(new ProviderConfig(id, "openai-responses", BaseUrl, "k"), new HttpClient(handler));

    /// <summary>Runs one request to the end and returns the events; the body is asserted on the handler afterwards.</summary>
    private static async Task<List<ChatStreamEvent>> DrainAsync(IChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public async Task The_configured_cap_reaches_the_anthropic_request_as_max_tokens()
    {
        var handler = new CapturingHandler(AnthropicStream("end_turn"));
        await DrainAsync(Anthropic(handler), Request(4_096));

        Assert.Contains("\"max_tokens\":4096", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_cap_keeps_the_adapters_own_default()
    {
        var handler = new CapturingHandler(AnthropicStream("end_turn"));
        await DrainAsync(Anthropic(handler), Request(null));

        Assert.Contains("\"max_tokens\":8192", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_configured_cap_reaches_the_openai_family_only_when_it_is_known()
    {
        var compatible = new CapturingHandler(CompatibleStream());
        await DrainAsync(Compatible(compatible), Request(4_096));
        Assert.Contains("\"max_completion_tokens\":4096", compatible.Body, StringComparison.Ordinal);

        var responses = new CapturingHandler(ResponsesStream());
        await DrainAsync(Responses(responses), Request(4_096));
        Assert.Contains("\"max_output_tokens\":4096", responses.Body, StringComparison.Ordinal);

        // Unknown is an absent field, not a zero: the endpoint keeps its own default.
        var unset = new CapturingHandler(ResponsesStream());
        await DrainAsync(Responses(unset), Request(null));
        Assert.DoesNotContain("max_output_tokens", unset.Body, StringComparison.Ordinal);

        // The compatible family mirrors it: unpinned is an absent field, so a custom gateway keeps its own default - the
        // shape an aggregator's derived capability must never occupy again.
        var compatUnset = new CapturingHandler(CompatibleStream());
        await DrainAsync(Compatible(compatUnset), Request(null));
        Assert.DoesNotContain("max_completion_tokens", compatUnset.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_stopped_at_the_cap_is_reported_as_a_truncated_turn()
    {
        var handler = new CapturingHandler(AnthropicStream("max_tokens"));
        var events = await DrainAsync(Anthropic(handler), Request(4_096));

        // The turn ends as MaxTokens, which the transcript renders as 截断 and the loop never retries as an error.
        var completed = Assert.IsType<MessageCompleted>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, completed.Stop);
    }

    [Theory]
    [InlineData(ThinkingEffort.Off, 4_096, 4_096)]
    [InlineData(ThinkingEffort.Off, null, 8_192)]
    [InlineData(ThinkingEffort.Low, null, 8_192)]       // 2 048 is below the cap: the cap stands
    [InlineData(ThinkingEffort.Medium, null, 12_288)]    // exactly at the cap: lifted to 8 192 + 4 096
    [InlineData(ThinkingEffort.High, 8_192, 28_672)]     // 24 576 + 4 096
    [InlineData(ThinkingEffort.XHigh, 64_000, 64_000)]   // 32 768 is below the pin: the pin stands
    public async Task The_output_reserve_is_the_max_tokens_the_request_carries(
        ThinkingEffort thinking,
        int? pin,
        int expected)
    {
        var handler = new CapturingHandler(AnthropicStream("end_turn"));
        var model = new ModelSettings("m", thinking, pin);
        await DrainAsync(Anthropic(handler), new ChatRequest("sys", [], [ChatMessage.User("hi")], model));

        // The body says `expected`; the reserve compaction subtracts from the window is that same number, so a trigger line
        // can never sit above the request's real prompt limit.
        Assert.Contains($"\"max_tokens\":{expected}", handler.Body, StringComparison.Ordinal);
        Assert.Equal(expected, OutputCeiling.Wire(model));
        Assert.Equal(
            expected,
            CompactionPolicy.Budget(200_000, null, wireMaxOutput: OutputCeiling.Wire(model))!.Value.Reserve);
    }
}
