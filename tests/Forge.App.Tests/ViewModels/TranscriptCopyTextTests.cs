using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the copy-button text of transcript items: a reply copies back exactly what was appended, whatever the chunking was
/// (the streaming Markdown document splits it into blocks internally, the copy text never sees that split), the "no text"
/// placeholder copies as nothing, and a user message copies its text.
/// </summary>
public sealed class TranscriptCopyTextTests
{
    private static string Lines(int count, string line = "0123456789012345678") =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"{line}{i}\n"));

    [Fact]
    public void A_reply_copies_as_empty_before_anything_is_appended()
    {
        ICopyableText text = new TurnTextViewModel();

        Assert.Equal(string.Empty, text.CopyText);
    }

    [Fact]
    public void A_short_reply_copies_exactly()
    {
        var text = new TurnTextViewModel();
        text.Append("hello ");
        text.Append("world\n");

        Assert.Equal("hello world\n", text.CopyText);
    }

    [Fact]
    public void A_long_reply_copies_exactly()
    {
        var original = Lines(400);
        var text = new TurnTextViewModel();

        text.Append(original);

        Assert.Equal(original, text.CopyText);
    }

    [Fact]
    public void A_reply_streamed_in_small_chunks_copies_exactly_across_block_boundaries()
    {
        var original = Lines(500);
        var text = new TurnTextViewModel();

        for (var i = 0; i < original.Length; i += 37)
        {
            text.Append(original.Substring(i, Math.Min(37, original.Length - i)));
        }

        text.Complete();

        Assert.Equal(original, text.CopyText);
    }

    [Fact]
    public void A_long_line_without_newlines_copies_exactly()
    {
        var original = string.Join(' ', Enumerable.Repeat("word", 4000));
        var text = new TurnTextViewModel();

        text.Append(original);

        Assert.Equal(original, text.CopyText);
    }

    [Fact]
    public void A_long_unbroken_run_without_newlines_or_spaces_copies_exactly()
    {
        var original = new string('x', 20_000);
        var text = new TurnTextViewModel();

        text.Append(original);

        Assert.Equal(original, text.CopyText);
    }

    [Fact]
    public void Completing_a_reply_with_no_text_shows_the_placeholder_and_copies_as_empty()
    {
        var text = new TurnTextViewModel();

        text.Complete();

        Assert.True(text.HasPlaceholder);
        Assert.NotEmpty(text.PlaceholderText);
        Assert.Equal(string.Empty, text.CopyText);
    }

    [Fact]
    public void Completing_a_reply_that_has_text_keeps_the_text_and_no_placeholder()
    {
        var text = new TurnTextViewModel();
        text.Append("done");
        text.Complete();

        Assert.False(text.HasPlaceholder);
        Assert.Equal("done", text.CopyText);
        Assert.False(text.IsStreaming);
    }

    [Fact]
    public void A_reply_stops_streaming_once_completed_and_completion_is_idempotent()
    {
        var text = new TurnTextViewModel();
        text.Append("body");
        Assert.True(text.IsStreaming);

        text.Complete();
        text.Complete();

        Assert.False(text.IsStreaming);
        Assert.Equal("body", text.CopyText);
    }

    [Fact]
    public void A_user_message_copies_its_text_through_the_interface()
    {
        ICopyableText message = new UserMessageViewModel("line one\nline two", DateTimeOffset.UtcNow);

        Assert.Equal("line one\nline two", message.CopyText);
    }

    [Fact]
    public void A_user_message_with_empty_text_copies_as_empty()
    {
        ICopyableText message = new UserMessageViewModel(string.Empty, DateTimeOffset.UtcNow);

        Assert.Equal(string.Empty, message.CopyText);
    }

    [Fact]
    public void Completing_a_tool_row_sets_result_status_and_error_state()
    {
        var ok = new ToolRowViewModel("read", "{}");
        Assert.False(ok.HasResult);
        Assert.Null(ok.Result);

        ok.Complete(new ToolResult("file body"));

        Assert.True(ok.HasResult);
        Assert.Equal("file body", ok.Result);
        Assert.Equal("✓", ok.Status);
        Assert.False(ok.IsError);

        var bad = new ToolRowViewModel("read", "{}");
        bad.Complete(new ToolResult("boom", IsError: true));

        Assert.Equal("boom", bad.Result);
        Assert.Equal("✗", bad.Status);
        Assert.True(bad.IsError);

        var empty = new ToolRowViewModel("read", "{}");
        empty.Complete(new ToolResult(string.Empty));

        Assert.False(empty.HasResult);
        Assert.Equal("✓", empty.Status);
    }
}
