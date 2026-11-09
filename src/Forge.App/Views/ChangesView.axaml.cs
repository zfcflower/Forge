using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel?.SelectedFile is not { } file)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            viewModel.ReportCopyPathFailure("clipboard unavailable");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(file.Path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine("[changes] copy path failed: " + exception.Message);
            viewModel.ReportCopyPathFailure(exception.Message);
        }
    }
}
