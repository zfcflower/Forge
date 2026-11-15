using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the thinking row (wave W17a): it is collapsed by default, streams like reply text but carries no Markdown,
/// shows only the first line until opened, seals when the answer starts, interleaves with reply text in arrival order,
/// and is never replayed from the store (reasoning lives only in the event stream).
/// </summary>
public sealed class TurnThinkingTests
{
    [Fact]
    public void A_new_thinking_row_is_collapsed_and_streaming_with_no_text()
    {
        var row = new TurnThinkingViewModel();

        Assert.False(row.IsExpanded);
        Assert.Equal("▸", row.ExpandGlyph);
        Assert.True(row.IsStreaming);
        Assert.False(row.HasText);
        Assert.Equal(string.Empty, row.Text);
        Assert.Equal(string.Empty, row.Preview);
    }

    [Fact]
    public void Appending_streams_text_and_keeps_the_row_running()
    {
        var row = new TurnThinkingViewModel();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Append("hello");
        row.Append(" world");

        Assert.Equal("hello world", row.Text);
        Assert.True(row.HasText);
        Assert.True(row.IsStreaming);
        Assert.Equal("hello world", row.Preview);
        Assert.Contains(nameof(TurnThinkingViewModel.Text), changed);
    }

    [Fact]
    public void Appending_nothing_changes_nothing()
    {
        var row = new TurnThinkingViewModel();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Append(string.Empty);

        Assert.False(row.HasText);
        Assert.Equal(string.Empty, row.Text);
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData("hello\nworld", "hello")]
    [InlineData("  \n  hello\nworld", "hello")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void The_preview_is_the_first_non_blank_line(string text, string expected)
    {
        var row = new TurnThinkingViewModel();

        row.Append(text);

        Assert.Equal(expected, row.Preview);
    }

    [Fact]
    public void The_preview_caps_a_long_first_line_with_an_ellipsis()
    {
        var row = new TurnThinkingViewModel();

        row.Append(new string('a', 200));

        Assert.Equal(160 + 1, row.Preview.Length);
        Assert.Equal(new string('a', 160) + "…", row.Preview);
    }

    [Fact]
    public void Completing_is_idempotent()
    {
        var row = new TurnThinkingViewModel();
        row.Append("reason");

        row.Complete();
        row.Complete();

        Assert.False(row.IsStreaming);
        Assert.Equal("reason", row.Text);
    }

    [Fact]
    public void Toggling_flips_expanded_and_the_glyph()
    {
        var row = new TurnThinkingViewModel();

        row.ToggleCommand.Execute(null);

        Assert.True(row.IsExpanded);
        Assert.Equal("▾", row.ExpandGlyph);

        row.ToggleCommand.Execute(null);

        Assert.False(row.IsExpanded);
        Assert.Equal("▸", row.ExpandGlyph);
    }

    [Fact]
    public void Thinking_and_text_interleave_in_arrival_order_and_seal_each_other()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);

        card.AppendThinking("t1");
        card.AppendText("a1");
        card.AppendThinking("t2");
        card.AppendText("a2");

        Assert.Equal(
            [
                typeof(TurnThinkingViewModel),
                typeof(TurnTextViewModel),
                typeof(TurnThinkingViewModel),
                typeof(TurnTextViewModel),
            ],
            card.Blocks.Select(static block => block.GetType()));
        Assert.Equal("t1", Assert.IsType<TurnThinkingViewModel>(card.Blocks[0]).Text);
        Assert.Equal("a1", Assert.IsType<TurnTextViewModel>(card.Blocks[1]).CopyText);
        Assert.Equal("t2", Assert.IsType<TurnThinkingViewModel>(card.Blocks[2]).Text);
        Assert.Equal("a2", Assert.IsType<TurnTextViewModel>(card.Blocks[3]).CopyText);

        // Each handoff seals the open stretch: nothing is left streaming mid-card.
        Assert.False(Assert.IsType<TurnThinkingViewModel>(card.Blocks[0]).IsStreaming);
        Assert.False(Assert.IsType<TurnTextViewModel>(card.Blocks[1]).IsStreaming);
        Assert.False(Assert.IsType<TurnThinkingViewModel>(card.Blocks[2]).IsStreaming);
        Assert.True(Assert.IsType<TurnTextViewModel>(card.Blocks[3]).IsStreaming);
    }

    [Fact]
    public void Queued_thinking_and_text_flush_into_one_card_with_thinking_first()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);

        transcript.QueueThinkingDelta("reason");
        transcript.QueueDelta("answer");
        transcript.FlushPending();

        var card = Assert.IsType<TurnCardViewModel>(Assert.Single(transcript.Items));
        Assert.Equal(
            [typeof(TurnThinkingViewModel), typeof(TurnTextViewModel)],
            card.Blocks.Select(static block => block.GetType()));
        Assert.Equal("reason", Assert.IsType<TurnThinkingViewModel>(card.Blocks[0]).Text);
        Assert.Equal("answer", Assert.IsType<TurnTextViewModel>(card.Blocks[1]).CopyText);
    }

    [Fact]
    public void Flushing_without_deltas_creates_no_card()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);

        transcript.FlushPending();

        Assert.True(transcript.IsEmpty);
        Assert.Empty(transcript.Items);
    }

    [Fact]
    public void Replaying_stored_messages_leaves_no_thinking_rows()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        var at = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var messages = new List<StoredMessage>
        {
            new(1, new ChatMessage(MessageRole.User, [new TextBlock("hi")]), null, at),
            new(2, new ChatMessage(MessageRole.Assistant, [new TextBlock("answer")]), null, at),
            new(
                3,
                new ChatMessage(
                    MessageRole.Tool,
                    [new ToolResultBlock("c1", [new TextBlock("ok")], IsError: false)]),
                null,
                at),
        };

        TranscriptReplay.Apply(transcript, messages);

        var thinkings = transcript.Items
            .OfType<TurnCardViewModel>()
            .SelectMany(static card => card.Blocks.OfType<TurnThinkingViewModel>())
            .ToList();
        Assert.Empty(thinkings);
        // The replayed answer itself still arrives as reply text.
        Assert.Contains(
            transcript.Items.OfType<TurnCardViewModel>().SelectMany(static card => card.Blocks.OfType<TurnTextViewModel>()),
            static text => text.CopyText.Contains("answer", StringComparison.Ordinal));
    }
}
