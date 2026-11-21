using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Views;

/// <summary>
/// A tooltip that carries a whole text: the tooltip theme caps its own width (Fluent's <c>ToolTip</c> <c>MaxWidth</c> is
/// 320), so a one-line string is clipped at that cap - the reader only ever sees the beginning. Wrapping the text inside
/// that cap instead lets a long title, task or message run onto as many lines as it needs.
/// </summary>
internal static class ToolTipText
{
    /// <summary>Width the wrapped text asks for: inside the tooltip's own cap, its padding included.</summary>
    public const double MaxWidth = 300;

    /// <summary>A tip's content control; callers keep the instance and rewrite <see cref="TextBlock.Text"/>.</summary>
    public static TextBlock Wrap(string? text) => new()
    {
        Text = text ?? string.Empty,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = MaxWidth,
    };
}
