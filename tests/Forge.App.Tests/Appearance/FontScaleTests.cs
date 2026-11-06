using Forge.App.Appearance;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// Pins the pure size scale of TASK-fonts: the tokens <c>Tokens.axaml</c> ships are the values at 13, the derivation is
/// linear with <c>MidpointRounding.AwayFromZero</c>, and a larger interface size never shrinks a token.
/// </summary>
public sealed class FontScaleTests
{
    [Fact]
    public void The_reference_size_reproduces_the_tokens_axaml_defaults()
    {
        var tokens = FontScale.Compute(FontScale.ReferenceSize);

        Assert.Equal(new FontSizeTokens(11, 12, 13, 15, 18, 22), tokens);
        Assert.Equal(FontScale.Default, tokens);
    }

    [Fact]
    public void The_smallest_size_keeps_every_token_whole_and_positive()
    {
        // 11/12/13/15/18/22 * 9/13, rounded: 8. 8. 9. 10. 12. 15.
        var tokens = FontScale.Compute(9);

        Assert.Equal(new FontSizeTokens(8, 8, 9, 10, 12, 15), tokens);
        Assert.Equal(9, tokens.Base);
    }

    [Fact]
    public void The_largest_size_scales_every_token_up()
    {
        // 11/12/13/15/18/22 * 28/13, rounded: 24. 26. 28. 32. 39. 47.
        var tokens = FontScale.Compute(28);

        Assert.Equal(new FontSizeTokens(24, 26, 28, 32, 39, 47), tokens);
        Assert.Equal(28, tokens.Base);
    }

    [Fact]
    public void A_half_point_rounds_away_from_zero_not_to_even()
    {
        // 12.5 and 14.5 are exact .5 midpoints; round-to-even would give 12 and 14.
        Assert.Equal(13, FontScale.Compute(12.5).Base);
        Assert.Equal(15, FontScale.Compute(14.5).Base);
    }

    [Fact]
    public void Every_token_is_monotonic_over_the_size_range()
    {
        var previous = FontScale.Compute(9);

        for (var size = 9.5; size <= 28; size += 0.5)
        {
            var current = FontScale.Compute(size);

            Assert.True(current.Xs >= previous.Xs, $"FontSizeXs shrank at {size}");
            Assert.True(current.Sm >= previous.Sm, $"FontSizeSm shrank at {size}");
            Assert.True(current.Base >= previous.Base, $"FontSizeBase shrank at {size}");
            Assert.True(current.Md >= previous.Md, $"FontSizeMd shrank at {size}");
            Assert.True(current.Lg >= previous.Lg, $"FontSizeLg shrank at {size}");
            Assert.True(current.Xl >= previous.Xl, $"FontSizeXl shrank at {size}");
            previous = current;
        }
    }
}