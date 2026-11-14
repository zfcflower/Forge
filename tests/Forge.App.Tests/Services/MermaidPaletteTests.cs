using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the palette the Mermaid renderer paints with (wave W11): it reads the host's theme tokens, so light and dark produce
/// different colours (and therefore different cache keys), and the layout never depends on which variant is active.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MermaidPaletteTests
{
    [Fact]
    public Task Light_and_dark_read_different_tokens() => SharedUi.RunAsync(() =>
    {
        var host = new Border();
        var window = new Window { Width = 320, Height = 200, Content = host, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        SharedUi.Settle(window);
        var light = MermaidPalette.FromTheme(host);

        window.RequestedThemeVariant = ThemeVariant.Dark;
        SharedUi.Settle(window);
        var dark = MermaidPalette.FromTheme(host);

        Assert.NotEqual(light.CacheKey, dark.CacheKey);
        Assert.NotEqual(light.Foreground, dark.Foreground);
        Assert.NotEqual(light.Background, dark.Background);
        Assert.DoesNotContain("rgba", light.CacheKey, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            new[] { light.Background, light.Foreground, light.Accent, light.Muted, light.Surface, light.Border, light.Line },
            static hex => Assert.Matches("^#[0-9A-F]{6}$", hex));

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void The_fallback_palette_is_opaque_and_distinct()
    {
        var fallback = MermaidPalette.Fallback;

        Assert.All(
            new[] { fallback.Background, fallback.Foreground, fallback.Accent, fallback.Muted, fallback.Surface, fallback.Border, fallback.Line },
            static hex => Assert.Matches("^#[0-9A-F]{6}$", hex));
        Assert.NotEqual(fallback.Background, fallback.Foreground);

        // The CSS variable names are exactly the ones Mermaider writes into the SVG root.
        var variables = fallback.Variables();
        Assert.Equal(fallback.Background, variables["--bg"]);
        Assert.Equal(fallback.Foreground, variables["--fg"]);
        Assert.Equal(fallback.Accent, variables["--accent"]);
        Assert.Equal(7, variables.Count);
    }
}
