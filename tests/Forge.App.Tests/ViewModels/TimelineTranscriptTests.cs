using System.ComponentModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the live half of the timeline rows: a <see cref="ToolRowViewModel"/> is running from construction until its
/// result arrives (and announces that flip), and a notice row carries its node kind - the default <c>Notice</c>, or
/// the <c>Approval</c> a denied approval is stored with (forge-app spec, "聊天时间轴(W13)").
/// </summary>
public sealed class ToolRowViewModelTests
{
    [Fact]
    public void A_new_row_is_running()
    {
        Assert.True(new ToolRowViewModel("bash", "{}").IsRunning);
    }

    [Fact]
    public void Completing_with_a_tool_result_stops_the_row_and_announces_it()
    {
        var row = new ToolRowViewModel("bash", "{}");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Complete(new ToolResult("ok"));

        Assert.False(row.IsRunning);
        Assert.False(row.IsError);
        Assert.Equal("ok", row.Result);
        Assert.Contains(nameof(ToolRowViewModel.IsRunning), changed);
    }

    [Fact]
    public void Completing_with_an_error_stops_the_row_and_announces_it()
    {
        var row = new ToolRowViewModel("bash", "{}");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Complete(LocText.Literal("boom"), isError: true);

        // A row with an error is not running: the timeline must not keep it in the running state.
        Assert.False(row.IsRunning);
        Assert.True(row.IsError);
        Assert.Contains(nameof(ToolRowViewModel.IsRunning), changed);
    }
}

/// <summary>The notice rows of the timeline: their kind defaults to <see cref="TimelineNodeKind.Notice"/> and is stored as given.</summary>
public sealed class TimelineTranscriptTests
{
    [Fact]
    public void Notices_default_to_the_notice_kind()
    {
        Assert.Equal(TimelineNodeKind.Notice, new NoticeViewModel(LocText.Literal("note")).Kind);
        Assert.Equal(TimelineNodeKind.Notice, new TurnNoticeViewModel(LocText.Literal("note"), isError: false).Kind);
    }

    [Fact]
    public void The_transcript_stores_an_approval_notice_with_its_kind()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);

        transcript.AddNotice(LocText.Literal("denied"), kind: TimelineNodeKind.Approval);

        var notice = Assert.Single(transcript.Items);
        var viewModel = Assert.IsType<NoticeViewModel>(notice);
        Assert.Equal(TimelineNodeKind.Approval, viewModel.Kind);
        Assert.Equal("denied", viewModel.Text);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void The_transcript_defaults_a_notice_to_the_notice_kind()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);

        transcript.AddNotice(LocText.Literal("compacted"));

        var notice = Assert.IsType<NoticeViewModel>(Assert.Single(transcript.Items));
        Assert.Equal(TimelineNodeKind.Notice, notice.Kind);
    }

    [Fact]
    public void A_turn_card_stores_an_approval_notice_in_its_blocks()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);

        card.AddNotice(LocText.Literal("denied"), kind: TimelineNodeKind.Approval);

        var block = Assert.Single(card.Blocks);
        var notice = Assert.IsType<TurnNoticeViewModel>(block);
        Assert.Equal(TimelineNodeKind.Approval, notice.Kind);
        Assert.Equal("denied", notice.Text);
    }
}
