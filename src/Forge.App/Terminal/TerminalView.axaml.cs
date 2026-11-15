using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Forge.App.Terminal;

/// <summary>
/// The body of a terminal tab: the close banner, the buffer-search bar, the surface and the status line. The view holds no
/// terminal state of its own; everything it shows is a property of <see cref="TerminalViewModel"/>, so a floating window
/// that renders the same view model shows the same terminal. The only state here is the search bar's own focus and key
/// handling, which belongs to the view because it is about widgets, not about the terminal.
/// </summary>
public partial class TerminalView : UserControl
{
    private TerminalViewModel? viewModel;
    private bool attached;

    public TerminalView()
    {
        InitializeComponent();
        SearchQuery.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Subscribe(DataContext as TerminalViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (attached)
        {
            Subscribe(DataContext as TerminalViewModel);
        }
    }

    private void Subscribe(TerminalViewModel? next)
    {
        if (ReferenceEquals(viewModel, next))
        {
            return;
        }

        if (viewModel is not null)
        {
            viewModel.SearchOpened -= OnSearchOpened;
        }

        viewModel = next;
        if (viewModel is not null)
        {
            viewModel.SearchOpened += OnSearchOpened;
        }
    }

    /// <summary>Ctrl+F (or the bar being reopened): the caret goes into the query box, with the old query selected.</summary>
    private void OnSearchOpened(object? sender, EventArgs e)
    {
        SearchQuery.Focus();
        SearchQuery.SelectAll();
    }

    /// <summary>Enter walks to the next match, Shift+Enter to the previous one, Esc closes the bar.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) != 0:
                viewModel.SearchPrevious();
                e.Handled = true;
                break;
            case Key.Enter:
                viewModel.SearchNext();
                e.Handled = true;
                break;
            case Key.Escape:
                viewModel.CloseSearch();
                e.Handled = true;
                break;
        }
    }
}
