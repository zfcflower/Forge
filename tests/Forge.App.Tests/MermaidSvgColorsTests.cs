using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The colour preprocessor between Mermaider and Svg.Skia. The regression these tests pin: a diagram with a
/// database-shaped node (the cylinder of <c>A[("…")]</c>) failed to render with "unknown variable --_line", because
/// Mermaider writes its fills as <c>color-mix()</c> over internal custom properties that exist only as declarations in
/// the SVG's own <c>&lt;style&gt;</c> block, which the resolver never read.
/// </summary>
public sealed class MermaidSvgColorsTests
{
    /// <summary>The palette Mermaider receives, so the expected hex values are exact rather than relative.</summary>
    private static MermaidPalette Palette => new(
        "#FAFAFA", "#1D2129", "#067A76", "#66717D", "#F4F7F8", "#D8DEE1", "#66717D");

    [Fact]
    public void Database_shape_mix_resolves_the_style_declared_line_variable()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" style="--bg:#FAFAFA;--fg:#1D2129;--line:#66717D;--accent:#067A76;--muted:#66717D;--surface:#F4F7F8;--border:#D8DEE1">
            <style>
              svg {
                --_line: var(--line, color-mix(in srgb, var(--fg) 50%, var(--bg)));
              }
            </style>
            <rect x="16" y="23" width="143" height="42" fill="color-mix(in srgb, var(--_line) 11%, var(--bg))" stroke="color-mix(in srgb, var(--_line) 74%, var(--fg))" />
            </svg>
            """;

        var ok = MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem);

        Assert.True(ok, problem);
        // #66717D (the palette's line) at 11% into #FAFAFA, and at 74% into #1D2129: the exact colours Svg.Skia
        // must paint, computed by hand from the CSS color-mix rules the evaluator implements.
        Assert.Contains("fill=\"#EAEBEC\"", resolved);
        Assert.Contains("stroke=\"#535C67\"", resolved);
        Assert.DoesNotContain("color-mix(", resolved);
    }

    [Fact]
    public void A_declaration_whose_value_is_a_mix_resolves_through_the_nested_mix()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" style="--bg:#FAFAFA;--fg:#1D2129;--line:#66717D;--accent:#067A76;--muted:#66717D;--surface:#F4F7F8;--border:#D8DEE1">
            <style>
              svg {
                --_line-soft: color-mix(in srgb, var(--fg) 14%, var(--bg));
              }
            </style>
            <rect x="16" y="23" width="143" height="42" fill="color-mix(in srgb, var(--_line-soft) 100%, var(--bg))" />
            </svg>
            """;

        var ok = MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem);

        Assert.True(ok, problem);
        // --_line-soft is #DBDCDD (fg at 14% into bg); carrying it at 100% through the outer mix keeps it unchanged.
        Assert.Contains("fill=\"#DBDCDD\"", resolved);
    }

    [Fact]
    public void A_cycle_of_style_declarations_is_cut_off_by_the_nesting_limit()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" style="--bg:#FAFAFA;--fg:#1D2129;--line:#66717D;--accent:#067A76;--muted:#66717D;--surface:#F4F7F8;--border:#D8DEE1">
            <style>
              svg {
                --a: var(--b);
                --b: var(--a);
              }
            </style>
            <rect x="16" y="23" width="143" height="42" fill="color-mix(in srgb, var(--a) 10%, var(--bg))" />
            </svg>
            """;

        var ok = MermaidSvgColors.TryResolve(svg, Palette, out _, out var problem);

        Assert.False(ok);
        Assert.Contains("nest too deep", problem);
    }
}
