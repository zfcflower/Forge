using System.Diagnostics;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The adapter-side contract behind <c>turn_metrics.stream_ms</c>: every <see cref="MessageCompleted"/> a built-in
/// adapter produces carries a <see cref="MessageCompleted.StreamElapsed"/> the adapter itself measured in its read
/// loop. A stream that yields several increments measures a real span (and never the whole request's wall clock), one
/// that completes without any output increment carries zero, a cancelled answer never fabricates a completion, and the
/// one-shot downgrade resends over the same streaming path so its answer is measured like any other - the refused
/// attempt contributed nothing. All three adapters are driven through the same fake SSE transport (<see cref="FakeSse"/>).
/// </summary>
public sealed class ProviderStreamElapsedTests : IDisposable
{
    private const string BaseUrl = "https://gateway.example/v1";
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(40);

    private readonly string id = "p-" + Guid.NewGuid().ToString("n");

    public void Dispose() => ProviderDegradations.Clear();

    private AnthropicProvider Anthropic(FakeSse.Handler handler) =>
        new(new ProviderConfig(id, "anthropic", BaseUrl, "k"), new HttpClient(handler));

    private OpenAiCompatibleProvider Compatible(FakeSse.Handler handler, bool cacheKey = false) =>
        new(
            new ProviderConfig(id, "openai-compatible", BaseUrl, "k"),
            new HttpClient(handler),
            options: new ProviderOptions { SendPromptCacheKey = cacheKey });

    private OpenAiResponsesProvider Responses(FakeSse.Handler handler, bool cacheKey = false) =>
        new(
            new ProviderConfig(id, "openai-responses", BaseUrl, "k"),
            new HttpClient(handler),
            options: new ProviderOptions { SendPromptCacheKey = cacheKey });

    private static ChatRequest Request() =>
        new("sys", [], [ChatMessage.User("hi")], new ModelSettings("m"))
        {
            CacheRoutingKey = "root-1",
        };

    private static async Task<List<ChatStreamEvent>> Drain(IChatProvider provider, ChatRequest request, CancellationToken ct = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in provider.StreamAsync(request, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    private static MessageCompleted Completed(List<ChatStreamEvent> events) =>
        Assert.IsType<MessageCompleted>(events[^1]);

    // --- Anthropic Messages API -------------------------------------------------

    private static IReadOnlyList<string> AnthropicStream() =>
    [
        FakeSse.Frame("""{"type":"message_start","message":{"usage":{"input_tokens":5}}}"""),
        FakeSse.Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        FakeSse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"one"}}"""),
        FakeSse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"two"}}"""),
        FakeSse.Frame("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"""),
        FakeSse.Frame("""{"type":"message_stop"}"""),
    ];

    [Fact]
    public async Task Anthropic_measures_first_increment_to_stream_end()
    {
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(AnthropicStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Anthropic(handler), Request());
        watch.Stop();

        var done = Completed(events);
        Assert.Equal(2, events.OfType<TextDelta>().Count());
        // Three delayed reads follow the first increment (deltas, stop), and the whole call also paid for the two
        // reads before it: the measurement is strictly smaller than the request's wall time, never equal to it.
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, 2 * Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }

    [Fact]
    public async Task Anthropic_streams_nothing_measures_zero()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"type":"message_start","message":{"usage":{"input_tokens":5}}}"""),
            FakeSse.Frame("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}"""),
            FakeSse.Frame("""{"type":"message_stop"}"""),
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, Pause)));

        var done = Completed(await Drain(Anthropic(handler), Request()));

        // Usage-only events never start the clock, so a zero-output call carries zero - never an "unknown".
        Assert.Equal(TimeSpan.Zero, done.StreamElapsed);
    }

    [Fact]
    public async Task Anthropic_cancelled_after_partial_output_never_fabricates_a_completion()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"type":"message_start","message":{"usage":{"input_tokens":5}}}"""),
            FakeSse.Frame("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            FakeSse.Frame("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"one"}}"""),
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, holdAtEnd: true)));
        using var cancelled = new CancellationTokenSource();

        var events = new List<ChatStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in Anthropic(handler).StreamAsync(Request(), cancelled.Token))
            {
                events.Add(evt);
                if (evt is TextDelta)
                {
                    await cancelled.CancelAsync();
                }
            }
        });

        // The answer was partial and then cancelled: the increment got through, the interruption surfaced as
        // cancellation, and nothing stands in for the completion that never happened.
        Assert.Contains(events, e => e is TextDelta);
        Assert.DoesNotContain(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task Anthropic_downgrade_resend_measures_only_its_own_stream()
    {
        var handler = new FakeSse.Handler(attempt => attempt == 1 ? FakeSse.Refused("anthropic-beta") : FakeSse.Ok(FakeSse.Body(AnthropicStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Anthropic(handler), Request());
        watch.Stop();

        Assert.Equal(2, handler.Attempts);
        var done = Completed(events);
        Assert.NotNull(done.Notice);
        // The refused attempt streamed nothing and its round trip is excluded: the measurement is the resent
        // stream's own first increment to its own end, strictly inside the whole call's wall time.
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, 2 * Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }

    // --- OpenAI Chat Completions ------------------------------------------------

    private static IReadOnlyList<string> CompatibleStream() =>
    [
        FakeSse.Frame("""{"choices":[{"delta":{"content":"one"}}]}"""),
        FakeSse.Frame("""{"choices":[{"delta":{"content":"two"}}]}"""),
        FakeSse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}"""),
        FakeSse.Done,
    ];

    [Fact]
    public async Task Compatible_measures_first_increment_to_stream_end()
    {
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(CompatibleStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Compatible(handler), Request());
        watch.Stop();

        var done = Completed(events);
        Assert.Equal(2, events.OfType<TextDelta>().Count());
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, 2 * Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }

    [Fact]
    public async Task Compatible_streams_nothing_measures_zero()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"usage":{"prompt_tokens":5,"completion_tokens":3}}"""),
            FakeSse.Done,
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, Pause)));

        var done = Completed(await Drain(Compatible(handler), Request()));

        Assert.Equal(TimeSpan.Zero, done.StreamElapsed);
    }

    [Fact]
    public async Task Compatible_tool_call_increments_count_as_output()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"noop","arguments":"{}"}}]}}]}"""),
            FakeSse.Frame("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""),
            FakeSse.Done,
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, Pause)));

        var events = await Drain(Compatible(handler), Request());

        // A tool-only answer streams its call increments: the clock starts at the first of them, not at the
        // first text delta that never comes.
        var done = Completed(events);
        Assert.Contains(events, e => e is ToolCallStarted);
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, Pause.TotalMilliseconds, 3 * Pause.TotalMilliseconds + 1000);
    }

    [Fact]
    public async Task Compatible_cancelled_after_partial_output_never_fabricates_a_completion()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"choices":[{"delta":{"content":"one"}}]}"""),
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, holdAtEnd: true)));
        using var cancelled = new CancellationTokenSource();

        var events = new List<ChatStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in Compatible(handler).StreamAsync(Request(), cancelled.Token))
            {
                events.Add(evt);
                if (evt is TextDelta)
                {
                    await cancelled.CancelAsync();
                }
            }
        });

        Assert.Contains(events, e => e is TextDelta);
        Assert.DoesNotContain(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task Compatible_downgrade_resend_measures_only_its_own_stream()
    {
        var handler = new FakeSse.Handler(attempt => attempt == 1 ? FakeSse.Refused("prompt_cache_key") : FakeSse.Ok(FakeSse.Body(CompatibleStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Compatible(handler, cacheKey: true), Request());
        watch.Stop();

        Assert.Equal(2, handler.Attempts);
        var done = Completed(events);
        Assert.NotNull(done.Notice);
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, 2 * Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }

    // --- OpenAI Responses -------------------------------------------------------

    private static IReadOnlyList<string> ResponsesStream() =>
    [
        FakeSse.Frame("""{"type":"response.output_text.delta","delta":"one"}"""),
        FakeSse.Frame("""{"type":"response.output_text.delta","delta":"two"}"""),
        FakeSse.Frame("""{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":3}}}"""),
    ];

    [Fact]
    public async Task Responses_measures_first_increment_to_stream_end()
    {
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(ResponsesStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Responses(handler), Request());
        watch.Stop();

        var done = Completed(events);
        Assert.Equal(2, events.OfType<TextDelta>().Count());
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }

    [Fact]
    public async Task Responses_streams_nothing_measures_zero()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":3}}}"""),
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, Pause)));

        var done = Completed(await Drain(Responses(handler), Request()));

        Assert.Equal(TimeSpan.Zero, done.StreamElapsed);
    }

    [Fact]
    public async Task Responses_cancelled_after_partial_output_never_fabricates_a_completion()
    {
        var frames = new[]
        {
            FakeSse.Frame("""{"type":"response.output_text.delta","delta":"one"}"""),
        };
        var handler = new FakeSse.Handler(_ => FakeSse.Ok(FakeSse.Body(frames, holdAtEnd: true)));
        using var cancelled = new CancellationTokenSource();

        var events = new List<ChatStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in Responses(handler).StreamAsync(Request(), cancelled.Token))
            {
                events.Add(evt);
                if (evt is TextDelta)
                {
                    await cancelled.CancelAsync();
                }
            }
        });

        Assert.Contains(events, e => e is TextDelta);
        Assert.DoesNotContain(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task Responses_downgrade_resend_measures_only_its_own_stream()
    {
        var handler = new FakeSse.Handler(attempt => attempt == 1 ? FakeSse.Refused("prompt_cache_key") : FakeSse.Ok(FakeSse.Body(ResponsesStream(), Pause)));
        var watch = Stopwatch.StartNew();
        var events = await Drain(Responses(handler, cacheKey: true), Request());
        watch.Stop();

        Assert.Equal(2, handler.Attempts);
        var done = Completed(events);
        Assert.NotNull(done.Notice);
        Assert.InRange(done.StreamElapsed.TotalMilliseconds, Pause.TotalMilliseconds, watch.Elapsed.TotalMilliseconds - 1);
    }
}
