using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Policies for tests written against the former absolute threshold.</summary>
internal static class CompactionWindows
{
    /// <summary>
    /// Compacts once the context reaches <paramref name="threshold"/> tokens and aims at <paramref name="targetRatio"/> of
    /// it: a window of twice the threshold leaves a usable budget of exactly the threshold (windows below 8192 reserve half),
    /// and a trigger ratio of 1 puts the trigger line on it.
    /// </summary>
    public static AgentOptions Legacy(int threshold, int maxTurns = 100, double targetRatio = 0.5) => new()
    {
        MaxTurns = maxTurns,
        Compaction = new CompactionPolicy { TriggerRatio = 1, TargetRatio = targetRatio, ContextWindow = _ => threshold * 2 },
    };
}

/// <summary>Records every compaction the loop asks for; optionally delegates to a real compactor.</summary>
internal sealed class RecordingCompactor(IContextCompactor? inner = null, Func<CompactionResult?>? result = null) : IContextCompactor
{
    private readonly List<(string AgentId, string Model, int Target, CompactionRequest Request)> calls = [];

    public IReadOnlyList<(string AgentId, string Model, int Target, CompactionRequest Request)> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public async Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct)
    {
        lock (calls)
        {
            calls.Add((instance.Id, instance.Definition.Model.Model, request.TargetTokens, request));
        }

        if (inner is not null)
        {
            return await inner.CompactAsync(instance, request, ct).ConfigureAwait(false);
        }

        return result?.Invoke();
    }
}

public class CompactionPolicyTests
{
    private const string Workspace = "D:/work/compaction";

    // A 2000-token window with no known output limit reserves half (the 4096 floor is capped at W/2): U = 1000, so the
    // default trigger line is 850 and the default target 500.
    private const int SmallWindow = 2000;

    private static AgentOptions Options(Func<ModelSettings, int?> window, int maxTurns = 10) => new()
    {
        MaxTurns = maxTurns,
        Compaction = new CompactionPolicy { ContextWindow = window },
    };

    // ---------------------------------------------------------------- arithmetic

    [Theory]
    [InlineData(200_000, null, 40_000, 160_000)] // unknown output: 20% of the window
    [InlineData(200_000, 8_192, 8_192, 191_808)] // the model's output limit when smaller
    [InlineData(200_000, 1_000, 4_096, 195_904)] // at least 4096
    [InlineData(1_000_000, 128_000, 128_000, 872_000)]
    [InlineData(1_000_000, 500_000, 200_000, 800_000)] // never more than 20%
    [InlineData(6_000, null, 3_000, 3_000)] // a tiny window keeps half for input instead of the 4096 floor
    [InlineData(2_000, 100, 1_000, 1_000)]
    public void Budget_reserves_output_and_leaves_the_rest_usable(int window, int? maxOutput, int reserve, int usable)
    {
        var budget = CompactionPolicy.Budget(window, maxOutput);

        Assert.NotNull(budget);
        Assert.Equal(window, budget.Value.Window);
        Assert.Equal(reserve, budget.Value.Reserve);
        Assert.Equal(usable, budget.Value.Usable);
        Assert.True(budget.Value.Reserve < window);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    public void An_unknown_window_has_no_budget(int? window) => Assert.Null(CompactionPolicy.Budget(window, 4096));

    [Fact]
    public void Trigger_target_and_regrowth_are_shares_of_the_usable_budget()
    {
        var budget = CompactionPolicy.Budget(200_000, null)!.Value;

        Assert.Equal(136_000, budget.TriggerTokens(0.85));
        Assert.Equal(80_000, budget.TargetTokens(0.5));
        Assert.Equal(16_000, budget.RegrowthTokens);
    }

    [Fact]
    public void BudgetFor_prefers_the_models_own_output_limit_over_the_resolver_and_survives_a_throwing_resolver()
    {
        var policy = new CompactionPolicy { ContextWindow = _ => 200_000, MaxOutputTokens = _ => 64_000 };

        Assert.Equal(40_000, policy.BudgetFor(new ModelSettings("m"))!.Value.Reserve);
        Assert.Equal(8_192, policy.BudgetFor(new ModelSettings("m", MaxOutputTokens: 8_192))!.Value.Reserve);
        Assert.Null(new CompactionPolicy().BudgetFor(new ModelSettings("m")));
        Assert.Null(new CompactionPolicy { ContextWindow = _ => throw new InvalidOperationException() }.BudgetFor(new ModelSettings("m")));
    }

    [Theory]
    [InlineData(0.85, 0.85)]
    [InlineData(0.5, 0.6)]
    [InlineData(1.1, 0.5)]
    [InlineData(0.85, 0)]
    public void Invalid_ratios_are_refused_by_the_host(double trigger, double target)
    {
        var options = new AgentOptions { Compaction = new CompactionPolicy { TriggerRatio = trigger, TargetRatio = target } };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), options: options));
    }

    // ---------------------------------------------------------------- real usage vs estimate

    [Fact]
    public async Task Real_prompt_usage_triggers_compaction_even_when_the_estimate_is_small()
    {
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(900, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "first"));
        Assert.Empty(compactor.Calls); // no usage yet: the small estimate stays under 850

        await AgentTestHost.CollectAsync(host.RunAsync(root, "second"));
        var call = Assert.Single(compactor.Calls);
        Assert.Equal(500, call.Target);
        Assert.Equal(200, call.Request.KeepRecentTokens); // min(20 000, 0.2·U) with U = 1000
        Assert.Equal(CompactionReason.Auto, call.Request.Reason);
        Assert.Null(call.Request.CustomInstructions);
    }

    [Fact]
    public async Task Real_prompt_usage_overrides_a_large_estimate()
    {
        // The first turn runs under a huge window; afterwards the window shrinks to 2000. The history estimate (~1000 tokens)
        // is above the 850 trigger, but the provider reported the prompt as 10 tokens, and that is what counts.
        var window = 1_000_000;
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(10, 5)));
        var host = AgentTestHost.Create(provider, options: Options(_ => window), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, new string('x', 4_000)));
        window = SmallWindow;
        await AgentTestHost.CollectAsync(host.RunAsync(root, "second"));

        Assert.Empty(compactor.Calls);
    }

    [Fact]
    public async Task Without_reported_usage_the_full_estimate_is_used()
    {
        var window = 1_000_000;
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", TokenUsage.Empty));
        var host = AgentTestHost.Create(provider, options: Options(_ => window), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, new string('x', 4_000)));
        window = SmallWindow;
        await AgentTestHost.CollectAsync(host.RunAsync(root, "second"));

        Assert.Single(compactor.Calls);
    }

    [Fact]
    public async Task Usage_recorded_on_one_model_does_not_carry_over_to_another()
    {
        // Same stored history, small reported usage on model A; the root rebuilt on model B starts from the full estimate.
        var store = new InMemorySessionStore();
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", new TokenUsage(10, 5)));
        var host = AgentTestHost.Create(
            provider,
            options: Options(model => model.Model == "model-b" ? SmallWindow : 1_000_000),
            store: store,
            compactor: compactor);
        var definitionA = new AgentDefinition("Root", "root", string.Empty, new ModelSettings("model-a"));
        var onA = host.CreateRoot("s", Workspace, definitionA);
        await AgentTestHost.CollectAsync(host.RunAsync(onA, new string('x', 4_000)));
        await AgentTestHost.CollectAsync(host.RunAsync(onA, "still on a"));
        Assert.Empty(compactor.Calls);

        var onB = await host.ResumeRootAsync("s", Workspace, CancellationToken.None, definitionA with { Model = new ModelSettings("model-b") });
        await AgentTestHost.CollectAsync(host.RunAsync(onB, "now on b"));

        var call = Assert.Single(compactor.Calls);
        Assert.Equal("model-b", call.Model);
    }

    [Fact]
    public async Task Unknown_window_or_disabled_policy_never_compacts_automatically()
    {
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say(new string('y', 8_000), TokenUsage.Empty));
        var unknown = AgentTestHost.Create(provider, options: Options(_ => null), compactor: compactor);
        var disabled = AgentTestHost.Create(
            provider,
            options: new AgentOptions { Compaction = new CompactionPolicy { Enabled = false, ContextWindow = _ => SmallWindow } },
            compactor: compactor);

        foreach (var host in new[] { unknown, disabled })
        {
            var root = host.CreateRoot("s", Workspace);
            for (var turn = 0; turn < 3; turn++)
            {
                await AgentTestHost.CollectAsync(host.RunAsync(root, "turn " + turn));
            }
        }

        Assert.Empty(compactor.Calls);
    }

    // ---------------------------------------------------------------- hysteresis

    [Fact]
    public async Task An_inherited_prefix_over_the_trigger_line_is_not_recompacted_every_turn()
    {
        // The defect: the inherited prefix alone (~1000 tokens) exceeds the child's trigger line (850), so before this policy
        // every request re-ran the compactor. Now a fruitless attempt waits for 0.1·U (100 tokens) of own growth.
        var store = new LineageTestStore();
        var ct = CancellationToken.None;
        var parent = await store.CreateSessionAsync(Workspace, "parent", ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("parent question"), null, ct);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant(new string('p', 4_000)), null, ct);
        var child = await store.InheritAsync(parent);

        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say("CHILD-SUMMARY") : Turn.Say("ok", TokenUsage.Empty));
        var compactor = new RecordingCompactor(new SummaryCompactor(store, _ => provider, new ApproximateTokenCounter()));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), store: store, compactor: compactor);
        var root = await host.ResumeRootAsync(child.Id, Workspace, ct);
        var inherited = root.History.Count;

        var stalled = new List<AgentCompactionStalled>();
        for (var turn = 1; turn <= 6; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "t" + turn));
            Assert.Empty(events.OfType<AgentFailed>());
            stalled.AddRange(events.OfType<AgentCompactionStalled>());
        }

        Assert.Single(compactor.Calls);
        Assert.Empty(stalled);
        Assert.Empty(await store.GetSummariesAsync(parent.Id, ct));
        Assert.Equal(inherited, root.History.TakeWhile(m => Turn.Text(m) != "t1").Count());
    }

    [Fact]
    public async Task Two_fruitless_attempts_stop_automatic_compaction_until_a_manual_one()
    {
        // Every turn adds ~110 own tokens (above the 100-token regrowth), and the compactor never shrinks anything.
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say(new string('z', 400), TokenUsage.Empty));
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var stalled = new List<AgentCompactionStalled>();
        async Task TurnAsync(string text)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, text));
            stalled.AddRange(events.OfType<AgentCompactionStalled>());
        }

        await TurnAsync(new string('x', 3_600)); // ~900 tokens: the first check is already over 850
        Assert.Single(compactor.Calls);
        await TurnAsync("second");
        Assert.Equal(2, compactor.Calls.Count);
        var notice = Assert.Single(stalled);
        Assert.Equal(root.Id, notice.AgentId);
        Assert.Equal(850, notice.TriggerTokens);
        Assert.True(notice.ContextTokens >= 850);

        for (var turn = 0; turn < 3; turn++)
        {
            await TurnAsync("more " + turn);
        }

        Assert.Equal(2, compactor.Calls.Count);
        Assert.Single(stalled);

        // A manual compaction re-enables automatic attempts.
        Assert.Null(await host.CompactAsync(root, null, CancellationToken.None));
        Assert.Equal(1, compactor.Calls[^1].Target);
        await TurnAsync("after manual");
        Assert.Equal(4, compactor.Calls.Count);
    }

    [Fact]
    public async Task A_successful_compaction_resets_the_fruitless_count()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "reset", CancellationToken.None);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say("S") : Turn.Say(new string('a', 1_200), TokenUsage.Empty));
        var compactor = new RecordingCompactor(new SummaryCompactor(store, _ => provider, new ApproximateTokenCounter()));
        // The built-in tool definitions alone would fill most of U = 1000 in the estimate: the root gets no tools.
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow) with { MaxSubAgentDepth = 0 }, store: store, compactor: compactor);
        var root = host.CreateRoot(session.Id, directory.Path, AgentTestHost.Definition("Root", tools: []));

        var stalled = 0;
        var compacted = 0;
        for (var turn = 1; turn <= 8; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "turn " + turn));
            Assert.Empty(events.OfType<AgentFailed>());
            stalled += events.OfType<AgentCompactionStalled>().Count();
            compacted += events.OfType<AgentCompacted>().Count();
        }

        // Each answer is ~300 tokens: compaction keeps succeeding, so it never stalls.
        Assert.True(compacted >= 2);
        Assert.Equal(0, stalled);
        Assert.All(compactor.Calls, call => Assert.Equal(500, call.Target));
    }

    // ---------------------------------------------------------------- overflow fallback

    private static ProviderException Overflow() =>
        new("anthropic returned 400 BadRequest: {\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 210000 tokens > 200000 maximum\"}}", retryable: false, statusCode: 400);

    [Fact]
    public async Task Overflow_compacts_once_and_resends_the_request_once()
    {
        var compactor = new RecordingCompactor(result: () => new CompactionResult(100, 50));
        var main = 0;
        var provider = new FakeProvider((_, _) => Interlocked.Increment(ref main) == 1 ? throw Overflow() : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, options: Options(_ => null), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Single(events.OfType<AgentCompacted>());
        var call = Assert.Single(compactor.Calls);
        Assert.Equal(CompactionReason.Overflow, call.Request.Reason);
        Assert.True(call.Request.KeepRecentTokens <= Math.Max(1, call.Target / 2), "the overflow path keeps at most half its target raw");
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("done", Turn.Text(root.History[^1]));
    }

    [Fact]
    public async Task A_second_overflow_is_reported_as_the_provider_error()
    {
        var compactor = new RecordingCompactor(result: () => new CompactionResult(100, 50));
        var provider = new FakeProvider((_, _) => throw Overflow());
        var host = AgentTestHost.Create(provider, options: Options(_ => SmallWindow), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        var failed = Assert.Single(events.OfType<AgentFailed>());
        Assert.Contains("prompt is too long", failed.Error, StringComparison.Ordinal);
        Assert.Single(compactor.Calls);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Overflow_with_nothing_to_compact_is_not_resent()
    {
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => throw Overflow());
        var host = AgentTestHost.Create(provider, options: Options(_ => null), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        Assert.Single(events.OfType<AgentFailed>());
        Assert.Single(compactor.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Other_provider_errors_do_not_compact()
    {
        var compactor = new RecordingCompactor(result: () => new CompactionResult(100, 50));
        var provider = new FakeProvider((_, _) => throw new ProviderException("openai returned 400 BadRequest: invalid tool schema", false, 400));
        var host = AgentTestHost.Create(provider, options: Options(_ => null), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        Assert.Single(events.OfType<AgentFailed>());
        Assert.Empty(compactor.Calls);
        Assert.Single(provider.Requests);
    }

    // ---------------------------------------------------------------- manual

    [Fact]
    public async Task Manual_compaction_ignores_the_threshold_and_the_switch_and_keeps_the_recent_tokens()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "manual", CancellationToken.None);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? Turn.Say("MANUAL-SUMMARY")
                : Turn.Say(request.Messages.Count + new string('a', 400), TokenUsage.Empty));
        // Automatic compaction is off; U = 1000, so keepRecent = 200 tokens: the last interaction (~110 tokens) stays raw,
        // the one before it (~110 more) would not fit as well.
        var options = new AgentOptions { Compaction = new CompactionPolicy { Enabled = false, ContextWindow = _ => SmallWindow } };
        var host = AgentTestHost.Create(provider, options: options, store: store);
        var root = host.CreateRoot(session.Id, directory.Path);
        for (var turn = 1; turn <= 3; turn++)
        {
            Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, "turn " + turn))).OfType<AgentCompacted>());
        }

        var lastAnswer = root.History[^1];
        var sink = new ListSink();

        var result = await host.CompactAsync(root, sink, CancellationToken.None);

        Assert.NotNull(result);
        var compacted = Assert.Single(sink.Events.OfType<AgentCompacted>());
        Assert.Equal(result.TokensAfter, compacted.TokensAfter);
        Assert.Equal(3, root.History.Count);
        Assert.Contains("MANUAL-SUMMARY", Turn.Text(root.History[0]), StringComparison.Ordinal);
        Assert.Equal("turn 3", Turn.Text(root.History[1]));
        Assert.Equal(Turn.Text(lastAnswer), Turn.Text(root.History[^1]));
        var summary = Assert.Single(await store.GetSummariesAsync(session.Id, CancellationToken.None));
        Assert.Equal(4, summary.ToSeq);
    }

    [Fact]
    public async Task Manual_compaction_with_an_unknown_window_keeps_twenty_thousand_tokens()
    {
        var compactor = new RecordingCompactor();
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), compactor: compactor);
        var root = host.CreateRoot("s", Workspace);
        await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        Assert.Null(await host.CompactAsync(root, null, "  keep the API notes  ", CancellationToken.None));

        var request = Assert.Single(compactor.Calls).Request;
        Assert.Equal(new CompactionRequest(1, CompactionPolicy.MaxKeepRecentTokens, CompactionReason.Manual, "keep the API notes"), request);
    }

    [Fact]
    public async Task Manual_compaction_without_a_store_does_nothing()
    {
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")));
        var root = host.CreateRoot("s", Workspace);
        await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));

        Assert.Null(await host.CompactAsync(root, null, CancellationToken.None));
        Assert.Equal(2, root.History.Count);
    }

    // ---------------------------------------------------------------- sub-agents

    [Fact]
    public async Task A_sub_agent_is_measured_against_its_own_models_window()
    {
        var scout = new AgentDefinition("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);
        var task = new string('t', 4_000); // ~1000 tokens: over the scout's 850 line, nowhere near the root's
        var asked = new List<string>();
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((request, index) =>
            Turn.LastUserText(request) == task
                ? Turn.Say("child report", TokenUsage.Empty)
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", "{\"kind\":\"Scout\",\"task\":\"" + task + "\"}"))
                    : Turn.Say("parent done", TokenUsage.Empty));
        var options = Options(model =>
        {
            lock (asked)
            {
                asked.Add(model.Model);
            }

            return model.Model == "scout-model" ? SmallWindow : 1_000_000;
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: options, agents: [scout], compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        var events = await host.RunToCompletionAsync(root);
        var call = Assert.Single(compactor.Calls);
        Assert.Equal("scout-model", call.Model);
        Assert.NotEqual(root.Id, call.AgentId);
        Assert.Equal(500, call.Target);
        Assert.Contains("scout-model", asked);
    }

    private sealed class ListSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Emit(AgentEvent value) => Events.Add(value);
    }
}

public class ProviderOverflowClassificationTests
{
    [Theory]
    [InlineData("anthropic returned 400 BadRequest: {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 201234 tokens > 200000 maximum\"}}")]
    [InlineData("openai returned 400 BadRequest: {\"error\":{\"message\":\"This model's maximum context length is 128000 tokens.\",\"code\":\"context_length_exceeded\"}}")]
    [InlineData("Responses stream failed: context_length_exceeded")]
    [InlineData("Your input exceeds the context window of this model.")]
    [InlineData("Input is too long for requested model.")]
    public void Overflow_messages_are_recognised(string message) =>
        Assert.True(ProviderErrors.IsContextOverflow(new ProviderException(message, retryable: false, statusCode: 400)));

    [Fact]
    public void Status_413_and_the_explicit_flag_count_as_overflow()
    {
        Assert.True(ProviderErrors.IsContextOverflow(new ProviderException("too large", false, 413)));
        var flagged = new ProviderException("whatever", false, 400);
        flagged.Data[ProviderErrors.ContextOverflowKey] = true;
        Assert.True(ProviderErrors.IsContextOverflow(flagged));
        Assert.True(ProviderErrors.IsContextOverflow(new InvalidOperationException("wrap", new ProviderException("prompt is too long", false, 400))));
    }

    [Theory]
    [InlineData("anthropic returned 429 TooManyRequests: rate limit: too many input tokens per minute", 429, true)]
    [InlineData("server overloaded: prompt is too long to process right now", 529, true)]
    [InlineData("openai returned 400 BadRequest: invalid tool schema", 400, false)]
    [InlineData("openai returned 401 Unauthorized", 401, false)]
    public void Rate_limits_retryable_and_unrelated_errors_are_not_overflow(string message, int status, bool retryable) =>
        Assert.False(ProviderErrors.IsContextOverflow(new ProviderException(message, retryable, status)));

    [Fact]
    public void Non_provider_exceptions_are_not_overflow()
    {
        Assert.False(ProviderErrors.IsContextOverflow(null));
        Assert.False(ProviderErrors.IsContextOverflow(new InvalidOperationException("prompt is too long")));
    }
}
