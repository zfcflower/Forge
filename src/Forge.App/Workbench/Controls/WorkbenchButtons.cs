using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A transparent icon button of the workbench chrome: the theme draws <see cref="Icon"/> and supplies the hover background,
/// and the button keeps a 20 px minimum so a 14 px icon still has a comfortable target. Used by panel title bars, tab
/// headers and the floating window title bar.
/// </summary>
internal class PanelIconButton : Button
{
    /// <summary>The theme's icon, sized from <see cref="IconSize"/> once the template is applied.</summary>
    private PathIcon? icon;

    /// <summary>The title bar's icon size; every tool except add-project uses it.</summary>
    public const double DefaultIconSize = 14;

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<PanelIconButton, Geometry?>(nameof(Icon));

    /// <summary>
    /// The icon's box; the button shrinks to the icon plus its own padding, so a 24 px tool gets a 30 px hit target without
    /// the tool having to know the title bar's metrics.
    /// </summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<PanelIconButton, double>(nameof(IconSize), DefaultIconSize);

    /// <summary>Paints the icon in the accent colour: the tool is a panel's primary action, not another chrome button.</summary>
    public static readonly StyledProperty<bool> IsAccentProperty =
        AvaloniaProperty.Register<PanelIconButton, bool>(nameof(IsAccent));

    /// <summary>Icon geometry, reused from the token dictionary or from <c>Workbench.axaml</c>.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool IsAccent
    {
        get => GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    /// <summary>
    /// Sizes the icon after the template is applied. A template binding on <c>Width</c>/<c>Height</c> does not reach a
    /// control that sets them in its own constructor, so the icon is measured here instead of from the markup.
    /// </summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        icon = e.NameScope.Find<PathIcon>("PART_Icon");
        ApplyIconSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconSizeProperty)
        {
            ApplyIconSize();
        }
    }

    private void ApplyIconSize()
    {
        if (icon is null)
        {
            return;
        }

        icon.Width = IconSize;
        icon.Height = IconSize;
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
