using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The "find references" list: one row per reported location, opened as a flyout next to the caret. Enter or a double click
/// jumps to the row, Escape closes — the flyout's own light dismissal handles that, so the view only forwards the two
/// gestures a keyboard user needs.
/// </summary>
public partial class ReferenceListView : UserControl
{
    public ReferenceListView()
    {
        InitializeComponent();
        ReferenceList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the user asks to close the list without picking a row.</summary>
    internal event EventHandler? CloseRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ReferenceList.ItemCount > 0)
        {
            ReferenceList.SelectedIndex = 0;
        }

        ReferenceList.Focus();
    }

    private ReferenceListViewModel? Model => DataContext as ReferenceListViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // The list's own handling covers Up/Down/PageUp/PageDown; Enter and Escape are the two the popup adds.
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = OpenSelected();
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool OpenSelected()
    {
        if (Model is not { } model || ReferenceList.SelectedItem is not ReferenceRowViewModel row)
        {
            return false;
        }

        model.Open(row);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
