using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>Geometry lookups for chrome built in code; the keys live in the token and workbench dictionaries.</summary>
internal static class WorkbenchIcons
{
    public static Geometry? Find(IResourceHost? anchor, string key) =>
        anchor is not null && anchor.TryFindResource(key, out var value) && value is Geometry geometry ? geometry : null;

    public static Geometry? Find(string key) => Find(Application.Current, key);
}

/// <summary>
/// Renders one title-bar tool of a panel: a <see cref="PanelToolButton"/> becomes an icon button, anything else is handed
/// to the application data templates as it is.
/// </summary>
internal sealed class HeaderToolTemplate : IDataTemplate
{
    public bool Match(object? data) => true;

    public Control? Build(object? param)
    {
        if (param is not PanelToolButton tool)
        {
            return new ContentPresenter
            {
                Content = param,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var button = new PanelIconButton
        {
            Icon = WorkbenchIcons.Find(tool.IconKey),
            Command = tool.Command,
            IconSize = tool.IconSize,
            IsAccent = tool.IsAccent,
        };
        ToolTip.SetTip(button, tool.Tooltip);
        Avalonia.Automation.AutomationProperties.SetAutomationId(button, tool.Id);
        Avalonia.Automation.AutomationProperties.SetName(button, tool.Tooltip);
        return button;
    }
}
