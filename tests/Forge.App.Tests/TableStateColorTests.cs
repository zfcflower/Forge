using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The table's own two row states. Pressing a row and selecting it are the Tiffany accent the user asked for (a second
/// hue beside the brand mint: the mint says "ours", the blue says "the row you are acting on"), and the same pale tint marks
/// the editable cells of the model metadata table. The mapping is a set of resource aliases in
/// <c>Resources/Tokens.axaml</c>, so what this pins is that the framework's keys resolve to Forge's tokens - a copied colour
/// would pass a visual check and still drift.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TableStateColorTests
{
    [Fact]
    public Task Row_press_and_selection_alias_the_tiffany_tokens() => SharedUi.RunAsync(() =>
    {
        var window = new Window();
        IBrush Shared(string key) => Resource(window, key, ThemeVariant.Light);

        // Pressed (not selected) is the pale tint the editable cells wear; selection is the deeper step, and its
        // pointer-over / pressed variants step up together.
        Assert.Same(Shared("TiffanyTintBrush"), Shared("TableViewRowBackgroundPressed"));
        Assert.Same(Shared("TiffanySelectionBrush"), Shared("TableViewRowBackgroundSelected"));
        Assert.Same(Shared("TiffanySelectionStrongBrush"), Shared("TableViewRowBackgroundSelectedPointerOver"));
        Assert.Same(Shared("TiffanySelectionStrongBrush"), Shared("TableViewRowBackgroundSelectedPressed"));

        // Both themes define the family: a dark variant without them would fall back to light tints on dark surfaces.
        foreach (var key in new[] { "TiffanyTintBrush", "TiffanySelectionBrush", "TiffanySelectionStrongBrush" })
        {
            Assert.NotNull(Resource(window, key, ThemeVariant.Light));
            Assert.NotNull(Resource(window, key, ThemeVariant.Dark));
        }

        // The three steps are the same hue at increasing depth, so the states are ordered rather than swapped.
        var tint = Assert.IsAssignableFrom<ISolidColorBrush>(Shared("TiffanyTintBrush")).Color;
        var selected = Assert.IsAssignableFrom<ISolidColorBrush>(Shared("TiffanySelectionBrush")).Color;
        var strong = Assert.IsAssignableFrom<ISolidColorBrush>(Shared("TiffanySelectionStrongBrush")).Color;
        Assert.True(Luminance(tint) > Luminance(selected), "the pressed tint is paler than a selected row");
        Assert.True(Luminance(selected) > Luminance(strong), "selected+pointerover is deeper than selected");
        return Task.CompletedTask;
    });

    private static IBrush Resource(Visual anchor, string key, ThemeVariant variant)
    {
        Assert.True(anchor.TryFindResource(key, variant, out var value), "missing resource: " + key + " (" + variant + ")");
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    private static double Luminance(Color color) => (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
}
