using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class FilesToolView : UserControl
{
    public FilesToolView()
    {
        InitializeComponent();
    }

    private void OnScopePicked(object? sender, RoutedEventArgs e) => ScopePicker.Flyout?.Hide();

    /// <summary>Fills in the lazily read parts of the picker (uncommitted count, turn file counts, commits) when it opens.</summary>
    private void OnScopeFlyoutOpening(object? sender, EventArgs e)
    {
        if (DataContext is FilesPanelViewModel { ChangeReview: { } review })
        {
            _ = review.RefreshScopePickerAsync();
        }
    }

    private void OnBranchPicked(object? sender, RoutedEventArgs e) => CompareBranchPicker.Flyout?.Hide();

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
