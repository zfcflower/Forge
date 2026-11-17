using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Views.Preview;

/// <summary>
/// The table preview: a fixed header row and virtualized, fixed-height, monospace data rows. The columns spread over the tab's
/// width when the table is narrower than it, and every header cell has a grip that drags the width of its whole column.
/// </summary>
public partial class TablePreviewView : UserControl
{
    /// <summary>The margin the rows keep on each side, which the columns must not cover.</summary>
    private const double SideMargins = 24;

    public TablePreviewView()
    {
        InitializeComponent();
        AddHandler(Thumb.DragDeltaEvent, OnGripDrag, RoutingStrategies.Bubble);
        SizeChanged += (_, _) => Fit();
        DataContextChanged += (_, _) => Fit();
    }

    private void Fit()
    {
        if (DataContext is TableFilePreviewViewModel table && Bounds.Width > SideMargins)
        {
            table.FitTo(Bounds.Width - SideMargins - 1);
        }
    }

    private void OnGripDrag(object? sender, VectorEventArgs e)
    {
        if (e.Source is Thumb { DataContext: TablePreviewCellViewModel cell } && DataContext is TableFilePreviewViewModel table)
        {
            table.Resize(cell.Column, e.Vector.X);
            e.Handled = true;
        }
    }
}
