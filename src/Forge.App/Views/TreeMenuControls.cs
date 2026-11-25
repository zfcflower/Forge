using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Renders generated tree-menu entries (lifecycle 5.4 rule 1) as menu controls: a disabled entry carries its reason as a
/// tooltip that shows while disabled, an enabled one its warning, and — where the row asks for it — the default entry is
/// bold (5.2). A branch row keeps its menu flat: its first group is ordered by the one entry its trailing "+" runs.
/// </summary>
internal static class TreeMenuControls
{
    public static List<Control> Build(IReadOnlyList<TreeMenuItemViewModel> items, bool boldDefault = true)
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
                Command = item.HasKinds || !item.IsEnabled ? null : item.Command,
            };
            if (boldDefault && item.IsDefault)
            {
                menuItem.FontWeight = FontWeight.Bold;
            }

            if (item.HasKinds)
            {
                ShrinkSubmenuChevron(menuItem);

                // The submenu is where the kind is picked: the parent row only opens it.
                foreach (var kind in item.Kinds)
                {
                    var child = new MenuItem
                    {
                        Header = KindHeader(kind),
                        IsEnabled = kind.IsEnabled && kind.Command is not null,
                        Command = kind.IsEnabled ? kind.Command : null,
                    };

                    if (kind.Reason is { Length: > 0 } reason)
                    {
                        ToolTip.SetTip(child, reason);
                        ToolTip.SetShowOnDisabled(child, true);
                    }

                    menuItem.Items.Add(child);
                }
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

    /// <summary>
    /// The submenu arrow's 8x16 size and its chevron geometry are written into the MenuItem's control template, and a template
    /// value outranks every style: a style setter for that path never applied. The size is therefore set on the template child
    /// itself, once the item has one - 6x6, so the arrow sits beside the label instead of dwarfing it.
    /// </summary>
    private static void ShrinkSubmenuChevron(MenuItem item) =>
        // The template's parts exist only once it has been applied: attaching to the visual tree happens before that.
        item.TemplateApplied += (_, _) =>
        {
            foreach (var shape in item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            {
                if (shape.Name == "PART_ChevronPath")
                {
                    shape.Width = 6;
                    shape.Height = 6;
                }
            }
        };

    /// <summary>
    /// One kind's row: the name, and under it the kind's own one-line summary. The fixed width is what makes the submenu wider
    /// than a list of bare names - it is the row's floor, and the popup sizes to the widest row.
    /// </summary>
    private static Control KindHeader(AgentKindMenuItem kind)
    {
        var header = new StackPanel { Spacing = 1, MinWidth = 250 };
        header.Children.Add(new TextBlock
        {
            Text = kind.Name,
            FontWeight = kind.IsDefault ? FontWeight.Bold : FontWeight.Normal,
        });
        if (kind.Description.Length > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = kind.Description,
                Classes = { "caption" },
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
            });
        }

        return header;
    }
}
