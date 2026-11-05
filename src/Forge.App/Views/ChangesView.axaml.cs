using Avalonia.Controls;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class ChangesView : UserControl
{
    private ChangesViewModel? viewModel;

    public ChangesView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (viewModel is not null)
        {
            viewModel.FocusRequested -= OnFocusRequested;
        }

        viewModel = DataContext as ChangesViewModel;
        if (viewModel is not null)
        {
            viewModel.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFocusRequested(FileDiffViewModel file) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                var list = this.FindControl<ListBox>("RowList");
                if (list is null || viewModel is null)
                {
                    return;
                }

                var index = viewModel.IndexOfFile(file);
                if (index >= 0)
                {
                    list.ScrollIntoView(index);
                }
            },
            DispatcherPriority.Background);
}
