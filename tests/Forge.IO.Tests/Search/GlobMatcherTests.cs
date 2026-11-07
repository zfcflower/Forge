using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The path glob: <c>*</c> inside one segment, <c>?</c>, <c>**</c> across segments, character classes with negation and
/// ranges, backslash escapes, platform case folding, a path deep enough to leave the stack-indexed segment buffer, and
/// the difference between "match the whole path" and the git pathspec rule for a pattern that names one segment.
/// </summary>
public class GlobMatcherTests
{
    [Fact]
    public void A_star_matches_inside_one_segment_and_never_across_a_separator()
    {
        var matcher = GlobMatcher.Compile("src/*.cs");

        Assert.True(matcher.IsMatch("src/a.cs"));
        Assert.True(matcher.IsMatch("src/a.long.name.cs"));
        Assert.False(matcher.IsMatch("src/nested/a.cs"));
        Assert.False(matcher.IsMatch("other/a.cs"));
        Assert.False(matcher.IsMatch("src/a.cs/deeper"));
    }

    [Fact]
    public void A_double_star_segment_matches_any_number_of_segments()
    {
        var anyCs = GlobMatcher.Compile("**/*.cs");
        var inside = GlobMatcher.Compile("a/**");

        Assert.True(anyCs.IsMatch("a.cs"));
        Assert.True(anyCs.IsMatch("src/a.cs"));
        Assert.True(anyCs.IsMatch("src/nested/deep/a.cs"));
        Assert.False(anyCs.IsMatch("src/a.txt"));
        Assert.True(inside.IsMatch("a/b"));
        Assert.True(inside.IsMatch("a/b/c/d"));
        Assert.False(inside.IsMatch("a"));
    }

    [Fact]
    public void A_question_mark_matches_exactly_one_character()
    {
        var matcher = GlobMatcher.Compile("a?c");

        Assert.True(matcher.IsMatch("abc"));
        Assert.True(matcher.IsPathMatch("dir/axc"));
        Assert.False(matcher.IsMatch("ac"));
        Assert.False(matcher.IsMatch("abbc"));
        Assert.False(matcher.IsMatch("dir/abc"));
    }

    [Fact]
    public void A_character_class_matches_one_listed_character_and_a_negated_class_the_others()
    {
        var listed = GlobMatcher.Compile("file[abc].txt");
        var negated = GlobMatcher.Compile("file[!abc].txt");
        var range = GlobMatcher.Compile("file[0-2].txt");

        Assert.True(listed.IsMatch("fileb.txt"));
        Assert.False(listed.IsMatch("filed.txt"));
        Assert.True(negated.IsMatch("filed.txt"));
        Assert.False(negated.IsMatch("filea.txt"));
        Assert.True(range.IsMatch("file2.txt"));
        Assert.False(range.IsMatch("file3.txt"));
    }

    [Fact]
    public void A_backslash_quotes_the_next_wildcard()
    {
        Assert.True(GlobMatcher.Compile(@"a\*b").IsMatch("a*b"));
        Assert.False(GlobMatcher.Compile(@"a\*b").IsMatch("axb"));
        Assert.True(GlobMatcher.Compile(@"a\?b").IsMatch("a?b"));
        Assert.False(GlobMatcher.Compile(@"a\?b").IsMatch("axb"));
        Assert.True(GlobMatcher.Compile(@"\[x\]").IsMatch("[x]"));
        Assert.False(GlobMatcher.Compile(@"\[x\]").IsMatch("x"));
    }

    [Fact]
    public void Case_folding_follows_the_platform()
    {
        var expected = WorkspacePath.Comparer == StringComparer.OrdinalIgnoreCase;
        var matcher = GlobMatcher.Compile("*.CS");
        var range = GlobMatcher.Compile("name[AB].txt");

        Assert.Equal(expected, matcher.IsPathMatch("a.cs"));
        Assert.Equal(expected, range.IsPathMatch("namea.txt"));
    }

    [Fact]
    public void A_path_of_eighty_levels_still_matches_the_double_star_pattern()
    {
        var deep = string.Join('/', Enumerable.Repeat("level", 80)) + "/file.cs";

        Assert.True(GlobMatcher.Compile("**/*.cs").IsMatch(deep));
        Assert.True(GlobMatcher.Compile("**").IsMatch(deep));
    }

    [Fact]
    public void A_pattern_without_a_separator_names_one_segment_while_a_pattern_with_one_is_anchored()
    {
        var segment = GlobMatcher.Compile("*.cs");
        var anchored = GlobMatcher.Compile("src/*.cs");
        var everything = GlobMatcher.Compile("**");

        Assert.True(segment.IsSegmentPattern);
        Assert.True(segment.IsPathMatch("a/b/name.cs"));
        Assert.False(segment.IsMatch("a/b/name.cs"));
        Assert.False(anchored.IsSegmentPattern);
        Assert.False(everything.IsSegmentPattern);
    }

    [Fact]
    public void A_trailing_separator_is_dropped_and_empty_segments_are_ignored()
    {
        var matcher = GlobMatcher.Compile("src//nested/");

        Assert.True(matcher.IsMatch("src/nested"));
        Assert.Equal("src//nested/", matcher.Pattern);
    }

    [Fact]
    public void A_literal_pattern_matches_only_the_same_path()
    {
        var matcher = GlobMatcher.Compile("src/nested/file.cs");
        var foldsCase = WorkspacePath.Comparer == StringComparer.OrdinalIgnoreCase;

        Assert.True(matcher.IsMatch("src/nested/file.cs"));
        Assert.Equal(foldsCase, matcher.IsMatch("src/nested/File.cs"));
        Assert.False(matcher.IsMatch("src/file.cs"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("//")]
    public void A_pattern_that_names_no_segment_is_rejected(string pattern) =>
        Assert.Throws<ArgumentException>(() => GlobMatcher.Compile(pattern));
}
