using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// The terminal font fallback chain (wave W17a): the terminal's own family/size win when set, the code font otherwise, and
/// the built-in default when there are no preferences at all. The rule is a pure function, so it needs no visual.
/// </summary>
public sealed class TerminalFontFallbackTests
{
    [Fact]
    public void An_unset_override_follows_the_code_font()
    {
        var preferences = new AppPreferences
        {
            CodeFontFamily = "Cascadia Mono",
            CodeFontSize = 15,
            TerminalFontFamily = string.Empty,
            TerminalFontSize = 0,
        };

        Assert.Equal("Cascadia Mono", TerminalControl.FontFamilyOf(preferences));
        Assert.Equal(15, TerminalControl.FontSizeOf(preferences));
    }

    [Fact]
    public void A_set_override_wins_over_the_code_font()
    {
        var preferences = new AppPreferences
        {
            CodeFontFamily = "Cascadia Mono",
            CodeFontSize = 15,
            TerminalFontFamily = "Consolas",
            TerminalFontSize = 18,
        };

        Assert.Equal("Consolas", TerminalControl.FontFamilyOf(preferences));
        Assert.Equal(18, TerminalControl.FontSizeOf(preferences));
    }

    [Fact]
    public void No_preferences_reads_the_built_in_defaults()
    {
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, TerminalControl.FontFamilyOf(null));
        Assert.Equal(AppPreferences.DefaultCodeFontSize, TerminalControl.FontSizeOf(null));
    }

    [Theory]
    [InlineData(4, AppPreferences.MinTerminalFontSize)]
    [InlineData(64, AppPreferences.MaxTerminalFontSize)]
    public void The_size_override_is_clamped_by_the_preference(double written, double expected)
    {
        var preferences = new AppPreferences { TerminalFontSize = written };

        Assert.Equal(expected, preferences.TerminalFontSize);
        Assert.Equal(expected, TerminalControl.FontSizeOf(preferences));
    }

    [Fact]
    public void A_nan_size_reads_as_unset_instead_of_reaching_the_metadata()
    {
        var preferences = new AppPreferences { CodeFontSize = 13, TerminalFontSize = double.NaN };

        Assert.Equal(0, preferences.TerminalFontSize);
        Assert.Equal(13, TerminalControl.FontSizeOf(preferences));
    }
}