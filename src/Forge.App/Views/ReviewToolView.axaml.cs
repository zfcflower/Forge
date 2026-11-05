using Avalonia.Controls;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class ReviewToolView : UserControl
{
    public ReviewToolView()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ReviewEntryViewModel entry } && DataContext is ReviewPanelViewModel viewModel)
        {
            viewModel.Open(entry);
        }
    }
}
