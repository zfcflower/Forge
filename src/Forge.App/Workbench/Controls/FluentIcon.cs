using Avalonia.Controls;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// The app's one icon surface. It keeps <see cref="PathIcon"/>'s rendering as it is and adds only the grid contract: every
/// icon geometry lives on a 24×24 grid, and the whole grid is scaled into the icon's box instead of the glyph's ink bounds
/// being stretched onto it — Fluent's glyphs only fill part of their grid, so stretching the ink would make a 14 px button
/// icon a size heavier than designed.
/// </summary>
/// <remarks>
/// This is not a drawing control: it overrides no render and knows no stroke, so the icons themselves stay data, defined in
/// one place (<c>Resources/Icons.axaml</c>) and painted by the built-in template below. It exists as its own type so the grid
/// contract applies by type: the stock <c>PathIcon</c> theme is left alone for the built-in controls that use it.
/// </remarks>
internal sealed class FluentIcon : PathIcon;
