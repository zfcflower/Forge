using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class CompactionTests
{
    private const string SummaryText = "SUMMARY-OF-OLD-MESSAGES";

    [Fact]
    public async Task Threshold_compacts_into_a_layer_one_summary_and_keeps_the_originals()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "compaction", CancellationToken.None);
        var answer = new string('a', 800);
        var provider = new FakeProvider((request, index) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? Turn.Say(SummaryText)
                : Turn.Say(answer));
        var options = new AgentOptions { CompactionThresholdTokens = 200, MaxTurns = 4 };
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
            Assert.Equal(SummaryLayers.StageLayer, summary.Layer);
            Assert.Equal(SummaryText, summary.Text);
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
        var options = new AgentOptions { CompactionThresholdTokens = 10 };
        var host = AgentTestHost.Create(provider, options: options);
        var root = host.CreateRoot("session", Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "turn"));

        Assert.Empty(events.OfType<AgentCompacted>());
        Assert.Equal(2, root.History.Count);
    }

    [Fact]
    public async Task Stage_summaries_fold_into_a_session_summary_without_rewriting_them()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "fold", CancellationToken.None);
        var answer = new string('d', 800);
        var provider = new FakeProvider((request, index) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say(SummaryText) : Turn.Say(answer));
        var compactor = new SummaryCompactor(
            store,
            _ => provider,
            new ApproximateTokenCounter(),
            targetRatio: 0.05,
            maxStageSummaries: 1);
        var host = AgentTestHost.Create(
            provider,
            options: new AgentOptions { CompactionThresholdTokens = 100 },
            store: store,
            compactor: compactor);
        var root = host.CreateRoot(session.Id, directory.Path);

        for (var turn = 1; turn <= 6; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"));
            Assert.Empty(events.OfType<AgentFailed>());
        }

        var summaries = await store.GetSummariesAsync(session.Id, CancellationToken.None);
        Assert.Contains(summaries, summary => summary.Layer == SummaryLayers.SessionLayer);
        Assert.Contains(summaries, summary => summary.Layer == SummaryLayers.StageLayer);
        Assert.All(summaries, summary => Assert.Equal(SummaryText, summary.Text));
        Assert.Equal(12, (await AgentTestHost.CollectAsync(store.ReadAsync(session.Id, 1, null, CancellationToken.None))).Count);
    }
}
