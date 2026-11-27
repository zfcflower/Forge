using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The transcript's per-turn statistics are the provider's numbers, so nothing the harness adds around a request may reach
/// them. The budget line rides along as a trailing user message, marked with the <c>forge-context</c> pair that tells it —
/// to the model and to a log analysis alike — apart from anything the user typed: it must go out with every request, must
/// never be persisted (a replay would otherwise show a message no user typed), and must not change the usage a turn card
/// reports.
/// </summary>
public sealed class TurnStatisticsTests
{
    /// <summary>A tool call on the first request, an answer on the second, each reporting a fixed, distinct usage.</summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private static readonly TokenUsage First = new(1_234, 10, 100, 50);
        private static readonly TokenUsage Second = new(2_345, 20, 200, 60);

        private int calls;

        public string Id => "scripted";

        /// <summary>The budget line the request ended with, or null when its last message is ordinary history.</summary>
        public List<string?> TrailingLines { get; } = [];

        /// <summary>The message right before the last one: the carrier, or whatever history happened to be there.</summary>
        public List<string> SecondToLast { get; } = [];

        public List<int> MessageCounts { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var call = Interlocked.Increment(ref calls);
            MessageCounts.Add(request.Messages.Count);
            var last = request.Messages[^1];
            var text = last.Content.OfType<TextBlock>().LastOrDefault()?.Text ?? string.Empty;
            TrailingLines.Add(
                last.Role == MessageRole.User && text.Contains("auto-compaction at", StringComparison.Ordinal) ? text : null);
            var previous = request.Messages.Count >= 2 ? request.Messages[^2] : null;
            SecondToLast.Add(
                previous is null
                    ? "<none>"
                    : previous.Role + ": " + (previous.Content.OfType<TextBlock>().LastOrDefault()?.Text ?? "<blocks>"));
            if (call == 1)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{call}", "no_such_tool", "{}")]),
                    First,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                    Second,
                    StopReason.EndTurn);
            }
        }
    }

    private const string BudgetLine = "Context 1k/2k (50%); auto-compaction at 1.7k.";

    /// <summary>The text the request's last message carries for a budget line: the line itself between the context markers.</summary>
    private static string Marked(string line) => $"<forge-context>\n{line}\n</forge-context>";

    /// <summary>Both requests report these; a turn card shows their sum, whatever else the harness appends.</summary>
    private static readonly TokenUsage TurnUsage = new(3_579, 30, 300, 110);

    private static (AgentHost Host, ScriptedProvider Provider) Subject(Func<string, string?>? dynamicContext)
    {
        var provider = new ScriptedProvider();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            DynamicContext = dynamicContext,
        });
        return (host, provider);
    }

    private static async Task<AgentTurnCompleted> RunTurnAsync(AgentHost host, AgentInstance root)
    {
        AgentTurnCompleted? completed = null;
        await foreach (var evt in host.RunAsync(root, "go"))
        {
            if (evt is AgentTurnCompleted done)
            {
                completed = done;
            }
        }

        return Assert.IsType<AgentTurnCompleted>(completed);
    }

    [Fact]
    public async Task The_budget_line_rides_with_every_request_without_touching_the_history_or_a_turns_usage()
    {
        var (host, provider) = Subject(_ => BudgetLine);
        var root = host.CreateRoot("session-stats", Directory.GetCurrentDirectory());

        var completed = await RunTurnAsync(host, root);

        // The line went out with both requests of the turn, appended after the persisted history, marked as harness text.
        Assert.Equal([Marked(BudgetLine), Marked(BudgetLine)], provider.TrailingLines);

        // And it is its own block: a fixed carrier line sits between the request's own last message and the tail, so the two
        // never merge into one wire message. Without it the tail welded onto the opening question, and the next request -
        // where the tail follows a tool result - left that earlier block changed, invalidating the whole cached prefix.
        Assert.Equal(
            ["Assistant: " + AgentLoop.ContextCarrier, "Assistant: " + AgentLoop.ContextCarrier],
            provider.SecondToLast);

        // It is not history: neither the replay nor the next request's cached prefix can hold it.
        Assert.DoesNotContain(
            root.History.SelectMany(static message => message.Content).OfType<TextBlock>(),
            static block => block.Text.Contains("auto-compaction at", StringComparison.Ordinal));

        // The turn card's numbers are exactly the provider's, summed: the trailing line adds no tokens of its own.
        Assert.Equal(TurnUsage, completed.Usage);
    }

    [Fact]
    public async Task Without_the_line_no_request_carries_it_and_the_usage_is_the_same()
    {
        var (host, provider) = Subject(null);
        var root = host.CreateRoot("session-stats-off", Directory.GetCurrentDirectory());

        var completed = await RunTurnAsync(host, root);

        // Both requests end on ordinary history (the tool result), and the turn still reports what the provider said.
        Assert.Equal([null, null], provider.TrailingLines);
        // No tail, no carrier: the request this turn sends is byte-identical to one from a host that never had the feature.
        Assert.DoesNotContain(
            provider.SecondToLast,
            text => text.Contains(AgentLoop.ContextCarrier, StringComparison.Ordinal));
        Assert.Equal(TurnUsage, completed.Usage);
    }
}
