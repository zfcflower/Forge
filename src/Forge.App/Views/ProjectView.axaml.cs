using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class ProjectView : UserControl
{
    public ProjectView()
    {
        InitializeComponent();
        // The whole row is the hit target, not just the chevron or the trailing buttons.
        TreeList.AddHandler(TappedEvent, OnRowTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        TreeList.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || IsInsideButton(source))
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TreeNodeViewModel node }
            && node.Activate())
        {
            e.Handled = true;
        }
    }

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (TreeList.SelectedItem is TreeNodeViewModel node && node.Activate())
        {
            e.Handled = true;
        }
    }

    /// <summary>The row's own buttons (chevron, new session, open) already ran their command; the row must not run a second one.</summary>
    private static bool IsInsideButton(Visual source)
    {
        for (Visual? current = source; current is not null and not ListBoxItem; current = current.GetVisualParent())
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }
}
