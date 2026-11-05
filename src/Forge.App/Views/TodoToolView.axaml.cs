using Avalonia.Controls;
using Avalonia.Input;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class TodoToolView : UserControl
{
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
}
