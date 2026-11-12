using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class CompactionTests
{
    private const string SummaryText = "SUMMARY-OF-OLD-MESSAGES";

    [Fact]
    public async Task Threshold_compacts_into_a_session_summary_from_the_start_and_keeps_the_originals()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "compaction", CancellationToken.None);
        var answer = new string('a', 800);
        var provider = new FakeProvider((request, index) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? Turn.Say(SummaryText)
                : Turn.Say(answer, TokenUsage.Empty));
        var options = CompactionWindows.Legacy(200, maxTurns: 4);
        var host = AgentTestHost.Create(provider, options: options, store: store);
        var root = host.CreateRoot(session.Id, directory.Path);

        var compactions = new List<AgentCompacted>();
        for (var turn = 1; turn <= 4; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"));
            Assert.Empty(events.OfType<AgentFailed>());
            compactions.AddRange(events.OfType<AgentCompacted>());
        }

        Assert.NotEmpty(compactions);
        Assert.All(compactions, compaction =>
        {
            Assert.Equal(root.Id, compaction.AgentId);
            Assert.True(compaction.TokensAfter < compaction.TokensBefore);
        });

        var summaries = await store.GetSummariesAsync(session.Id, CancellationToken.None);
        Assert.NotEmpty(summaries);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(SummaryLayers.SessionLayer, summary.Layer);
            Assert.Equal(SummaryText, summary.Text);
            Assert.Equal(1, summary.FromSeq);
            Assert.True(summary.ToSeq >= summary.FromSeq);
        });

        var stored = await AgentTestHost.CollectAsync(store.ReadAsync(session.Id, 1, null, CancellationToken.None));
        Assert.Equal(8, stored.Count);
        Assert.Equal("turn 1", MessageJson.ExtractFullText(stored[0].Message));

        Assert.Equal(MessageRole.User, root.History[0].Role);
        Assert.Contains(SummaryText, Turn.Text(root.History[0]), StringComparison.Ordinal);
        Assert.True(root.History.Count < stored.Count);
        Assert.DoesNotContain(root.History, message => Turn.Text(message) == "turn 1");
        Assert.Equal(MessageJson.ExtractFullText(stored[^1].Message), MessageJson.ExtractFullText(root.History[^1]));
    }

    [Fact]
    public async Task NoThreshold_means_no_compaction()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "no-compaction", CancellationToken.None);
        var provider = new FakeProvider((request, index) => Turn.Say(new string('b', 4_000)));
        var host = AgentTestHost.Create(provider, store: store);
        var root = host.CreateRoot(session.Id, directory.Path);

        for (var turn = 1; turn <= 3; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"));
            Assert.Empty(events.OfType<AgentCompacted>());
        }

        Assert.Empty(await store.GetSummariesAsync(session.Id, CancellationToken.None));
        Assert.Equal(6, root.History.Count);
    }

    [Fact]
    public async Task Compaction_is_skipped_without_a_session_store()
    {
        var provider = new FakeProvider((request, index) => Turn.Say(new string('c', 4_000)));
        var options = CompactionWindows.Legacy(10);
        var host = AgentTestHost.Create(provider, options: options);
        var root = host.CreateRoot("session", Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "turn"));

        Assert.Empty(events.OfType<AgentCompacted>());
        Assert.Equal(2, root.History.Count);
    }

    [Fact]
    public async Task Every_compaction_appends_a_wider_session_summary_and_never_rewrites_earlier_ones()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "rounds", CancellationToken.None);
        var answer = new string('d', 800);
        var provider = new FakeProvider((request, index) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say(SummaryText) : Turn.Say(answer, TokenUsage.Empty));
        var host = AgentTestHost.Create(provider, options: CompactionWindows.Legacy(100, targetRatio: 0.05), store: store);
        var root = host.CreateRoot(session.Id, directory.Path);

        for (var turn = 1; turn <= 6; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"));
            Assert.Empty(events.OfType<AgentFailed>());
        }

        var summaries = await store.GetSummariesAsync(session.Id, CancellationToken.None);
        Assert.True(summaries.Count >= 2);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(SummaryLayers.SessionLayer, summary.Layer);
            Assert.Equal(1, summary.FromSeq);
        });
        var byTime = summaries.OrderBy(summary => summary.At).Select(summary => summary.ToSeq).ToList();
        Assert.Equal(byTime.Order().Distinct(), byTime); // each later record reaches further than the one before
        Assert.Equal(12, (await AgentTestHost.CollectAsync(store.ReadAsync(session.Id, 1, null, CancellationToken.None))).Count);
        Assert.Single(root.History, message => Turn.Text(message).Contains(SummaryText, StringComparison.Ordinal));
    }

    private const string CustomPrompt = "CUSTOM-SUMMARIZER-PROMPT";

    /// <summary>Runs four turns past the threshold; the provider answers any summarizer request (known prompts) with <see cref="SummaryText"/>.</summary>
    private static async Task<(IReadOnlyList<ChatRequest> Requests, IReadOnlyList<SummaryRecord> Summaries)> RunCompactingAsync(
        Func<ISessionStore, Func<ModelSettings, IChatProvider>, AgentHost> build,
        params string[] summarizerPrompts)
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "prompt", CancellationToken.None);
        var answer = new string('a', 800);
        var provider = new FakeProvider((request, _) =>
            summarizerPrompts.Contains(request.SystemPrompt) ? Turn.Say(SummaryText) : Turn.Say(answer, TokenUsage.Empty));
        var host = build(store, _ => provider);
        var root = host.CreateRoot(session.Id, directory.Path);

        for (var turn = 1; turn <= 4; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"));
            Assert.Empty(events.OfType<AgentFailed>());
        }

        return (provider.Requests, await store.GetSummariesAsync(session.Id, CancellationToken.None));
    }

    private static AgentHost Host(ISessionStore store, Func<ModelSettings, IChatProvider> providers, Forge.Core.Prompts.Templates.PromptTemplateSet? templates, IContextCompactor? compactor = null) =>
        new(new AgentHostOptions
        {
            Providers = providers,
            Tools = [],
            Layers = new PromptLayers("test system", null, null),
            Options = CompactionWindows.Legacy(200, maxTurns: 4),
            Store = store,
            Templates = templates,
            Compactor = compactor,
        });

    [Fact]
    public async Task A_custom_summarizer_prompt_is_what_the_summarizer_request_carries()
    {
        var (requests, summaries) = await RunCompactingAsync(
            (store, providers) => AgentTestHost.Create(
                providers(new ModelSettings("m")),
                options: CompactionWindows.Legacy(200, maxTurns: 4),
                store: store,
                compactor: new SummaryCompactor(store, providers, new ApproximateTokenCounter(), summarizerPrompt: CustomPrompt)),
            CustomPrompt,
            SummaryCompactor.SummarizerPrompt);

        var summarizerCalls = requests.Where(r => r.SystemPrompt == CustomPrompt).ToList();
        Assert.NotEmpty(summarizerCalls);
        Assert.DoesNotContain(requests, r => r.SystemPrompt == SummaryCompactor.SummarizerPrompt);
        Assert.All(summarizerCalls, call => Assert.Empty(call.Tools));
        Assert.All(summarizerCalls, call => Assert.Equal(ThinkingEffort.Off, call.Model.Thinking));
        Assert.NotEmpty(summaries);
        Assert.All(summaries, summary => Assert.Equal(SummaryText, summary.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public async Task A_null_or_blank_summarizer_prompt_falls_back_to_the_built_in_one(string? prompt)
    {
        var (requests, summaries) = await RunCompactingAsync(
            (store, providers) => AgentTestHost.Create(
                providers(new ModelSettings("m")),
                options: CompactionWindows.Legacy(200, maxTurns: 4),
                store: store,
                compactor: new SummaryCompactor(store, providers, new ApproximateTokenCounter(), summarizerPrompt: prompt)),
            SummaryCompactor.SummarizerPrompt);

        Assert.Contains(requests, r => r.SystemPrompt == SummaryCompactor.SummarizerPrompt);
        Assert.NotEmpty(summaries);
    }

    [Fact]
    public async Task An_agent_host_built_with_an_overridden_compaction_slot_summarizes_with_it()
    {
        using var fixture = new Forge.Core.Tests.Prompts.Templates.TemplateFixture();
        fixture.Global(PromptTemplateKind.Slot, Forge.Core.Prompts.Templates.PromptSlotIds.CompactionSummarizer, "---\nmode: replace\n---\n" + CustomPrompt);
        var templates = fixture.Resolve();

        var (requests, summaries) = await RunCompactingAsync(
            (store, providers) => Host(store, providers, templates),
            CustomPrompt,
            SummaryCompactor.SummarizerPrompt);

        Assert.Contains(requests, r => r.SystemPrompt == CustomPrompt);
        Assert.DoesNotContain(requests, r => r.SystemPrompt == SummaryCompactor.SummarizerPrompt);
        Assert.NotEmpty(summaries);
    }

    [Fact]
    public async Task An_agent_host_with_an_appended_compaction_slot_uses_the_composed_text()
    {
        using var fixture = new Forge.Core.Tests.Prompts.Templates.TemplateFixture();
        fixture.Global(PromptTemplateKind.Slot, Forge.Core.Prompts.Templates.PromptSlotIds.CompactionSummarizer, "---\nmode: append\n---\nAPPENDED-RULE");
        var templates = fixture.Resolve();
        var composed = templates.Slot(Forge.Core.Prompts.Templates.PromptSlotIds.CompactionSummarizer);

        var (requests, _) = await RunCompactingAsync(
            (store, providers) => Host(store, providers, templates),
            composed,
            SummaryCompactor.SummarizerPrompt);

        Assert.Contains(requests, r => r.SystemPrompt == composed);
        Assert.EndsWith("\n\nAPPENDED-RULE", composed, StringComparison.Ordinal);
        Assert.DoesNotContain(requests, r => r.SystemPrompt == SummaryCompactor.SummarizerPrompt);
    }

    [Fact]
    public async Task An_agent_host_without_templates_or_with_the_built_in_set_uses_the_compiled_in_prompt()
    {
        var builtIn = Forge.Core.Prompts.Templates.PromptTemplateSet.BuiltIn();
        var normalized = builtIn.Slot(Forge.Core.Prompts.Templates.PromptSlotIds.CompactionSummarizer);

        var (withoutTemplates, _) = await RunCompactingAsync((store, providers) => Host(store, providers, null), SummaryCompactor.SummarizerPrompt);
        var (withBuiltIn, summaries) = await RunCompactingAsync((store, providers) => Host(store, providers, builtIn), normalized, SummaryCompactor.SummarizerPrompt);

        Assert.Contains(withoutTemplates, r => r.SystemPrompt == SummaryCompactor.SummarizerPrompt);
        Assert.Contains(withBuiltIn, r => r.SystemPrompt == normalized);
        Assert.NotEmpty(summaries);
    }

    [Fact]
    public async Task An_explicit_compactor_wins_over_the_templates_slot()
    {
        using var fixture = new Forge.Core.Tests.Prompts.Templates.TemplateFixture();
        fixture.Global(PromptTemplateKind.Slot, Forge.Core.Prompts.Templates.PromptSlotIds.CompactionSummarizer, "FROM-TEMPLATE");
        var templates = fixture.Resolve();

        var (requests, summaries) = await RunCompactingAsync(
            (store, providers) => Host(
                store,
                providers,
                templates,
                new SummaryCompactor(store, providers, new ApproximateTokenCounter(), summarizerPrompt: "EXPLICIT-COMPACTOR")),
            "EXPLICIT-COMPACTOR",
            "FROM-TEMPLATE",
            SummaryCompactor.SummarizerPrompt);

        Assert.Contains(requests, r => r.SystemPrompt == "EXPLICIT-COMPACTOR");
        Assert.DoesNotContain(requests, r => r.SystemPrompt == "FROM-TEMPLATE");
        Assert.NotEmpty(summaries);
    }
}
