using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class SummaryLayersTests
{
    private static readonly DateTimeOffset Clock = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Func<string, int> Chars = static text => text.Length;

    [Fact]
    public void Build_orders_session_summary_then_stage_summaries_then_raw_tail()
    {
        var summaries = new SummaryRecord[]
        {
            new("s", 1, 21, 30, "stage-c", Clock),
            new("s", 2, 1, 10, "session", Clock),
            new("s", 1, 11, 20, "stage-b", Clock),
        };

        var view = SummaryLayers.Build(summaries, 45);

        Assert.Equal("session", view.SessionSummary?.Text);
        Assert.Equal(new[] { "stage-b", "stage-c" }, view.StageSummaries.Select(s => s.Text));
        Assert.Equal(30, view.LastSummarizedSeq);
        Assert.Equal(31, view.RawFromSeq);
    }

    [Fact]
    public void Build_prefers_the_widest_session_summary_and_drops_stages_it_covers()
    {
        var summaries = new SummaryRecord[]
        {
            new("s", 2, 1, 10, "old-session", Clock),
            new("s", 2, 1, 20, "new-session", Clock.AddMinutes(1)),
            new("s", 1, 5, 12, "covered-stage", Clock),
            new("s", 1, 21, 30, "live-stage", Clock),
        };

        var view = SummaryLayers.Build(summaries, 40);

        Assert.Equal("new-session", view.SessionSummary?.Text);
        var stage = Assert.Single(view.StageSummaries);
        Assert.Equal("live-stage", stage.Text);
        Assert.Equal(30, view.LastSummarizedSeq);
    }

    [Fact]
    public void Build_without_summaries_starts_raw_at_one()
    {
        var view = SummaryLayers.Build([], 12);

        Assert.Null(view.SessionSummary);
        Assert.Empty(view.StageSummaries);
        Assert.Equal(0, view.LastSummarizedSeq);
        Assert.Equal(1, view.RawFromSeq);
    }

    [Fact]
    public void Plan_is_idle_when_the_context_fits_the_budget()
    {
        var messages = Messages(1, 5, 10);

        var plan = SummaryLayers.Plan([], messages, 5, 1000, Chars);

        Assert.True(plan.IsIdle);
        Assert.Null(plan.Promote);
        Assert.Empty(plan.Fold);
        Assert.Null(plan.FoldRange);
    }

    [Fact]
    public void Plan_promotes_the_oldest_raw_range_that_exceeds_the_budget()
    {
        var messages = Messages(1, 5, 10);

        var plan = SummaryLayers.Plan([], messages, 5, 25, Chars);

        Assert.NotNull(plan.Promote);
        Assert.Equal(1, plan.Promote.FromSeq);
        Assert.Equal(3, plan.Promote.ToSeq);
        Assert.Empty(plan.Fold);
    }

    [Fact]
    public void Plan_never_promotes_the_newest_message()
    {
        var messages = Messages(1, 6, 10);

        var plan = SummaryLayers.Plan([], messages, 6, 0, Chars);

        Assert.NotNull(plan.Promote);
        Assert.Equal(1, plan.Promote.FromSeq);
        Assert.Equal(5, plan.Promote.ToSeq);
        Assert.Null(SummaryLayers.Plan([], Messages(1, 1, 10), 1, 0, Chars).Promote);
    }

    [Fact]
    public void Plan_counts_summaries_against_the_budget()
    {
        var summaries = new SummaryRecord[] { new("s", 2, 1, 2, new string('x', 30), Clock) };
        var messages = Messages(3, 5, 10);

        var withSummary = SummaryLayers.Plan(summaries, messages, 5, 35, Chars);
        var withoutSummary = SummaryLayers.Plan([], messages, 5, 35, Chars);

        Assert.NotNull(withSummary.Promote);
        Assert.Equal(3, withSummary.Promote.FromSeq);
        Assert.True(withoutSummary.IsIdle);
    }

    [Fact]
    public void Plan_skips_messages_that_are_already_summarized()
    {
        var summaries = new SummaryRecord[] { new("s", 1, 1, 3, "stage", Clock) };
        var messages = Messages(1, 5, 10);

        Assert.True(SummaryLayers.Plan(summaries, messages, 5, 1000, Chars).IsIdle);

        var plan = SummaryLayers.Plan(summaries, messages, 5, 15, Chars);
        Assert.NotNull(plan.Promote);
        Assert.Equal(4, plan.Promote.FromSeq);
        Assert.Equal(4, plan.Promote.ToSeq);
    }

    [Fact]
    public void Plan_folds_the_oldest_stage_summaries_into_layer_two()
    {
        var summaries = new List<SummaryRecord>();
        for (var index = 0; index < 5; index++)
        {
            summaries.Add(new SummaryRecord("s", 1, 1 + (index * 10), 10 + (index * 10), $"stage-{index}", Clock.AddMinutes(index)));
        }

        var plan = SummaryLayers.Plan(summaries, [], 50, 1000, Chars, maxStageSummaries: 2);

        Assert.Null(plan.Promote);
        Assert.Equal(new[] { "stage-0", "stage-1", "stage-2" }, plan.Fold.Select(s => s.Text));
        Assert.Equal(1, plan.FoldRange?.FromSeq);
        Assert.Equal(30, plan.FoldRange?.ToSeq);
        Assert.Equal(5, summaries.Count);
    }

    [Fact]
    public void Plan_extends_the_existing_session_range_when_folding_again()
    {
        var summaries = new List<SummaryRecord> { new("s", 2, 1, 30, "session", Clock) };
        for (var index = 0; index < 4; index++)
        {
            summaries.Add(new SummaryRecord("s", 1, 31 + (index * 10), 40 + (index * 10), $"stage-{index}", Clock.AddMinutes(index)));
        }

        var plan = SummaryLayers.Plan(summaries, [], 70, 1000, Chars, maxStageSummaries: 1);

        Assert.Equal(3, plan.Fold.Count);
        Assert.Equal(1, plan.FoldRange?.FromSeq);
        Assert.Equal(60, plan.FoldRange?.ToSeq);
    }

    [Fact]
    public void Plan_can_promote_and_fold_in_one_step_without_rewriting_input()
    {
        var summaries = new List<SummaryRecord>();
        for (var index = 0; index < 4; index++)
        {
            summaries.Add(new SummaryRecord("s", 1, 1 + (index * 10), 10 + (index * 10), $"stage-{index}", Clock.AddMinutes(index)));
        }

        var messages = Messages(41, 45, 10);
        var snapshot = summaries.ToArray();

        var plan = SummaryLayers.Plan(summaries, messages, 45, 20, Chars, maxStageSummaries: 2);

        Assert.NotNull(plan.Promote);
        Assert.Equal(41, plan.Promote.FromSeq);
        Assert.Equal(new[] { "stage-0", "stage-1" }, plan.Fold.Select(s => s.Text));
        Assert.Equal(snapshot, summaries);
    }

    [Fact]
    public async Task Compaction_loop_keeps_only_the_newest_stage_summaries_and_raw_tail()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        for (var index = 1; index <= 30; index++)
        {
            await store.AppendAsync(session.Id, Text(new string('a', 10)), null, default);
        }

        var budget = 35;
        for (var round = 0; round < 6; round++)
        {
            var summaries = await store.GetSummariesAsync(session.Id, default);
            var messages = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
            var plan = SummaryLayers.Plan(summaries, messages, messages[^1].Seq, budget, Chars, maxStageSummaries: 2);
            if (plan.IsIdle)
            {
                break;
            }

            if (plan.Promote is { } promote)
            {
                await store.PutSummaryAsync(
                    new SummaryRecord(session.Id, 1, promote.FromSeq, promote.ToSeq, $"stage {promote.FromSeq}-{promote.ToSeq}", DateTimeOffset.UtcNow),
                    default);
            }

            if (plan.FoldRange is { } foldRange)
            {
                await store.PutSummaryAsync(
                    new SummaryRecord(session.Id, 2, foldRange.FromSeq, foldRange.ToSeq, "session", DateTimeOffset.UtcNow.AddSeconds(1)),
                    default);
            }
        }

        var final = await store.GetSummariesAsync(session.Id, default);
        var view = SummaryLayers.Build(final, 30);
        Assert.NotNull(view.SessionSummary);
        Assert.InRange(view.StageSummaries.Count, 0, 2);
        Assert.Equal(view.LastSummarizedSeq + 1, view.RawFromSeq);
        Assert.True(view.LastSummarizedSeq < 30, "the newest message must stay raw");
        Assert.All(view.StageSummaries, stage => Assert.Equal(1, stage.Layer));
    }

    private static List<StoredMessage> Messages(long from, long to, int length)
    {
        var messages = new List<StoredMessage>((int)(to - from + 1));
        for (var seq = from; seq <= to; seq++)
        {
            messages.Add(new StoredMessage(seq, Text(new string('m', length)), null, Clock.AddMinutes(seq)));
        }

        return messages;
    }
}
