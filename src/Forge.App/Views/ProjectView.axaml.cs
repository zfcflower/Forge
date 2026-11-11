using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class ProjectView : UserControl
{
    /// <summary>Key of the row the user picked; a refresh re-applies it, so the tree never loses the selection by itself.</summary>
    private string? selectedKey;

    /// <summary>True while a restore posted from a row change is in flight, so its own selection events are ignored.</summary>
    private bool restoring;

    private ProjectTreeViewModel? tree;

    /// <summary>The rows' context menu, made once and refilled from the row's generated entries on every open.</summary>
    private ContextMenu? rowMenu;

    public ProjectView()
    {
        InitializeComponent();
        // The whole row is the hit target, not just the chevron or the trailing buttons.
        TreeList.AddHandler(TappedEvent, OnRowTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        TreeList.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        TreeList.AddHandler(ContextRequestedEvent, OnRowContextRequested, RoutingStrategies.Bubble);
        TreeList.AddHandler(Button.ClickEvent, OnPlusClick, RoutingStrategies.Bubble);
        TreeList.SelectionChanged += OnSelectionChanged;
        if (TreeList.Items is INotifyCollectionChanged items)
        {
            items.CollectionChanged += OnRowsChanged;
        }
    }

    /// <summary>Follows the panel's view model, whose active-row signal means "another tab is on screen now".</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (tree is not null)
        {
            tree.ActiveRowChanged -= OnActiveRowChanged;
        }

        tree = DataContext as ProjectTreeViewModel;
        if (tree is not null)
        {
            tree.ActiveRowChanged += OnActiveRowChanged;
        }
    }

    private void OnActiveRowChanged(object? sender, EventArgs e) => ClearSelection();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (restoring)
        {
            return;
        }

        selectedKey = (TreeList.SelectedItem as TreeNodeViewModel)?.Key;
    }

    /// <summary>
    /// A refresh splices rows in and out, and the ListBox drops the selection when the selected item is one of them. The pick
    /// belongs to the user, not to the refresh, so it is put back once the list has settled (this also covers a tab switch,
    /// which refreshes the tree behind it).
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (selectedKey is null)
        {
            return;
        }

        restoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RestoreSelection();
            }
            finally
            {
                restoring = false;
            }
        });
    }

    private void RestoreSelection()
    {
        if (selectedKey is null || TreeList.SelectedItem is TreeNodeViewModel current && current.Key == selectedKey)
        {
            return;
        }

        foreach (var item in TreeList.Items)
        {
            if (item is TreeNodeViewModel node && node.Key == selectedKey)
            {
                TreeList.SelectedItem = node;
                return;
            }
        }
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || IsInsideButton(source))
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TreeNodeViewModel node })
        {
            if (node.Activate())
            {
                e.Handled = true;
            }

            return;
        }

        // A click on the empty part of the tree — below the last row, or the strip beside a row — takes the pick back: the
        // selection belongs to the user, so it has to be possible to drop it as well as set it. A click on the scroll bar is
        // not that click.
        if (source.FindAncestorOfType<ScrollBar>(includeSelf: true) is null)
        {
            ClearSelection();
        }
    }

    /// <summary>Drops the row selection, and with it the key a later refresh would have restored.</summary>
    private void ClearSelection()
    {
        selectedKey = null;
        TreeList.SelectedItem = null;
        TreeList.SelectedItems?.Clear();
    }

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && TreeList.SelectedItem is not null)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (TreeList.SelectedItem is TreeNodeViewModel node && node.Activate())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The row's menu, generated now from its snapshot (lifecycle 5.4 rules 1 and 4: no git or database call here). A row
    /// without a menu (agents, history containers) opens nothing.
    /// </summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: TreeNodeViewModel node } row)
        {
            return;
        }

        e.Handled = true;
        var items = node.MenuItems();
        if (items.Count == 0)
        {
            return;
        }

        rowMenu ??= new ContextMenu();
        rowMenu.ItemsSource = TreeMenuControls.Build(items);
        rowMenu.Open(row);
    }

    /// <summary>The trailing "+": one enabled entry runs at once, otherwise the first group opens under the button (5.3).</summary>
    private void OnPlusClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: TreeNodeViewModel node } button || !button.Classes.Contains("tree-plus"))
        {
            return;
        }

        e.Handled = true;
        if (node.TryRunPlus())
        {
            return;
        }

        var items = node.PlusItems();
        if (items.Count > 0)
        {
            new MenuFlyout { ItemsSource = TreeMenuControls.Build(items), Placement = PlacementMode.BottomEdgeAlignedRight }
                .ShowAt(button);
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
