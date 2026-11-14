using Forge.App.Services;
using Mermaider;
using Mermaider.Models;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the SVG colour preprocessing (wave W11). Svg.Skia paints an unresolved <c>color-mix()</c> black, so every offset the
/// renderer leaves in the SVG has to become a concrete <c>#rrggbb</c>: the CSS color-mix weights (an omitted percentage, two
/// percentages summing below 100% scaling the alpha) and the palette variables with nested fallbacks. Anything else must fail
/// with a reason instead of painting a black shape.
/// </summary>
public sealed class MermaidSvgColorsTests
{
    private static MermaidPalette Palette => MermaidPalette.Fallback;

    [Theory]
    // 50/50 of red and blue, rounded per channel.
    [InlineData("in srgb, #ff0000 50%, #0000ff", "#800080")]
    [InlineData("in srgb, #ff0000, #0000ff", "#800080")]
    // A short hex colour means the same thing.
    [InlineData("in srgb, #f00, #00f", "#800080")]
    // 72/28 with the palette's background (#FFFFFF), so the 28% side lightens every channel it touches.
    [InlineData("in srgb, #ff0000 72%, var(--bg)", "#FF4747")]
    [InlineData("in srgb, var(--bg) 30%, var(--fg)", "#5F5F5F")]
    // An omitted percentage is 100% minus the other one.
    [InlineData("in srgb, #ff0000, #0000ff 25%", "#BF0040")]
    // A nested fallback resolves to the palette's accent (#2F7449).
    [InlineData("in srgb, var(--accent, var(--missing)), #000000", "#183A25")]
    public void A_color_mix_becomes_a_concrete_colour(string expression, string expected)
    {
        Assert.True(MermaidSvgColors.TryEvaluate(expression, Palette.Variables(), out var hex, out var problem), problem);
        Assert.Equal(expected, hex);
        Assert.Null(problem);
    }

    [Fact]
    public void A_sum_below_100_percent_keeps_the_ratio_and_scales_the_alpha()
    {
        // CSS color-mix(in srgb, red 40%, blue 40%) is the 50/50 mix at 80% opacity, so the channels are the plain mix and
        // only the reported alpha shrinks - the premultiplied channels are divided by the mixed alpha, not by the scaled one.
        Assert.True(
            MermaidSvgColors.TryEvaluate("in srgb, #ff0000 40%, #0000ff 40%", Palette.Variables(), out var hex, out var problem),
            problem);

        Assert.Equal("#800080CC", hex);
    }

    [Theory]
    [InlineData("in oklab, #ff0000, #0000ff", "only color-mix(in srgb")]
    [InlineData("in srgb, red, #0000ff", "neither a hex colour nor var()")]
    [InlineData("in srgb, #ff0000", "only color-mix(in srgb")]
    [InlineData("in srgb, var(--nope), #000000", "unknown variable --nope")]
    [InlineData("in srgb, #ff0000 120%, #0000ff", "bad percentage")]
    [InlineData("in srgb, #zz0000, #0000ff", "bad hex colour")]
    public void An_unsupported_expression_fails_with_a_reason(string expression, string reason)
    {
        Assert.False(MermaidSvgColors.TryEvaluate(expression, Palette.Variables(), out var hex, out var problem));
        Assert.Empty(hex);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_svg_without_color_mix_is_returned_unchanged()
    {
        const string svg = "<svg><path fill=\"#123456\" stroke=\"var(--line)\"/></svg>";

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem));
        Assert.Equal(svg, resolved);
        Assert.Null(problem);
    }

    [Fact]
    public void Every_color_mix_in_the_svg_is_replaced()
    {
        const string svg =
            "<svg><path fill=\"color-mix(in srgb, #ff0000, #0000ff)\"/>"
            + "<path stroke=\"color-mix(in srgb, #00ff00 50%, #000000)\"/></svg>";

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem), problem);
        Assert.DoesNotContain("color-mix(", resolved, StringComparison.Ordinal);
        Assert.Contains("fill=\"#800080\"", resolved, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#008000\"", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_var_with_a_fallback_keeps_the_parentheses_of_its_inner_call()
    {
        const string svg = "<svg><path fill=\"color-mix(in srgb, var(--accent, var(--missing)), #000000)\"/></svg>";

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem), problem);
        Assert.Contains("#183A25", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_color_mix_fails_instead_of_painting_a_black_shape()
    {
        const string svg = "<svg><path fill=\"color-mix(in srgb, #ff0000, #0000ff\"/></svg>";

        Assert.False(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem));
        Assert.Equal(svg, resolved);
        Assert.Equal("unterminated color-mix()", problem);
    }

    [Fact]
    public void A_label_that_spells_color_mix_is_text_and_is_left_alone()
    {
        // Mermaid labels are text content, not paints: a label documenting the CSS function must reach the picture verbatim
        // (and a broken spelling in a label must not fail the whole render).
        const string svg =
            "<svg><text>color-mix(in srgb, #ff0000, #0000ff)</text><text>color-mix(broken</text></svg>";

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem));
        Assert.Equal(svg, resolved);
        Assert.Null(problem);
    }

    [Fact]
    public void A_style_element_s_css_body_is_still_resolved()
    {
        // The <style> body is not a text node a reader sees: it paints, so its color-mix() offsets are resolved too.
        const string svg =
            "<svg><style>.edge{stroke:color-mix(in srgb, #ff0000, #0000ff)}</style>"
            + "<path stroke=\"color-mix(in srgb, #00ff00 50%, #000000)\"/></svg>";

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem), problem);
        Assert.DoesNotContain("color-mix(", resolved, StringComparison.Ordinal);
        Assert.Contains("stroke:#800080", resolved, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#008000\"", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_flowchart_svgs_have_every_colour_resolved()
    {
        // A decision node is the shape Mermaider tints with color-mix(); with the flat fills the block renders it must be
        // resolved before Svg.Skia sees it, or the diamond comes out a black box.
        const string source = "flowchart TD\n  A[Start] --> B{Decision}\n  B -->|yes| C[Done]\n";
        var svg = MermaidRenderer.RenderSvg(source, new RenderOptions
        {
            Bg = Palette.Background,
            Fg = Palette.Foreground,
            Accent = Palette.Accent,
            Muted = Palette.Muted,
            Surface = Palette.Surface,
            Border = Palette.Border,
            Line = Palette.Line,
            Transparent = true,
            Gradient = false,
            Elevation = 0,
        });

        Assert.Contains("color-mix(", svg, StringComparison.Ordinal);
        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem), problem);
        Assert.DoesNotContain("color-mix(", resolved, StringComparison.Ordinal);
        Assert.Contains("#", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowchart TD\n  A[Start] --> B{Decision}\n  B -->|yes| C[Done]\n")]
    [InlineData("sequenceDiagram\n  Alice->>Bob: Hello\n  Bob-->>Alice: Hi back\n")]
    [InlineData("stateDiagram-v2\n  [*] --> Idle\n  Idle --> Busy : start\n  Busy --> [*]\n")]
    [InlineData("classDiagram\n  class Animal {\n    +name\n  }\n  Animal <|-- Dog\n")]
    [InlineData("erDiagram\n  CUSTOMER ||--o{ ORDER : places\n  ORDER ||--|{ LINE : contains\n")]
    public void Every_default_type_leaves_no_unresolved_colour_and_draws_a_real_picture(string source)
    {
        var svg = MermaidRenderer.RenderSvg(source, new RenderOptions
        {
            Bg = Palette.Background,
            Fg = Palette.Foreground,
            Accent = Palette.Accent,
            Muted = Palette.Muted,
            Surface = Palette.Surface,
            Border = Palette.Border,
            Line = Palette.Line,
            Transparent = true,
            Gradient = false,
            Elevation = 0,
        });

        Assert.True(MermaidSvgColors.TryResolve(svg, Palette, out var resolved, out var problem), problem);
        Assert.DoesNotContain("color-mix(", resolved, StringComparison.Ordinal);

        var result = MermaidRenderService.FromSvg(svg, Palette);
        Assert.Equal(MermaidRenderOutcome.Rendered, result.Outcome);
        var picture = Assert.IsType<MermaidPicture>(result.Picture);
        Assert.True(picture.Size.Width > 0 && picture.Size.Height > 0);

        // Painted on white: something is drawn (not blank) and the drawing is not a black slab (an unresolved colour).
        var width = (int)Math.Ceiling(picture.Size.Width);
        var height = (int)Math.Ceiling(picture.Size.Height);
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawPicture(picture.Picture);
        }

        long drawn = 0;
        long dark = 0;
        foreach (var pixel in bitmap.Pixels)
        {
            if (pixel != SkiaSharp.SKColors.White)
            {
                drawn++;
            }

            if (pixel.Red < 40 && pixel.Green < 40 && pixel.Blue < 40)
            {
                dark++;
            }
        }

        Assert.True(drawn > 0, "the diagram drew nothing");
        Assert.True(dark * 2 < (long)width * height, "most of the diagram is black");
    }
}
