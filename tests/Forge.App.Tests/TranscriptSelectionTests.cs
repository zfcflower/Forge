using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.Changes.Markdown;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A message card selects as one run of text. The block arithmetic and the point-to-character mapping are what make that work
/// across a card's paragraphs and code blocks, so they are checked here directly: which blocks a range covers, what each one
/// contributes, and which character a point lands on.
/// <para>
/// The pointer plumbing itself (press, drag threshold, capture, edge scroll) cannot be driven in the headless harness — a plain
/// <see cref="SelectableTextBlock"/> does not select on synthetic mouse input either — so it is covered by hand in the app.
/// </para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class TranscriptSelectionTests
{
    [Fact]
    public void A_range_inside_one_block_covers_only_that_block()
    {
        var range = new CardSelection(AnchorBlock: 1, AnchorChar: 7, HeadBlock: 1, HeadChar: 3);

        Assert.Equal((1, 3, 1, 7), range.Ordered());
        Assert.Equal((3, 7), range.For(1, 20));
        Assert.Equal((-1, -1), range.For(0, 20));
        Assert.Equal((-1, -1), range.For(2, 20));
    }

    /// <summary>A drag from the middle of block 0 into the middle of block 2 selects all of block 1 and half of each end.</summary>
    [Fact]
    public void A_range_across_blocks_takes_whole_blocks_in_between()
    {
        var range = new CardSelection(AnchorBlock: 0, AnchorChar: 4, HeadBlock: 2, HeadChar: 6);

        Assert.Equal((4, 10), range.For(0, 10));
        Assert.Equal((0, 10), range.For(1, 10));
        Assert.Equal((0, 6), range.For(2, 10));
        Assert.Equal((-1, -1), range.For(3, 10));
    }

    /// <summary>Dragging upwards gives the same selection as dragging downwards over the same text.</summary>
    [Fact]
    public void A_reverse_drag_orders_like_a_forward_one()
    {
        var forward = new CardSelection(0, 3, 2, 5);
        var backward = new CardSelection(2, 5, 0, 3);

        Assert.Equal(forward.Ordered(), backward.Ordered());
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(forward.For(index, 10), backward.For(index, 10));
        }
    }

    /// <summary>Offsets past the text are clamped, so a drag that ends past the last character still selects the tail.</summary>
    [Fact]
    public void Offsets_past_the_end_are_clamped_to_the_text()
    {
        var range = new CardSelection(0, 99, 1, 99);

        Assert.Equal((5, 5), range.For(0, 5));
        Assert.Equal((0, 3), range.For(1, 3));
    }

    [Fact]
    public void A_block_whose_share_is_empty_contributes_nothing()
    {
        // A range that starts at a block's end and continues past it leaves that block with nothing to paint.
        var range = new CardSelection(0, 5, 1, 4);

        Assert.Equal((5, 5), range.For(0, 5));
        Assert.Equal((0, 4), range.For(1, 10));
    }

    /// <summary>The point-to-character map the card's drag relies on: a point in a block lands on the character under it.</summary>
    [Fact]
    public Task A_point_maps_to_the_character_under_it() => SharedUi.RunAsync(() =>
    {
        var block = new MarkdownTextBlock { Text = "hello world", Width = 420, TextWrapping = TextWrapping.Wrap };
        var window = new Window { Content = new Panel { Background = Brushes.Transparent, Children = { block } }, Width = 420, Height = 80 };
        window.Show();
        SharedUi.Settle(window);

        Assert.Equal(0, block.IndexAt(new Point(0, 3)));
        Assert.True(block.IndexAt(new Point(60, 3)) is > 0 and < 11);
        Assert.Equal(11, block.IndexAt(new Point(415, 3)));
        return Task.CompletedTask;
    });
}
