using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The compact finished sub-agent menu of the bottom group's title bar: a 20 px button with the item count that opens a
/// flyout listing the finished sub-agents. Choosing one opens its read-only tab and closes the menu; the rows keep no
/// selection, so the same entry can be picked again after its tab is closed.
/// </summary>
public partial class FinishedSubAgentsHeaderView : UserControl
{
    public FinishedSubAgentsHeaderView()
    {
        InitializeComponent();
    }

    private void OnAgentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubAgentViewModel agent }
            && DataContext is FinishedSubAgentsViewModel menu)
        {
            menu.Open(agent);
            if (MenuButton.Flyout is Flyout flyout)
            {
                flyout.Hide();
            }
        }
    }
}
