using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class SummaryLayersTests
{
    private static readonly DateTimeOffset Clock = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
}
