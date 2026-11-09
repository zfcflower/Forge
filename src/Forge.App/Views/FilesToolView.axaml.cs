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
