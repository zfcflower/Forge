using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The compact finished sub-agent menu of the bottom group's title bar: a 20 px button with the item count that opens a
/// flyout listing the finished sub-agents. Choosing one selects it on the view model, which opens its read-only tab.
/// </summary>
public partial class FinishedSubAgentsHeaderView : UserControl
{
    public FinishedSubAgentsHeaderView()
    {
        InitializeComponent();
        if (MenuButton.Flyout is Flyout { Content: ListBox list })
        {
            list.SelectionChanged += OnSelectionChanged;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MenuButton.Flyout is { } flyout && e.AddedItems.Count > 0)
        {
            flyout.Hide();
        }
    }
}
