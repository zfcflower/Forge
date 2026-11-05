using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class FilesToolView : UserControl
{
    public FilesToolView()
    {
        InitializeComponent();
    }

    private void OnChangedSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ChangedFileViewModel change } && DataContext is FilesPanelViewModel viewModel)
        {
            viewModel.OpenChanged(change);
        }
    }
}
