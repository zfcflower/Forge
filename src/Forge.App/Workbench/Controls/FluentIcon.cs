using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// The app's one icon surface. It keeps <see cref="PathIcon"/>'s rendering as it is and adds only the grid contract: every
/// icon geometry lives on a 24×24 grid, and the whole grid is scaled into the icon's box instead of the glyph's ink bounds
/// being stretched onto it — Fluent's glyphs only fill part of their grid, so stretching the ink would make a 14 px button
/// icon a size heavier than designed. It also draws the one turn the chrome asks of a glyph (<see cref="Rotated"/>).
/// </summary>
/// <remarks>
/// This is not a drawing control: it overrides no render and knows no stroke, so the icons themselves stay data, defined in
/// one place (<c>Resources/Icons.axaml</c>) and painted by the built-in template below. It exists as its own type so the grid
/// contract applies by type: the stock <c>PathIcon</c> theme is left alone for the built-in controls that use it.
/// </remarks>
internal sealed partial class FluentIcon : PathIcon
{

    static FluentIcon() =>
        RotatedProperty.Changed.AddClassHandler<FluentIcon>((icon, _) => icon.Turn());

    /// <summary>
    /// The chevron contract: <c>chevron_right</c> points right while its row is closed and down once the row is open.
    /// </summary>
    /// <remarks>
    /// The turn is drawn here and not by a <c>tree-chevron</c> style: a style-set <see cref="Visual.RenderTransform"/> never
    /// reaches a <see cref="PathIcon"/> — the glyph's own transform wins, so the style's value is silently dropped, and a
    /// <c>RotateTransform.Angle</c> setter is dropped with it. Treating the turn as part of the icon's own contract is what
    /// keeps one chevron behaving like the next.
    /// </remarks>
    /// <summary>Whether the glyph is drawn a quarter turn clockwise.</summary>
    [GeneratedStyledProperty]
    public partial bool Rotated { get; set; }

    /// <summary>A closed row carries no transform at all: most glyphs on screen are chevrons that stay as they are.</summary>
    private void Turn() => RenderTransform = Rotated ? new RotateTransform(90) : null;
}
