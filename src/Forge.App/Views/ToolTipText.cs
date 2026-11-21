using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Views;

/// <summary>
/// A tooltip that carries a whole text. Two things are needed for it, and both live here:
/// <list type="bullet">
/// <item>the tooltip theme caps its own width (Fluent's <c>ToolTip</c> <c>MaxWidth</c> is 320), so the text wraps inside
/// that cap instead of running past it or being clipped;</item>
/// <item>Avalonia hands a tooltip that hangs on a <see cref="TextBlock"/> its owner's text layout, so the tip's text keeps
/// the owner's wrapping and neither limit is inherited.</item>
/// </list>
/// Callers keep the returned block and rewrite its text; the tip goes on the owner control (a row, a border), never on the
/// trimmed text inside it.
/// </summary>
internal static class ToolTipText
{
    /// <summary>Width the wrapped text asks for: inside the tooltip's own cap, its padding included.</summary>
    public const double MaxWidth = 300;

    /// <summary>A tip's content, wrapping inside the cap; <paramref name="block"/> is what a caller rewrites later.</summary>
    public static TextBlock Wrap(string? text, out TextBlock block)
    {
        block = new TextBlock
        {
            Text = text ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxWidth,
            TextTrimming = TextTrimming.None,
        };
        return block;
    }

    /// <summary>The same content for a caller that never rewrites it.</summary>
    public static TextBlock Wrap(string? text) => Wrap(text, out _);
}
