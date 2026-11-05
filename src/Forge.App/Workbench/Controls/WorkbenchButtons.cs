using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A 20x20 transparent icon button of the workbench chrome: the theme draws <see cref="Icon"/> at 12 px and supplies the
/// hover background. Used by panel title bars, tab headers and the floating window title bar.
/// </summary>
internal class PanelIconButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<PanelIconButton, Geometry?>(nameof(Icon));

    /// <summary>Icon geometry, reused from the token dictionary or from <c>Workbench.axaml</c>.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>A button of an auto-hide strip: its text is rotated by the strip, so the theme keeps it one strip wide.</summary>
internal sealed class AutoHideButton : Button;

/// <summary>Binds a styled property to a theme resource so a code-built control follows the light/dark switch.</summary>
internal static class WorkbenchResources
{
    public static void Link(StyledElement target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key));
}
