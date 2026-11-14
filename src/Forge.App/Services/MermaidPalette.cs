using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Services;

/// <summary>
/// The colours one Mermaid diagram is painted in, as <c>#RRGGBB</c> strings: what Mermaider's <c>RenderOptions</c> takes and
/// what <see cref="MermaidSvgColors"/> resolves the SVG's CSS variables against. Derived from the host's theme tokens so a
/// diagram follows light/dark like every other block, with opaque fallbacks for a host that has no theme resources (tests).
/// </summary>
internal readonly record struct MermaidPalette(
    string Background,
    string Foreground,
    string Accent,
    string Muted,
    string Surface,
    string Border,
    string Line)
{
    /// <summary>Used when a resource is missing or is not a solid colour; matches the light theme tokens.</summary>
    internal static MermaidPalette Fallback { get; } = new("#FFFFFF", "#1A1A1A", "#2F7449", "#6E6E6E", "#F2F2F2", "#D4D4D8", "#9CA3AF");

    /// <summary>
    /// Reads the theme tokens from <paramref name="scope"/> for its current variant. Called when a render starts (and again on
    /// a theme switch), never per frame: resource lookups are cheap but not free, and the palette is part of the cache key.
    /// </summary>
    internal static MermaidPalette FromTheme(StyledElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var variant = scope.ActualThemeVariant;
        return new MermaidPalette(
            Read(scope, variant, "SurfaceBackgroundBrush", Fallback.Background),
            Read(scope, variant, "TextPrimaryBrush", Fallback.Foreground),
            Read(scope, variant, "AccentTextBrush", Fallback.Accent),
            Read(scope, variant, "TextMutedBrush", Fallback.Muted),
            Read(scope, variant, "SurfaceMutedBrush", Fallback.Surface),
            Read(scope, variant, "BorderBrush", Fallback.Border),
            Read(scope, variant, "TextMutedBrush", Fallback.Line));
    }

    /// <summary>The identity of this palette in the render cache: two themes that map to the same colours share a picture.</summary>
    internal string CacheKey => string.Concat(Background, ",", Foreground, ",", Accent, ",", Muted, ",", Surface, ",", Border, ",", Line);

    /// <summary>The same colours under the CSS variable names Mermaider writes into the SVG root.</summary>
    internal Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["--bg"] = Background,
        ["--fg"] = Foreground,
        ["--accent"] = Accent,
        ["--muted"] = Muted,
        ["--surface"] = Surface,
        ["--border"] = Border,
        ["--line"] = Line,
    };

    private static string Read(StyledElement scope, Avalonia.Styling.ThemeVariant variant, string key, string fallback) =>
        scope.TryFindResource(key, variant, out var value) && value is ISolidColorBrush { Color: var color }
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : fallback;
}
