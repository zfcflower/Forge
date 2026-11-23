using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The strip moves once per request, not once per turn, and the event that makes that possible is the context measurement:
/// every one of them carries the instance's cumulative usage, taken after the request's own usage landed on the counter. The
/// per-call figures below are what a shell differences against its own reading, so they are pinned as exact values.
/// </summary>
public sealed class AgentLoopContextUsageTests
{
    /// <summary>
    /// Answers from a script: the first call ends on a call to a tool no host registers (the loop records the error result and
    /// asks again inside the same turn), the second ends the turn. Each call reports its own usage, so the counter moves twice.
    /// </summary>
    private sealed class UsageReportingProvider : IChatProvider
    {
        private int calls;

        public string Id => "scripted-usage";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var call = Interlocked.Increment(ref calls);
            var usage = call == 1 ? new TokenUsage(100, 10, 90, 0) : new TokenUsage(30, 5, 20, 0);
            if (call == 1)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c1", "no_such_tool", "{}")]),
                    usage,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                    usage,
                    StopReason.EndTurn);
            }
        }
    }

    [Fact]
    public async Task Every_context_measurement_carries_the_cumulative_usage_of_the_request_it_measured()
    {
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => new UsageReportingProvider(),
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
        });
        var root = host.CreateRoot("session-context-usage", Directory.GetCurrentDirectory());

        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, "go"))
        {
            events.Add(evt);
        }

        // One measurement per request, each carrying the counter as it stood after that request: 100 then 100+30.
        Assert.Equal(
            [new TokenUsage(100, 10, 90, 0), new TokenUsage(130, 15, 110, 0)],
            events.OfType<AgentContextMeasured>().Select(measured => measured.Usage));

        // The turn's completion carries the same cumulative figure, so a shell's difference over it is exactly zero.
        Assert.Equal(new TokenUsage(130, 15, 110, 0), events.OfType<AgentTurnCompleted>().Single().Usage);
    }
}
