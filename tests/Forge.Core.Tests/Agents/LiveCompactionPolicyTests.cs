using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Pins <see cref="AgentHost.SetCompactionPolicy"/> (a policy swapped on a live host reaches the next model request, an
/// invalid one is refused and the old one stays) and <see cref="AgentContextMeasured"/> (C against U and the trigger line,
/// reported after every model request and every compaction, the budget null for an unknown window).
/// </summary>
public sealed class LiveCompactionPolicyTests
{
    private const string Workspace = "D:/work/live-compaction";

    // A 2000-token window with no known output limit reserves half: U = 1000.
    private const int SmallWindow = 2000;

    private static AgentOptions Options(Func<ModelSettings, int?>? window, double trigger = 0.85) => new()
    {
        MaxTurns = 10,
        Compaction = new CompactionPolicy { ContextWindow = window, TriggerRatio = trigger },
    };

    // ---------------------------------------------------------------- SetCompactionPolicy

    [Fact]
    public void An_invalid_or_null_policy_is_refused_and_the_current_one_stays()
    {
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), options: Options(_ => SmallWindow));
        var initial = host.CompactionPolicy;

        Assert.Throws<ArgumentOutOfRangeException>(() => host.SetCompactionPolicy(new CompactionPolicy { TriggerRatio = 1.1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.SetCompactionPolicy(new CompactionPolicy { TriggerRatio = 0.5, TargetRatio = 0.6 }));
        Assert.Throws<ArgumentNullException>(() => host.SetCompactionPolicy(null!));

        Assert.Same(initial, host.CompactionPolicy);
        Assert.Same(initial, host.Options.Compaction);

        var replacement = new CompactionPolicy { TriggerRatio = 0.7, ContextWindow = _ => SmallWindow };
        host.SetCompactionPolicy(replacement);
        Assert.Same(replacement, host.CompactionPolicy);
        // The construction options stay as they were: they are the initial policy only.
        Assert.Same(initial, host.Options.Compaction);
    }

    [Fact]
    public async Task The_next_turn_compacts_at_the_new_trigger_line()
    {
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(900, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow, trigger: 0.95), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "first"));
        await AgentTestHost.CollectAsync(host.RunAsync(root, "second"));
        Assert.Empty(compactor.Calls); // C ~ 900 stays under the 950 line

        host.SetCompactionPolicy(new CompactionPolicy { ContextWindow = _ => SmallWindow, TriggerRatio = 0.85 });
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "third"));

        Assert.Empty(events.OfType<AgentFailed>());
        var call = Assert.Single(compactor.Calls);
        Assert.Equal(CompactionReason.Auto, call.Request.Reason);
        Assert.Equal(500, call.Target);

        // The other way round: switching compaction off stops it on the next request.
        host.SetCompactionPolicy(new CompactionPolicy { ContextWindow = _ => SmallWindow, Enabled = false });
        await AgentTestHost.CollectAsync(host.RunAsync(root, "fourth"));
        Assert.Single(compactor.Calls);
    }

    [Fact]
    public async Task A_policy_set_while_a_turn_runs_applies_to_that_turns_next_request()
    {
        var compactor = new RecordingCompactor();
        AgentHost? host = null;
        var provider = new FakeProvider((_, index) =>
        {
            if (index > 0)
            {
                return Turn.Say("done", new TokenUsage(950, 5));
            }

            // The settings change lands while the first request is in flight; the tool round that follows must see it.
            host!.SetCompactionPolicy(new CompactionPolicy { ContextWindow = _ => SmallWindow });
            var call = new ChatMessage(
                MessageRole.Assistant,
                [new TextBlock("calling"), new ToolCallBlock("c1", "echo", "{}")]);
            return [new MessageCompleted(call, new TokenUsage(900, 5), StopReason.ToolUse)];
        });
        host = AgentTestHost.Create(provider, [FakeTool.Echo("echo")], options: Options(_ => 1_000_000), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var compaction = Assert.Single(compactor.Calls);
        Assert.Equal(CompactionReason.Auto, compaction.Request.Reason);
        Assert.Equal(2, provider.Requests.Count);
    }

    // ---------------------------------------------------------------- AgentContextMeasured

    [Fact]
    public async Task Every_model_request_reports_the_context_against_the_usable_budget()
    {
        var provider = new FakeProvider((_, index) => index == 0
            ? [new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock("c1", "echo", "{}")]),
                new TokenUsage(400, 5),
                StopReason.ToolUse)]
            : Turn.Say("ok", new TokenUsage(600, 5)));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("echo")], options: Options(_ => SmallWindow));
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var measures = events.OfType<AgentContextMeasured>().ToList();
        Assert.Equal(2, measures.Count);
        Assert.All(measures, measure =>
        {
            Assert.Equal(root.Id, measure.AgentId);
            Assert.Equal(1000, measure.UsableTokens);
            Assert.Equal(850, measure.TriggerTokens);
        });

        // C is the provider's prompt size plus an estimate of what the answer appended: never below the reported prompt,
        // and it follows the second, larger request.
        Assert.InRange(measures[0].ContextTokens, 400, 599);
        Assert.InRange(measures[1].ContextTokens, 600, 799);

        // The report follows the request, before the turn ends.
        var measuredAt = events.FindLastIndex(static e => e is AgentContextMeasured);
        var completedAt = events.FindIndex(static e => e is AgentTurnCompleted);
        Assert.True(completedAt < 0 || measuredAt < completedAt);
    }

    [Fact]
    public async Task An_unknown_window_reports_the_tokens_without_a_budget()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(1_234, 5)));
        var host = AgentTestHost.Create(provider, options: Options(null));
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var measure = Assert.Single(events.OfType<AgentContextMeasured>());
        Assert.True(measure.ContextTokens >= 1_234);
        Assert.Null(measure.UsableTokens);
        Assert.Null(measure.TriggerTokens);
    }

    [Fact]
    public async Task The_report_uses_the_policy_in_force()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(100, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow));
        var root = host.CreateRoot("s", Workspace);

        host.SetCompactionPolicy(new CompactionPolicy { ContextWindow = _ => SmallWindow, TriggerRatio = 0.6, TargetRatio = 0.3 });
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var measure = Assert.Single(events.OfType<AgentContextMeasured>());
        Assert.Equal(1000, measure.UsableTokens);
        Assert.Equal(600, measure.TriggerTokens);
    }

    [Fact]
    public async Task An_automatic_compaction_reports_the_shrunk_context_after_its_notice()
    {
        // The compactor claims success without changing the history: the report must still follow the notice and carry
        // the loop's own measurement (not the compactor's claim), against the same budget.
        var compactor = new RecordingCompactor(result: static () => new CompactionResult(950, 120));
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(900, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "first"));
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "second"));

        var compactedAt = events.FindIndex(static e => e is AgentCompacted);
        Assert.True(compactedAt >= 0, "the second turn did not compact");
        var after = Assert.IsType<AgentContextMeasured>(events[compactedAt + 1]);
        Assert.Equal(root.Id, after.AgentId);
        Assert.Equal(1000, after.UsableTokens);
        Assert.Equal(850, after.TriggerTokens);
        Assert.True(after.ContextTokens >= 900);
    }

    [Fact]
    public async Task A_manual_compaction_reports_the_context_after_its_notice()
    {
        var compactor = new RecordingCompactor(result: static () => new CompactionResult(400, 80));
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(300, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);
        await AgentTestHost.CollectAsync(host.RunAsync(root, "first"));

        var sink = new ListSink();
        var result = await host.CompactAsync(root, sink, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, sink.Events.Count);
        var compacted = Assert.IsType<AgentCompacted>(sink.Events[0]);
        Assert.Equal((400, 80), (compacted.TokensBefore, compacted.TokensAfter));
        var measured = Assert.IsType<AgentContextMeasured>(sink.Events[1]);
        Assert.Equal(1000, measured.UsableTokens);
        Assert.Equal(850, measured.TriggerTokens);

        // Nothing compacted: no notice and no report. A null sink is allowed.
        var nothing = new ListSink();
        var none = new RecordingCompactor();
        var quiet = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: none);
        var other = quiet.CreateRoot("s2", Workspace);
        Assert.Null(await quiet.CompactAsync(other, nothing, CancellationToken.None));
        Assert.Empty(nothing.Events);
        Assert.NotNull(await host.CompactAsync(root, null, CancellationToken.None));
    }

    private sealed class ListSink : IAgentEventSink
    {
        private readonly List<AgentEvent> events = [];

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (events)
                {
                    return [.. events];
                }
            }
        }

        public void Emit(AgentEvent value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }
    }
}
