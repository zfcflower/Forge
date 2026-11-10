using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class FilesToolView : UserControl
{
    public FilesToolView()
    {
        InitializeComponent();
    }

    // Button raises Click before it runs its Command. Hiding the flyout synchronously detaches the row and clears its bindings
    // (the item's DataContext and so its Command), so the pick was lost; the flyout closes once the command has run.
    private void OnScopePicked(object? sender, RoutedEventArgs e) => HideAfterCommand(ScopePicker);

    /// <summary>Fills in the lazily read parts of the picker (uncommitted count, turn file counts, commits) when it opens.</summary>
    private void OnScopeFlyoutOpening(object? sender, EventArgs e)
    {
        if (DataContext is FilesPanelViewModel { ChangeReview: { } review })
        {
            _ = review.RefreshScopePickerAsync();
        }
    }

    private void OnBranchPicked(object? sender, EventArgs e) => HideAfterCommand(CompareBranchPicker);

    private static void HideAfterCommand(Button picker) =>
        Dispatcher.UIThread.Post(() => picker.Flyout?.Hide(), DispatcherPriority.Input);

    private void OnChangedSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FilesPanelViewModel viewModel || sender is not ListBox list)
        {
            return;
        }

        switch (list.SelectedItem)
        {
            case FileDiffViewModel diff:
                viewModel.OpenChanged(diff);
                break;
            case ChangedFileViewModel change:
                viewModel.OpenChanged(change);
                break;
        }
    }
}
