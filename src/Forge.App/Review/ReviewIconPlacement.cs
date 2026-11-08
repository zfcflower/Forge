using Avalonia;

namespace Forge.App.Review;

/// <summary>
/// Where the floating "add review comment" icon goes. It belongs to the bottom right of the anchor — the end of the
/// selection, or the row that was clicked in the line-number margin — because that is where the pointer just was; it used
/// to hug the right edge of the viewport, far from the text. When the anchor sits too close to the right edge the icon
/// flips to its left, and the result is always clipped into the viewport so it can never be dropped off-screen.
/// </summary>
internal static class ReviewIconPlacement
{
    /// <summary>The icon's edge length, in DIPs.</summary>
    public const double IconSize = 26;

    /// <summary>Distance kept between the anchor and the icon, and between the icon and the viewport edge.</summary>
    public const double Gap = 4;

    /// <summary>Margin kept from the viewport's own edges.</summary>
    public const double Edge = 8;

    /// <summary>The icon's top-left corner for <paramref name="anchor"/> (bottom right of the text position).</summary>
    public static Point Find(Rect viewport, Point anchor)
    {
        var left = anchor.X + Gap;
        if (left + IconSize > viewport.Right - Edge)
        {
            left = anchor.X - IconSize - Gap;
        }

        var minX = viewport.X + Edge;
        var maxX = Math.Max(minX, viewport.Right - Edge - IconSize);
        var minY = viewport.Y + Edge;
        var maxY = Math.Max(minY, viewport.Bottom - Edge - IconSize);
        return new Point(Math.Clamp(left, minX, maxX), Math.Clamp(anchor.Y + Gap, minY, maxY));
    }
}
