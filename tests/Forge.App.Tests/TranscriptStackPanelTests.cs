using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.VisualTree;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

// The panel's extent (what the scroll bar's thumb is computed from) must not depend on which rows happen to be realized.
// Rows here have fixed, font-independent heights so every expectation is exact arithmetic.
[Collection("forge-app-ui")]
public sealed class TranscriptStackPanelTests
{
    private const double Viewport = 300;

    private abstract class Row(double height)
    {
        public double Height { get; } = height;
    }

    private sealed class Small() : Row(20);

    private sealed class Medium(double height = 100) : Row(height);

    private sealed class Large() : Row(300);

    /// <summary>A row whose height depends on the width it is given, like wrapped text.</summary>
    private sealed class Wrapping() : Row(0);

    private sealed class WrappingControl : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, availableSize.Width < 300 ? 200 : 100);
    }

    private static Row Cycle(int i) => (i % 3) switch { 0 => new Small(), 1 => new Medium(), _ => new Large() };

    private static (Window Window, ListBox Box, ScrollViewer Scroll) Mount(ObservableCollection<object> rows, double width = 400)
    {
        var box = new ListBox
        {
            Padding = new Thickness(0),
            AutoScrollToSelectedItem = false,
            ItemsSource = rows,
            ItemsPanel = new ItemsPanelTemplate
            {
                Content = new Func<IServiceProvider?, TemplateResult<Control>?>(_ => new TemplateResult<Control>(new TranscriptStackPanel(), new NameScope())),
            },
            ItemTemplate = new FuncDataTemplate<object>((item, _) => item is Wrapping ? new WrappingControl() : new Border { Height = (item as Row)?.Height ?? 0 }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var window = new Window { Width = width, Height = Viewport, Content = box };
        window.Show();
        SharedUi.Settle(window);
        return (window, box, box.GetVisualDescendants().OfType<ScrollViewer>().First());
    }

    /// <summary>The per-row chrome the list box adds around the item (padding of its container), measured from a realized row.</summary>
    private static double Chrome(ListBox box, double contentHeight) =>
        box.ContainerFromIndex(0)!.Bounds.Height - contentHeight;

    private static double Expected(IEnumerable<object> rows, double chrome) => rows.Sum(r => ((Row)r).Height + chrome);

    private static void ScrollTo(Window window, ScrollViewer scroll, double y)
    {
        scroll.Offset = new Vector(0, y);
        SharedUi.Settle(window);
    }

    [Fact]
    public Task Extent_is_exact_wherever_the_viewport_is_for_mixed_row_types() => SharedUi.RunAsync(() =>
    {
        var rows = new ObservableCollection<object>(Enumerable.Range(0, 300).Select(i => (object)Cycle(i)));
        var (window, box, scroll) = Mount(rows);
        var expected = Expected(rows, Chrome(box, ((Row)rows[0]).Height));

        // Every type has been realized once at the top; from there on the extent is known exactly wherever the viewport is as
        // long as it moves by scrolling (the stock panel figure moves with the mix of rows on screen). A jump far past the
        // realized rows is outside this: the realized rows then start at a position the stock panel guesses.
        var steps = Enumerable.Range(0, 49).Select(i => i * 250d).ToList();
        steps.AddRange(Enumerable.Reverse(steps.ToArray()));
        foreach (var y in steps)
        {
            ScrollTo(window, scroll, y);
            Assert.True(Math.Abs(expected - scroll.Extent.Height) < 1, $"y={y} off={scroll.Offset.Y} extent={scroll.Extent.Height} expected={expected}");
        }

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScrollIntoView_of_a_row_never_seen_lands_on_it_even_from_the_bottom() => SharedUi.RunAsync(() =>
    {
        var rows = new ObservableCollection<object>(Enumerable.Range(0, 300).Select(i => (object)Cycle(i)));
        var (window, box, scroll) = Mount(rows);
        ScrollTo(window, scroll, scroll.Extent.Height);
        ScrollTo(window, scroll, scroll.Extent.Height);

        box.ScrollIntoView(120);
        SharedUi.Settle(window, 6);

        var container = box.ContainerFromIndex(120);
        Assert.NotNull(container);
        var top = container.TranslatePoint(default, box)!.Value.Y;
        Assert.InRange(top, 0, Viewport - container.Bounds.Height);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Extent_follows_a_width_change_for_rows_whose_height_depends_on_width() => SharedUi.RunAsync(() =>
    {
        var rows = new ObservableCollection<object>(Enumerable.Range(0, 200).Select(_ => (object)new Wrapping()));
        var (window, box, scroll) = Mount(rows, width: 400);
        var chrome = box.ContainerFromIndex(0)!.Bounds.Height - 100;
        Assert.Equal(200 * (100 + chrome), scroll.Extent.Height, 1);

        // Rows narrower than 300 wrap to twice the height. Heights remembered at the old width must not price the rows below.
        window.Width = 250;
        SharedUi.Settle(window, 6);
        Assert.Equal(200 * (200 + chrome), scroll.Extent.Height, 1);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Removed_and_reset_rows_leave_no_stale_height_behind() => SharedUi.RunAsync(() =>
    {
        var rows = new ObservableCollection<object>(Enumerable.Range(0, 300).Select(i => (object)new Medium(i < 150 ? 100 : 200)));
        var (window, box, scroll) = Mount(rows);
        var chrome = Chrome(box, 100);

        // Dropping the rows realized at the top (a compaction of the oldest turns) must not leave their heights in the type average.
        for (var i = 0; i < 150; i++)
        {
            rows.RemoveAt(0);
        }

        SharedUi.Settle(window, 6);
        Assert.Equal(Expected(rows, chrome), scroll.Extent.Height, 1);

        // A reset with the same row types at other heights: nothing of the old figures may price the new rows.
        rows.Clear();
        foreach (var i in Enumerable.Range(0, 300))
        {
            rows.Add(new Medium(150));
        }

        SharedUi.Settle(window, 6);
        Assert.Equal(300 * (150 + chrome), scroll.Extent.Height, 1);

        window.Close();
        return Task.CompletedTask;
    });
}
