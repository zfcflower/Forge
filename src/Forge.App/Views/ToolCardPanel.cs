using Avalonia;
using Avalonia.Controls;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The tool card grid: up to <see cref="Columns"/> equal-width cards per row, and a full-width child (the open call's body)
/// starts a row of its own. One panel instead of nested items hosts, so a card and the body it opened are siblings in one
/// list and the body lands directly under the row it was clicked in.
/// <para>Item containers are the panel's children; whether a child is the full-width body is read from its data context
/// (<see cref="ToolOpenViewModel"/>), which the item template binds.</para>
/// </summary>
internal sealed class ToolCardPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<ToolCardPanel, int>(nameof(Columns), 4);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var width = availableSize.Width;
        var columnWidth = double.IsInfinity(width) ? 240 : width / columns;
        var total = 0d;
        var rowHeight = 0d;
        var inRow = 0;
        foreach (var child in Children)
        {
            if (IsWide(child))
            {
                if (inRow > 0)
                {
                    total += rowHeight;
                    rowHeight = 0;
                    inRow = 0;
                }

                child.Measure(new Size(width, double.PositiveInfinity));
                total += child.DesiredSize.Height;
                continue;
            }

            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++inRow == columns)
            {
                total += rowHeight;
                rowHeight = 0;
                inRow = 0;
            }
        }

        total += rowHeight;
        return new Size(double.IsInfinity(width) ? columnWidth * columns : width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var columnWidth = finalSize.Width / columns;
        var y = 0d;
        var index = 0;
        while (index < Children.Count)
        {
            if (IsWide(Children[index]))
            {
                Children[index].Arrange(new Rect(0, y, finalSize.Width, Children[index].DesiredSize.Height));
                y += Children[index].DesiredSize.Height;
                index++;
                continue;
            }

            // One visual row: the band's height is its tallest card, and no list is built to find it (this runs on every
            // layout pass of every realized round, so it must not allocate).
            var start = index;
            var rowHeight = 0d;
            while (index < Children.Count && index - start < columns && !IsWide(Children[index]))
            {
                rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
                index++;
            }

            for (var i = start; i < index; i++)
            {
                Children[i].Arrange(new Rect((i - start) * columnWidth, y, columnWidth, rowHeight));
            }

            y += rowHeight;
        }

        return finalSize;
    }

    /// <summary>Whether this child is the open call's body, which takes a row of its own.</summary>
    private static bool IsWide(Control child) => child.DataContext is ToolOpenViewModel;
}
