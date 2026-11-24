using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

    /// <summary>Ctrl+A on a card selects everything it holds — a reply with several blocks, a tool's expanded output, a thinking block.</summary>
    [Fact]
    public Task Selecting_the_card_takes_every_block_it_holds() => SharedUi.RunAsync(() =>
    {
        var one = new MarkdownTextBlock { Text = "first paragraph", TextWrapping = TextWrapping.Wrap };
        var two = new MarkdownCodeTextBlock { Text = "return Managed(path);", TextWrapping = TextWrapping.Wrap };
        var card = new SelectableCard { Child = new StackPanel { Children = { one, two } } };
        var window = new Window { Content = new Panel { Background = Brushes.Transparent, Children = { card } }, Width = 420, Height = 160 };
        window.Show();
        SharedUi.Settle(window);

        card.SelectAll();

        Assert.True(card.HasSelection);
        Assert.Equal(0, one.SelectionStart);
        Assert.Equal("first paragraph".Length, one.SelectionEnd);
        Assert.Equal(0, two.SelectionStart);
        Assert.Equal("return Managed(path);".Length, two.SelectionEnd);

        // A copied code block keeps its fences, so a snippet pasted into an issue is still code.
        var copied = card.SelectedText();
        Assert.NotNull(copied);
        Assert.Contains("first paragraph", copied, StringComparison.Ordinal);
        Assert.Contains("```", copied, StringComparison.Ordinal);
        Assert.Contains("return Managed(path);", copied, StringComparison.Ordinal);

        return Task.CompletedTask;
    });

    /// <summary>
    /// A rendered paragraph keeps its text in <c>Inlines</c>, so <c>Text</c> is null on it. The card must still select it: this is
    /// the reply's normal shape, and treating the block as zero-length made every drag and Ctrl+A on a reply a no-op.
    /// </summary>
    [Fact]
    public Task A_paragraph_built_from_inlines_is_selectable() => SharedUi.RunAsync(() =>
    {
        var inline = new MarkdownTextBlock
        {
            Inlines = [new Run("inline only")],
            TextWrapping = TextWrapping.Wrap,
        };
        var plain = new SelectableBlock { Text = "plain text", TextWrapping = TextWrapping.Wrap };
        var card = new SelectableCard { Child = new StackPanel { Children = { inline, plain } } };
        var window = new Window { Content = new Panel { Background = Brushes.Transparent, Children = { card } }, Width = 420, Height = 160 };
        window.Show();
        SharedUi.Settle(window);

        Assert.Null(inline.Text);
        card.SelectAll();

        Assert.Equal(0, inline.SelectionStart);
        Assert.Equal("inline only".Length, inline.SelectionEnd);
        Assert.Equal(0, plain.SelectionStart);
        Assert.Equal("plain text".Length, plain.SelectionEnd);

        var copied = card.SelectedText();
        Assert.NotNull(copied);
        Assert.StartsWith("inline only", copied, StringComparison.Ordinal);
        Assert.Contains("plain text", copied, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    /// <summary>A press outside the card that holds a selection drops it; a press inside it does not.</summary>
    [Fact]
    public Task A_click_outside_the_card_clears_its_selection() => SharedUi.RunAsync(() =>
    {
        var text = new MarkdownTextBlock { Text = "select me", TextWrapping = TextWrapping.Wrap };
        var card = new SelectableCard { Child = new StackPanel { Children = { text } } };
        var elsewhere = new TextBlock { Text = "somewhere else" };
        var window = new Window
        {
            Content = new Panel { Background = Brushes.Transparent, Children = { card, elsewhere } },
            Width = 420,
            Height = 200,
        };
        window.Show();
        SharedUi.Settle(window);

        card.SelectAll();
        Assert.True(card.HasSelection);

        card.ClearWhenPressOutside(elsewhere);
        Assert.False(card.HasSelection);
        Assert.Equal(0, text.SelectionEnd);

        // The root-level check leaves a press inside the card to the card itself (a left press clears there too; the drag
        // needs the press to start a new range).
        card.SelectAll();
        card.ClearWhenPressOutside(text);
        Assert.True(card.HasSelection);
        return Task.CompletedTask;
    });

    /// <summary>
    /// A left press drops the selection wherever it lands, the selected text included: a plain click deselects, and a drag
    /// paints a new range from the press. Only the right press keeps it, because the context menu acts on it.
    /// </summary>
    [Fact]
    public Task A_left_click_inside_the_card_drops_the_selection() => SharedUi.RunAsync(() =>
    {
        var text = new MarkdownTextBlock { Text = "select me", TextWrapping = TextWrapping.Wrap };
        var card = new SelectableCard { Child = new StackPanel { Children = { text } } };
        var window = new Window
        {
            Content = new Panel { Background = Brushes.Transparent, Children = { card } },
            Width = 420,
            Height = 200,
        };
        window.Show();
        SharedUi.Settle(window);

        card.SelectAll();
        Assert.True(card.HasSelection);
        var point = text.TranslatePoint(new Point(6, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        SharedUi.Settle(window);

        Assert.False(card.HasSelection);
        Assert.Equal(text.SelectionStart, text.SelectionEnd);
        return Task.CompletedTask;
    });

    /// <summary>
    /// The card's context menu: right-clicking a block must not drop the selection (the menu is about it), Copy takes the
    /// card's whole selection — not just the block it was opened on, which is what the framework's own menu did — and
    /// Select all is the card's Ctrl+A.
    /// </summary>
    [Fact]
    public Task The_context_menu_copies_the_card_selection_and_selects_it_all() => SharedUi.RunAsync(async () =>
    {
        var one = new MarkdownTextBlock { Text = "first paragraph", TextWrapping = TextWrapping.Wrap };
        var two = new MarkdownCodeTextBlock { Text = "return Managed(path);", TextWrapping = TextWrapping.Wrap };
        var card = new SelectableCard { Child = new StackPanel { Children = { one, two } } };
        var window = new Window
        {
            Content = new Panel { Background = Brushes.Transparent, Children = { card } },
            Width = 420,
            Height = 200,
        };
        window.Show();
        SharedUi.Settle(window);

        card.SelectAll();
        var point = two.TranslatePoint(new Point(6, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
        SharedUi.Settle(window);

        Assert.True(card.HasSelection);
        var menu = Assert.IsType<MenuFlyout>(two.ContextFlyout);
        Assert.Same(menu, one.ContextFlyout);
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(new KeyGesture(Key.C, KeyModifiers.Control), items[0].InputGesture);
        Assert.Equal(new KeyGesture(Key.A, KeyModifiers.Control), items[1].InputGesture);

        // The card's selection is copied as a whole, code block fenced like Ctrl+C.
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[0]));
        var copied = await window.Clipboard!.TryGetTextAsync();
        Assert.NotNull(copied);
        Assert.Contains("first paragraph", copied, StringComparison.Ordinal);
        Assert.Contains("```", copied, StringComparison.Ordinal);
        Assert.Contains("return Managed(path);", copied, StringComparison.Ordinal);

        card.Clear();
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[1]));
        Assert.True(card.HasSelection);
        Assert.Equal("first paragraph".Length, one.SelectionEnd);
        Assert.Equal("return Managed(path);".Length, two.SelectionEnd);
    });

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
