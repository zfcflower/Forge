using Forge.App.Preferences;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// Pins the palette's query grammar and its five-step ranking: only the first character of a typed line switches source,
/// the prefix is stripped exactly once, an empty query matches everything, and every rank (exact, prefix, segment start,
/// substring, subsequence) beats the next one. The secondary text of a pair may never outrank the same hit in the
/// primary, and the configured limit is clamped to the range the settings page also allows.
/// </summary>
public sealed class PaletteQueryTests
{
    [Theory]
    [InlineData(null, null, "")]
    [InlineData("", null, "")]
    [InlineData(">", (int)PaletteSource.Commands, "")]
    [InlineData(">git", (int)PaletteSource.Commands, "git")]
    [InlineData("#abc", (int)PaletteSource.Content, "abc")]
    [InlineData("#", (int)PaletteSource.Content, "")]
    [InlineData(":x", (int)PaletteSource.Sessions, "x")]
    [InlineData("@@", (int)PaletteSource.Symbols, "@")]
    [InlineData("a>b", null, "a>b")]
    [InlineData(";x", null, ";x")]
    [InlineData(" ", null, " ")]
    public void Parse_strips_a_leading_source_prefix_and_nothing_else(string? raw, int? source, string text)
    {
        var (parsed, rest) = PaletteQuery.Parse(raw);

        PaletteSource? expected = source is null ? null : (PaletteSource)source.Value;
        Assert.Equal(expected, parsed);
        Assert.Equal(text, rest);
    }

    [Theory]
    [InlineData((int)PaletteSource.Files, '\0')]
    [InlineData((int)PaletteSource.Content, '#')]
    [InlineData((int)PaletteSource.Sessions, ':')]
    [InlineData((int)PaletteSource.Commands, '>')]
    [InlineData((int)PaletteSource.Symbols, '@')]
    public void PrefixOf_names_the_switching_character(int source, char prefix) =>
        Assert.Equal(prefix, PaletteQuery.PrefixOf((PaletteSource)source));

    [Fact]
    public void Every_prefixed_source_round_trips_through_its_own_prefix()
    {
        foreach (var source in new[] { PaletteSource.Content, PaletteSource.Sessions, PaletteSource.Commands, PaletteSource.Symbols })
        {
            var (parsed, text) = PaletteQuery.Parse(PaletteQuery.PrefixOf(source) + "session");

            Assert.Equal(source, parsed);
            Assert.Equal("session", text);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything")]
    public void An_empty_query_matches_every_text_with_rank_zero(string? text) =>
        Assert.Equal(0, PaletteQuery.Score(text, string.Empty));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_non_empty_query_misses_null_or_empty_text(string? text) =>
        Assert.Equal(-1, PaletteQuery.Score(text, "a"));

    [Fact]
    public void An_exact_match_ignores_case()
    {
        Assert.Equal(0, PaletteQuery.Score("ReadMe.md", "readme.md"));
        Assert.Equal(0, PaletteQuery.Score("readme.md", "readme.md"));
    }

    [Fact]
    public void A_prefix_match_outranks_everything_after_the_first_character() =>
        Assert.Equal(1, PaletteQuery.Score("readme.md", "REA"));

    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('.')]
    [InlineData(':')]
    [InlineData('-')]
    [InlineData(' ')]
    [InlineData('_')]
    [InlineData('(')]
    public void A_match_right_after_a_separator_ranks_as_a_segment_start(char separator)
    {
        var text = "x" + separator + "models";

        Assert.Equal(2, PaletteQuery.Score(text, "mod"));
    }

    [Fact]
    public void A_segment_start_beats_a_substring_in_the_middle_of_a_word()
    {
        Assert.Equal(2, PaletteQuery.Score("src/Models.cs", "mod"));
        Assert.Equal(3, PaletteQuery.Score("xmode", "mod"));
    }

    [Fact]
    public void Query_characters_in_order_anywhere_rank_last()
    {
        Assert.Equal(4, PaletteQuery.Score("abcdef", "ad"));
        Assert.Equal(4, PaletteQuery.Score("ABCDEF", "ad"));
    }

    [Fact]
    public void Text_without_the_query_in_order_is_no_match()
    {
        Assert.Equal(-1, PaletteQuery.Score("abcdef", "da"));
        Assert.Equal(-1, PaletteQuery.Score("abc", "xyz"));
    }

    [Fact]
    public void A_pair_misses_when_neither_text_matches() =>
        Assert.Equal(-1, PaletteQuery.ScorePair("alpha", "beta", "zeta"));

    [Fact]
    public void A_pair_returns_the_primary_rank_when_the_secondary_text_misses()
    {
        Assert.Equal(1, PaletteQuery.ScorePair("modular", null, "mod"));
        Assert.Equal(3, PaletteQuery.ScorePair("xxmod", string.Empty, "mod"));
    }

    [Fact]
    public void The_same_hit_in_the_secondary_text_ranks_one_step_worse()
    {
        Assert.Equal(1, PaletteQuery.ScorePair("modular", "zzz", "mod"));
        Assert.Equal(2, PaletteQuery.ScorePair("zzz", "modular", "mod"));
        Assert.Equal(1, PaletteQuery.ScorePair("zzz", "mod", "mod"));
    }

    [Fact]
    public void A_secondary_hit_never_beats_a_worse_primary_hit_by_more_than_one()
    {
        // The primary text only holds the query as a substring (rank 3), the secondary holds it exactly (rank 0):
        // the pair still ranks 1, so the secondary can improve the pair but not jump ahead of a primary exact hit.
        Assert.Equal(1, PaletteQuery.ScorePair("xxmod", "mod", "mod"));
        Assert.Equal(0, PaletteQuery.ScorePair("mod", "modular", "mod"));
    }

    [Fact]
    public void A_pair_with_an_empty_query_matches_with_rank_zero()
    {
        Assert.Equal(0, PaletteQuery.ScorePair(null, null, string.Empty));
        Assert.Equal(0, PaletteQuery.ScorePair("x", "y", string.Empty));
    }

    [Theory]
    [InlineData(int.MinValue, AppPreferences.MinPaletteResultLimit)]
    [InlineData(0, AppPreferences.MinPaletteResultLimit)]
    [InlineData(AppPreferences.MinPaletteResultLimit, AppPreferences.MinPaletteResultLimit)]
    [InlineData(AppPreferences.MinPaletteResultLimit + 1, AppPreferences.MinPaletteResultLimit + 1)]
    [InlineData(AppPreferences.MaxPaletteResultLimit, AppPreferences.MaxPaletteResultLimit)]
    [InlineData(AppPreferences.MaxPaletteResultLimit - 1, AppPreferences.MaxPaletteResultLimit - 1)]
    [InlineData(AppPreferences.MaxPaletteResultLimit + 1, AppPreferences.MaxPaletteResultLimit)]
    [InlineData(int.MaxValue, AppPreferences.MaxPaletteResultLimit)]
    public void ClampLimit_holds_the_limit_inside_the_settings_range(int limit, int expected) =>
        Assert.Equal(expected, PaletteQuery.ClampLimit(limit));
}
