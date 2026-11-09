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
