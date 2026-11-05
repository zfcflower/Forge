using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class SessionView : UserControl
{
    private SessionWorkspaceViewModel? viewModel;
    private INotifyCollectionChanged? items;

    public SessionView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (items is not null)
        {
            items.CollectionChanged -= OnItemsChanged;
            items = null;
        }

        viewModel = DataContext as SessionWorkspaceViewModel;
        if (viewModel is not null)
        {
            items = viewModel.Transcript.Items;
            items.CollectionChanged += OnItemsChanged;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScrollToEnd();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd()
    {
        var list = this.FindControl<ListBox>("Transcript");
        if (list?.ItemsSource is not { } source)
        {
            return;
        }

        var count = source.Cast<object>().Count();
        if (count > 0)
        {
            list.ScrollIntoView(count - 1);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        if (viewModel?.SendCommand.CanExecute(null) == true)
        {
            viewModel.SendCommand.Execute(null);
        }
    }
}
