using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The Problems tab: a severity-filtered list of every diagnostic the language sessions hold. A double click (or Enter, or
/// a click while it is already selected) opens the file with the caret on the diagnostic.
/// </summary>
public partial class ProblemsPanelView : UserControl
{
    public ProblemsPanelView()
    {
        InitializeComponent();
        ProblemList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Opening the panel is the moment the list is worth building: until now nothing could see it.
        if (DataContext is ProblemsPanelViewModel model)
        {
            _ = model.RefreshAsync(CancellationToken.None);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ProblemsPanelViewModel model && this.IsAttachedToVisualTree())
        {
            _ = model.RefreshAsync(CancellationToken.None);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Open(ProblemList.SelectedItem);

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Open(ProblemList.SelectedItem))
        {
            e.Handled = true;
        }
    }

    private bool Open(object? item)
    {
        if (item is not ProblemRowViewModel row || DataContext is not ProblemsPanelViewModel model)
        {
            return false;
        }

        model.Open(row);
        return true;
    }
}
