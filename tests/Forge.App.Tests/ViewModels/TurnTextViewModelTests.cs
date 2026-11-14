using System.ComponentModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="TurnTextViewModel"/>'s streaming contract: one <c>ContentChanged</c> per non-empty append and per
/// completion, a byte-identical copy text whatever the chunking was, and the placeholder that appears only for an empty
/// reply and follows the UI language. Also pins that a tool row or a notice seals the text block that came before it.
/// </summary>
public sealed class TurnTextViewModelTests : IDisposable
{
    private readonly UiLanguage previousLanguage = Loc.Instance.Language;

    public void Dispose() => Loc.Instance.Language = previousLanguage;

    [Fact]
    public void ContentChanged_fires_once_per_non_empty_append_and_once_on_completion()
    {
        var model = new TurnTextViewModel();
        var raised = 0;
        model.ContentChanged += (_, _) => raised++;

        model.Append("a");
        Assert.Equal(1, raised);

        model.Append("");
        Assert.Equal(1, raised);

        model.Append("b");
        Assert.Equal(2, raised);
        Assert.True(model.IsStreaming);

        model.Complete();
        Assert.Equal(3, raised);
        Assert.False(model.IsStreaming);

        // Completion is idempotent and never streams again.
        model.Complete();
        Assert.Equal(3, raised);
        Assert.False(model.IsStreaming);
    }

    [Fact]
    public void CopyText_is_byte_identical_across_deterministic_random_chunking()
    {
        var original = "line one\r\n\tindented\r\n\nfence```\r\nlast without newline";
        var model = new TurnTextViewModel();
        var rng = new Random(7);
        var offset = 0;
        while (offset < original.Length)
        {
            var size = rng.Next(1, 9);
            var end = Math.Min(offset + size, original.Length);
            model.Append(original.Substring(offset, end - offset));
            offset = end;
        }

        model.Complete();

        Assert.Equal(original, model.CopyText);
    }

    [Fact]
    public void An_empty_reply_shows_the_placeholder_and_a_non_empty_one_does_not()
    {
        var empty = new TurnTextViewModel();
        empty.Complete();

        Assert.True(empty.HasPlaceholder);
        Assert.NotEmpty(empty.PlaceholderText);
        Assert.Equal(string.Empty, empty.CopyText);

        var filled = new TurnTextViewModel();
        filled.Append("text");
        filled.Complete();

        Assert.False(filled.HasPlaceholder);
        Assert.Equal(string.Empty, filled.PlaceholderText);
        Assert.Equal("text", filled.CopyText);
    }

    [Fact]
    public void The_placeholder_follows_the_language_and_refresh_text_announces_it()
    {
        var model = new TurnTextViewModel();
        model.Complete();

        Loc.Instance.Language = UiLanguage.Chinese;
        var chinese = model.PlaceholderText;
        Assert.NotEmpty(chinese);

        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Loc.Instance.Language = UiLanguage.English;

        Assert.NotEqual(chinese, model.PlaceholderText);
        Assert.DoesNotContain("PlaceholderText", changed);

        model.RefreshText();

        Assert.Contains("PlaceholderText", changed);
    }

    [Fact]
    public void A_tool_row_seals_the_open_text_block_and_a_new_text_block_starts_after_it()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);
        card.AppendText("first text");
        card.AddTool(new ToolCall("t1", "Read", "{}"));
        card.AppendText("second text");

        Assert.Equal(3, card.Blocks.Count);

        var first = Assert.IsType<TurnTextViewModel>(card.Blocks[0]);
        Assert.IsType<ToolRowViewModel>(card.Blocks[1]);
        var second = Assert.IsType<TurnTextViewModel>(card.Blocks[2]);

        // The text block before the tool row is sealed: its copy button is visible and its own text is all it copies.
        Assert.False(first.IsStreaming);
        Assert.Equal("first text", first.CopyText);
        Assert.True(second.IsStreaming);
        Assert.Equal("second text", second.CopyText);
    }

    [Fact]
    public void A_notice_seals_the_open_text_block_too()
    {
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);
        card.AppendText("text before notice");
        card.AddNotice(LocText.Literal("note"));

        var text = Assert.IsType<TurnTextViewModel>(card.Blocks[0]);

        Assert.False(text.IsStreaming);
        Assert.Equal("text before notice", text.CopyText);
        Assert.Equal(2, card.Blocks.Count);
    }
}
