using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>What a dragged TODO row carries: its id (the row to move) and its layer (null = the loose queue).</summary>
/// <param name="Id">The stored item's id.</param>
/// <param name="GroupId">The group the row is a step of, or null for a loose item.</param>
internal sealed record TodoDragSource(string Id, string? GroupId);

/// <summary>
/// The TODO panel's view. The row template is shared by loose items and by steps, so the reorder gesture lives here on the
/// row itself: a press on a not-started row starts a drag, and a drop on a row of the same layer (the loose queue, or the
/// same group) moves the source to that row's position. Only a not-started row can be dragged and a drag never changes
/// layers, so no drop indicator is drawn - the dragged row just fades. Everything else the panel does is a command on its
/// view model.
/// </summary>
public partial class TodoToolView : UserControl
{
    /// <summary>How the dragged row looks while the operation runs.</summary>
    private const double DraggingOpacity = 0.4;

    /// <summary>The dragged row travels in-process only: the drop needs its layer as well as its id.</summary>
    private static readonly DataFormat<TodoDragSource> TodoRowFormat =
        DataFormat.CreateInProcessFormat<TodoDragSource>("forge.app/todo-row");

    public TodoToolView()
    {
        InitializeComponent();
    }

    private void OnNewTaskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (DataContext is TodoListViewModel viewModel && viewModel.AddCommand.CanExecute(null))
        {
            viewModel.AddCommand.Execute(null);
        }
    }

    /// <summary>Starts the drag of a not-started row; a press on one of the row's own buttons belongs to that button.</summary>
    private async void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not TodoRowViewModel model || !model.CanDrag)
        {
            return;
        }

        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, row); visual = visual.GetVisualParent())
        {
            if (visual is Button)
            {
                return;
            }
        }

        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TodoRowFormat, new TodoDragSource(model.Id, model.GroupId)));
        row.Opacity = DraggingOpacity;
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        catch (InvalidOperationException)
        {
            // The platform refused the drag (the pressed pointer was gone before it started); the row stays where it is.
        }
        finally
        {
            row.Opacity = 1;
        }
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = SameLayer(sender, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drops the dragged row onto the target row's position; the store clamps the order to the layer.</summary>
    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (!SameLayer(sender, e)
            || sender is not Control row
            || row.DataContext is not TodoRowViewModel target
            || DataContext is not TodoListViewModel panel
            || e.DataTransfer.TryGetValue(TodoRowFormat) is not { } source)
        {
            return;
        }

        e.Handled = true;
        _ = panel.MoveAsync(source.Id, target.Order, CancellationToken.None);
    }

    /// <summary>True when the drop target is a row of the dragged row's own layer: the loose queue, or one group.</summary>
    private static bool SameLayer(object? sender, DragEventArgs e) =>
        sender is Control row
        && row.DataContext is TodoRowViewModel target
        && e.DataTransfer.TryGetValue(TodoRowFormat) is { } source
        && !string.Equals(source.Id, target.Id, StringComparison.Ordinal)
        && string.Equals(source.GroupId, target.GroupId, StringComparison.Ordinal);
}
