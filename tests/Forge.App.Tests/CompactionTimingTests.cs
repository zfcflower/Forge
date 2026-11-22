using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// When a compaction may interrupt a turn. A turn is a run of model requests collecting tool results; compacting in the
/// middle of one summarises the very evidence the turn is building. So the trigger line belongs to the first request of a
/// turn, and a turn already running compacts only at the emergency line near the window.
/// </summary>
public sealed class CompactionTimingTests
{
    /// <summary>Answers the first request of every turn with a call to <c>big</c>, and any later one by ending the turn.</summary>
    private sealed class ToolThenDoneProvider(RecordingCompactor compactor) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        /// <summary>How many compactions had already run when each request went out.</summary>
        public List<int> CompactionsBeforeCall { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            CompactionsBeforeCall.Add(compactor.Requests.Count);
            var call = Interlocked.Increment(ref calls);
            if (call % 2 == 1)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{call}", "big", "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                    TokenUsage.Empty,
                    StopReason.EndTurn);
            }
        }
    }

    /// <summary>A tool whose result carries the context across the trigger line inside one turn.</summary>
    private sealed class BigTool : ToolBase
    {
        public BigTool()
            : base("big", "Returns a large result.", """{"type":"object","properties":{},"additionalProperties":false}""", ToolSideEffect.ReadOnly)
        {
        }

        // 108 000 characters are 27 000 tokens: wide enough that the host's own tool descriptions cannot decide the test.
        protected override ValueTask<ToolResult> InvokeCoreAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken ct) => new(new ToolResult(new string('x', 108_000)));
    }

    private sealed class RecordingCompactor : IContextCompactor
    {
        public List<CompactionRequest> Requests { get; } = [];

        public Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult<CompactionResult?>(null);
        }
    }

    /// <summary>Ends every turn with one large assistant message: nothing the reduction rung is allowed to crop.</summary>
    private sealed class BigTextProvider(int chars) : IChatProvider
    {
        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock(new string('x', chars))]),
                TokenUsage.Empty,
                StopReason.EndTurn);
        }
    }

    /// <summary>A tool nobody calls: its description is context no summary can drop.</summary>
    private sealed class DescriptionTool(int characters)
        : ToolBase(
            "huge",
            new string('d', characters),
            """{"type":"object","properties":{},"additionalProperties":false}""",
            ToolSideEffect.ReadOnly)
    {
        protected override ValueTask<ToolResult> InvokeCoreAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken ct) => new(new ToolResult("ok"));
    }

    private static async Task<List<AgentEvent>> RunTurnAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, text))
        {
            events.Add(evt);
        }

        return events;
    }

    private static (AgentHost Host, ToolThenDoneProvider Provider, RecordingCompactor Compactor) Subject()
    {
        var compactor = new RecordingCompactor();
        var provider = new ToolThenDoneProvider(compactor);
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [new BigTool()],
            Layers = new PromptLayers("sys", null, null),
            Compactor = compactor,
            Options = new AgentOptions
            {
                // 40000-token window with the product defaults: R = 8000, U = 32000, trigger 27200, mid-turn line 30400.
                Compaction = new CompactionPolicy
                {
                    ContextWindow = _ => 40000,
                    MaxOutputTokens = _ => null,
                },
            },
        });
        return (host, provider, compactor);
    }

    [Fact]
    public async Task A_running_turn_keeps_its_tool_results_and_only_the_next_turn_compacts()
    {
        var (host, provider, compactor) = Subject();
        var root = host.CreateRoot("session-timing", Directory.GetCurrentDirectory());

        // Turn 1 makes two requests: the first is answered with a tool call, the second ends the turn. The tool result pushes
        // the context past the trigger, but both requests go out with no compaction behind them — the turn keeps its evidence.
        var first = await RunTurnAsync(host, root, "go");
        Assert.Equal([0, 0], provider.CompactionsBeforeCall);
        Assert.Empty(compactor.Requests);

        // After the turn the context sits above the trigger and below the mid-turn line: exactly the state the next turn has
        // to clean up before its own first request.
        var measured = first.OfType<AgentContextMeasured>().Last();
        Assert.Equal(32_000, measured.UsableTokens);
        Assert.Equal(27_200, measured.TriggerTokens);
        Assert.InRange(measured.ContextTokens, 27_201, 30_399);

        // Turn 2 starts above the trigger, so its first request compacts first. That compaction changes nothing (the double
        // reports no result), the turn's tool batch grows the context past the mid-turn line, and the request after it
        // crops the oldest result into a receipt — the reduction rung serves the mid-turn line without a summarizer
        // request, which is the other half of the rule: a running turn only yields once it is near the window.
        var second = await RunTurnAsync(host, root, "again");
        Assert.Equal([0, 0, 1, 1], provider.CompactionsBeforeCall);
        var auto = Assert.Single(compactor.Requests);
        Assert.Equal(CompactionReason.Auto, auto.Reason);
        var reduced = Assert.Single(second.OfType<AgentContextReduced>());
        Assert.True(reduced.TokensAfter < 30_400, $"the mid-turn line is 30400, cropped to {reduced.TokensAfter}");
    }

    /// <summary>The same 40 000-token window, with one tool whose description is the only large thing in the context.</summary>
    private static AgentHost FloorHost(IChatProvider provider, RecordingCompactor compactor, ITool tool) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [tool],
            Layers = new PromptLayers("sys", null, null),
            Compactor = compactor,
            Options = new AgentOptions
            {
                // Same budget as <see cref="Subject"/>: U = 32 000, trigger line 27 200, target 16 000, keepRecent 6 400.
                Compaction = new CompactionPolicy
                {
                    ContextWindow = _ => 40000,
                    MaxOutputTokens = _ => null,
                },
            },
        });

    [Fact]
    public async Task A_floor_above_the_line_stalls_before_any_summarizer_request()
    {
        // ~50 000 tokens of tool description against a 40 000-token window: the floor is above the 27 200 trigger line, so no
        // summary can bring the context under it. (This harness cannot build an inherited prefix; the tool definitions are the
        // floor's other fixed term and drive the same branch.)
        var compactor = new RecordingCompactor();
        var host = FloorHost(new BigTextProvider(400), compactor, new DescriptionTool(200_000));
        var root = host.CreateRoot("session-floor", Directory.GetCurrentDirectory());

        var first = await RunTurnAsync(host, root, "go");
        Assert.Empty(compactor.Requests);
        var stalled = Assert.Single(first.OfType<AgentCompactionStalled>());
        Assert.Equal(CompactionStallReason.Irreducible, stalled.Reason);

        // Nothing about the numbers changed, so the next turn neither retries the summarizer nor repeats the notice.
        var second = await RunTurnAsync(host, root, "again");
        Assert.Empty(compactor.Requests);
        Assert.DoesNotContain(second, evt => evt is AgentCompactionStalled);
    }

    [Fact]
    public async Task A_floor_above_the_target_but_below_the_line_still_asks_the_summarizer()
    {
        // ~15 000 tool tokens plus the 6 400 keepRecent window is above the 16 000 target but below the 27 200 line, and the
        // big assistant message is not a tool result, so the reduction rung cannot crop it: the summary is the only way down,
        // and "the target is out of reach" must never be read as "don't try".
        var compactor = new RecordingCompactor();
        var host = FloorHost(new BigTextProvider(120_000), compactor, new DescriptionTool(60_000));
        var root = host.CreateRoot("session-floor-target", Directory.GetCurrentDirectory());

        await RunTurnAsync(host, root, "go");
        Assert.Empty(compactor.Requests);

        await RunTurnAsync(host, root, "again");
        var request = Assert.Single(compactor.Requests);
        Assert.Equal(CompactionReason.Auto, request.Reason);
        Assert.Equal(16_000, request.TargetTokens);
        Assert.Equal(6_400, request.KeepRecentTokens);
    }
}
