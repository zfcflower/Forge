using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Thinking rows (wave W17a): reasoning is content of the agent's own turn, so it interleaves with the reply text in arrival
/// order, seals into its own row when the other kind arrives, and carries a 160-character collapsed preview. Reasoning is
/// never persisted, so a replayed transcript has no thinking row at all.
/// </summary>
public sealed class ThinkingRowTests
{
    [Fact]
    public void Thinking_opens_a_row_and_appends_within_it()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);

        card.AppendThinking("first ");
        card.AppendThinking("second");

        var row = Assert.IsType<TurnThinkingViewModel>(Assert.Single(card.Blocks));
        Assert.Equal("first second", row.Text);
        Assert.True(row.IsStreaming);
    }

    [Fact]
    public void Reply_text_seals_the_thinking_row_and_a_new_stretch_opens_another()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);

        card.AppendThinking("plan");
        card.AppendText("answer");
        card.AppendThinking("more");

        Assert.Equal(3, card.Blocks.Count);
        Assert.IsType<TurnThinkingViewModel>(card.Blocks[0]);
        Assert.IsType<TurnTextViewModel>(card.Blocks[1]);
        var second = Assert.IsType<TurnThinkingViewModel>(card.Blocks[2]);
        Assert.Equal("more", second.Text);
        Assert.False(((TurnThinkingViewModel)card.Blocks[0]).IsStreaming);
    }

    [Fact]
    public void Thinking_after_text_in_one_burst_still_lands_after_it()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);

        card.AppendText("answer");
        card.AppendThinking("why");

        Assert.IsType<TurnTextViewModel>(card.Blocks[0]);
        Assert.IsType<TurnThinkingViewModel>(card.Blocks[1]);
    }

    [Fact]
    public void The_collapsed_preview_stops_at_the_documented_limit()
    {
        var row = new TurnThinkingViewModel();
        row.Append(new string('x', TurnThinkingViewModel.PreviewCharacters + 40));

        Assert.EndsWith("…", row.Preview, StringComparison.Ordinal);
        Assert.Equal(TurnThinkingViewModel.PreviewCharacters + 1, row.Preview.Length);
    }

    [Fact]
    public void Finishing_the_turn_stops_the_streaming_state()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);
        card.AppendThinking("done");

        card.Finish(null, null);

        var row = (TurnThinkingViewModel)card.Blocks[0];
        Assert.False(row.IsStreaming);
    }
}