using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Renders generated tree-menu entries (lifecycle 5.4 rule 1) as menu controls: a disabled entry carries its reason as a
/// tooltip that shows while disabled, an enabled one its warning, and the default entry is bold (5.2).
/// </summary>
internal static class TreeMenuControls
{
    public static List<Control> Build(IReadOnlyList<TreeMenuItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var controls = new List<Control>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                controls.Add(new Separator());
                continue;
            }

            var menuItem = new MenuItem
            {
                Header = item.Header,
                IsEnabled = item.IsEnabled,
                Command = item.IsEnabled ? item.Command : null,
            };
            if (item.IsDefault)
            {
                menuItem.FontWeight = FontWeight.Bold;
            }

            if (item.HasTip)
            {
                ToolTip.SetTip(menuItem, item.Tip);
                ToolTip.SetShowOnDisabled(menuItem, true);
            }

            AutomationProperties.SetAutomationId(menuItem, "tree-menu-" + item.Key);
            controls.Add(menuItem);
        }

        return controls;
    }
}
