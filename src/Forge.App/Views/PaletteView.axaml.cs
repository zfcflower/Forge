using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The palette overlay: a card over the workbench with a source strip, one query line, the result list and the preview of
/// the selected row. The view owns the two things a view model cannot: keyboard routing (the palette takes Escape, the
/// arrows and Enter before the text box sees them) and focus (the query line is focused and selected whenever the palette
/// opens, so typing starts immediately after <c>Ctrl+P</c>).
/// </summary>
public partial class PaletteView : UserControl
{
    private PaletteViewModel? watched;

    public PaletteView()
    {
        InitializeComponent();

        // Tunnelling: Escape, Up, Down and Enter belong to the palette even though the focus sits in the text box.
        AddHandler(KeyDownEvent, OnSurfaceKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Results.DoubleTapped += OnResultsDoubleTapped;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => Watch(DataContext as PaletteViewModel);

    /// <summary>Moves the one PropertyChanged subscription the view holds to <paramref name="viewModel"/>.</summary>
    private void Watch(PaletteViewModel? viewModel)
    {
        if (ReferenceEquals(watched, viewModel))
        {
            return;
        }

        if (watched is not null)
        {
            watched.PropertyChanged -= OnViewModelPropertyChanged;
        }

        watched = viewModel;
        if (watched is not null)
        {
            watched.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => Watch(null);

    /// <summary>
    /// A detach dropped the subscription, so an attach takes it again: the same view can be re-attached with its data
    /// context unchanged (no DataContextChanged then), and it must still focus the query and follow the selection.
    /// </summary>
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Watch(DataContext as PaletteViewModel);
        if (watched is { IsOpen: true })
        {
            FocusQuery();
        }
    }

    /// <summary>Focusing has to wait for the palette to become visible: a collapsed control cannot take the focus.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (watched is not { } viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(PaletteViewModel.IsOpen) && viewModel.IsOpen)
        {
            FocusQuery();
            return;
        }

        // The arrow keys move the selection from the code-behind's tunnel handler, so the list no longer receives the key
        // itself and has to be told to bring the new row into view.
        if (e.PropertyName == nameof(PaletteViewModel.SelectedItem) && viewModel.SelectedItem is { } item)
        {
            Results.ScrollIntoView(item);
        }
    }

    private void FocusQuery() => Dispatcher.UIThread.Post(
        () =>
        {
            if (QueryBox.IsEffectivelyVisible)
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }
        },
        DispatcherPriority.Input);

    private void OnSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (watched is not { IsOpen: true } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                viewModel.Close();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                // A focused button (the close affordance) keeps its own Enter; everywhere else Enter opens the row.
                viewModel.Accept();
                e.Handled = true;
                break;
            case Key.Up:
                viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                viewModel.MoveSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void OnResultsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (watched is { IsOpen: true } viewModel && viewModel.SelectedItem is { IsNavigable: true })
        {
            viewModel.Accept();
            e.Handled = true;
        }
    }

    /// <summary>A click outside the card dismisses the palette, like Escape; the card itself swallows the press.</summary>
    private void OnScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (watched is { IsOpen: true } viewModel)
        {
            viewModel.Close();
            e.Handled = true;
        }
    }
}
