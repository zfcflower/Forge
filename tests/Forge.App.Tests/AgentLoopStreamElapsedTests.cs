using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The accumulation chain behind <c>turn_metrics.stream_ms</c>: <see cref="AgentLoop"/> sums every completed call's
/// <see cref="MessageCompleted.StreamElapsed"/> onto <see cref="AgentInstance.StreamElapsed"/> - an instance-cumulative
/// counter exactly like <see cref="AgentInstance.Usage"/> - and <see cref="AgentTurnCompleted"/> carries the cumulative
/// value, so a turn's own share is the difference against the turn-start snapshot.
/// </summary>
public sealed class AgentLoopStreamElapsedTests
{
    /// <summary>
    /// A provider whose calls answer from a script: odd calls end with a call to a tool no host registers, so the loop
    /// records the error result and makes a second model call inside the same turn; even calls end the turn. Each
    /// completion claims a distinct, scripted streaming time.
    /// </summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public List<TimeSpan> Reported { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var call = Interlocked.Increment(ref calls);
            var elapsed = TimeSpan.FromMilliseconds(100 * call);
            Reported.Add(elapsed);
            if (call % 2 == 1)
            {
                yield return new TextDelta("partial");
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{call}", "no_such_tool", "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse)
                {
                    StreamElapsed = elapsed,
                };
            }
            else
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                    TokenUsage.Empty,
                    StopReason.EndTurn)
                {
                    StreamElapsed = elapsed,
                };
            }
        }
    }

    private static async Task<List<AgentEvent>> RunOneTurnAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, text))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public async Task A_turn_accumulates_every_calls_streaming_time()
    {
        var provider = new ScriptedProvider();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
        });
        var root = host.CreateRoot("session-stream", Directory.GetCurrentDirectory());

        var completed = (await RunOneTurnAsync(host, root, "go")).OfType<AgentTurnCompleted>().Single();

        // Two model calls inside one turn (the first answered with an unknown tool, the loop ran it as an error
        // result and asked again): 100ms + 200ms, both from the completions the provider reported.
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], provider.Reported);
        Assert.Equal(TimeSpan.FromMilliseconds(300), completed.StreamElapsed);
    }

    [Fact]
    public async Task The_counter_is_cumulative_across_turns_and_the_turn_share_is_the_snapshot_difference()
    {
        var provider = new ScriptedProvider();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
        });
        var root = host.CreateRoot("session-stream", Directory.GetCurrentDirectory());

        var first = (await RunOneTurnAsync(host, root, "one")).OfType<AgentTurnCompleted>().Single();
        var second = (await RunOneTurnAsync(host, root, "two")).OfType<AgentTurnCompleted>().Single();

        // The event carries the instance-cumulative counter (like it carries the cumulative usage), never a
        // per-turn reset: the second turn's own share is exactly the difference against the first snapshot.
        Assert.Equal(TimeSpan.FromMilliseconds(300), first.StreamElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), second.StreamElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(700), second.StreamElapsed - first.StreamElapsed);
    }
}
