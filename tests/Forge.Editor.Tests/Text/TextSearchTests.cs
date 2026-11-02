using Forge.Editor.Text;

namespace Forge.Editor.Tests.Text;

public class TextSearchTests
{
    [Fact]
    public void Literal_search_is_case_insensitive_unless_asked_otherwise()
    {
        var snapshot = TextSnapshot.FromText("Foo foo FOO");

        Assert.Equal(3, TextSearch.Count(snapshot, "foo"));
        Assert.Equal(1, TextSearch.Count(snapshot, "foo", SearchOptions.CaseSensitive));
        Assert.Equal(new[] { 0, 4, 8 }, Offsets(TextSearch.FindAll(snapshot, "fOO")));
    }

    [Fact]
    public void Literal_search_reports_overlapping_occurrences()
    {
        var snapshot = TextSnapshot.FromText("aaaa");

        Assert.Equal(new[] { 0, 1, 2 }, Offsets(TextSearch.FindAll(snapshot, "aa", SearchOptions.CaseSensitive)));
    }

    [Fact]
    public void Whole_word_rejects_matches_glued_to_word_characters()
    {
        var snapshot = TextSnapshot.FromText("cat concat cat_x cat.");

        var matches = TextSearch.FindAll(snapshot, "cat", SearchOptions.WholeWord).ToList();

        Assert.Equal(new[] { 0, 17 }, matches.Select(m => m.Offset));
        Assert.All(matches, m => Assert.Equal(3, m.Length));
        Assert.Equal(20, matches[^1].End);
    }

    [Fact]
    public void Regex_search_returns_offsets_and_lengths()
    {
        var snapshot = TextSnapshot.FromText("a1 b22 c333");

        var matches = TextSearch.FindAll(snapshot, @"\d+", SearchOptions.Regex).ToList();

        Assert.Equal(new[] { 1, 4, 8 }, matches.Select(m => m.Offset));
        Assert.Equal(new[] { 1, 2, 3 }, matches.Select(m => m.Length));
    }

    [Fact]
    public void Regex_search_is_case_insensitive_by_default()
    {
        var snapshot = TextSnapshot.FromText("HELLO hello");

        Assert.Equal(2, TextSearch.Count(snapshot, "hello", SearchOptions.Regex));
        Assert.Equal(1, TextSearch.Count(snapshot, "hello", SearchOptions.Regex | SearchOptions.CaseSensitive));
    }

    [Fact]
    public void Non_multiline_regex_anchors_match_per_line()
    {
        var snapshot = TextSnapshot.FromText("a\nb\nbb");

        Assert.Equal(new[] { 2, 4 }, Offsets(TextSearch.FindAll(snapshot, "^b", SearchOptions.Regex | SearchOptions.CaseSensitive)));
    }

    [Fact]
    public void Dollar_does_not_see_the_carriage_return_of_a_crlf_break()
    {
        var snapshot = TextSnapshot.FromText("abc\r\ndef");

        var matches = TextSearch.FindAll(snapshot, "c$", SearchOptions.Regex).ToList();

        Assert.Equal(new[] { 2 }, matches.Select(m => m.Offset));
    }

    [Fact]
    public void Zero_length_regex_matches_are_skipped()
    {
        var snapshot = TextSnapshot.FromText("abxxc");

        var matches = TextSearch.FindAll(snapshot, "x*", SearchOptions.Regex | SearchOptions.CaseSensitive).ToList();

        Assert.Equal(new SearchMatch(2, 2), Assert.Single(matches));
    }

    [Fact]
    public void Multiline_regex_lets_the_dot_cross_line_breaks()
    {
        var snapshot = TextSnapshot.FromText("a\nb");

        var multiline = TextSearch.FindAll(snapshot, "a.b", SearchOptions.Regex | SearchOptions.Multiline).ToList();

        Assert.Equal(new SearchMatch(0, 3), Assert.Single(multiline));
        Assert.Equal(0, TextSearch.Count(snapshot, "a.b", SearchOptions.Regex));
    }

    [Fact]
    public void Multiline_regex_sees_the_whole_text_for_start_offsets()
    {
        var snapshot = TextSnapshot.FromText("ab\ncd\nab");

        Assert.Equal(new[] { 6 }, Offsets(TextSearch.FindAll(snapshot, "ab", SearchOptions.Regex | SearchOptions.Multiline | SearchOptions.CaseSensitive, 3)));
    }

    [Fact]
    public void Start_offset_excludes_matches_that_begin_before_it()
    {
        var snapshot = TextSnapshot.FromText("a a a a");

        Assert.Equal(new[] { 4, 6 }, Offsets(TextSearch.FindAll(snapshot, "a", SearchOptions.CaseSensitive, 3)));
        Assert.Equal(new[] { 3 }, Offsets(TextSearch.FindAll(TextSnapshot.FromText("aa aa"), "a+", SearchOptions.Regex | SearchOptions.CaseSensitive, 2)));
    }

    [Fact]
    public void Regex_start_offset_finds_a_match_beginning_at_it_inside_an_earlier_match()
    {
        var snapshot = TextSnapshot.FromText("aaa");

        Assert.Equal(new[] { 1 }, Offsets(TextSearch.FindAll(snapshot, "aa", SearchOptions.Regex | SearchOptions.CaseSensitive, 1)));
        Assert.Equal(new[] { 1 }, Offsets(TextSearch.FindAll(snapshot, "aa", SearchOptions.CaseSensitive, 1)));
    }

    [Fact]
    public void Try_find_next_returns_the_first_match_at_or_after_the_start()
    {
        var snapshot = TextSnapshot.FromText("xx needle yy needle");

        Assert.True(TextSearch.TryFindNext(snapshot, "needle", SearchOptions.None, 0, out var first));
        Assert.Equal(new SearchMatch(3, 6), first);
        Assert.True(TextSearch.TryFindNext(snapshot, "needle", SearchOptions.None, 4, out var second));
        Assert.Equal(new SearchMatch(13, 6), second);
        Assert.False(TextSearch.TryFindNext(snapshot, "needle", SearchOptions.None, 14, out var none));
        Assert.Equal(default, none);
    }

    [Fact]
    public void Empty_document_and_out_of_range_start_have_no_matches()
    {
        var empty = TextSnapshot.FromText(string.Empty);

        Assert.Equal(0, TextSearch.Count(empty, "a"));
        Assert.Equal(0, TextSearch.Count(empty, "a", SearchOptions.Regex));
        Assert.Empty(TextSearch.FindAll(TextSnapshot.FromText("abc"), "a", SearchOptions.None, 99));
    }

    [Fact]
    public void Empty_pattern_is_rejected_eagerly()
    {
        var snapshot = TextSnapshot.FromText("abc");

        Assert.Throws<ArgumentException>(() => TextSearch.FindAll(snapshot, string.Empty));
    }

    [Fact]
    public void Invalid_regex_fails_when_the_matches_are_enumerated()
    {
        var snapshot = TextSnapshot.FromText("abc");

        Assert.ThrowsAny<ArgumentException>(() => TextSearch.Count(snapshot, "(", SearchOptions.Regex));
    }

    [Fact]
    public void Unicode_case_folding_matches_non_ascii_letters()
    {
        var snapshot = TextSnapshot.FromText("ÉCOLE école");

        Assert.Equal(2, TextSearch.Count(snapshot, "école"));
        Assert.Equal(1, TextSearch.Count(snapshot, "école", SearchOptions.CaseSensitive));
    }

    [Fact]
    public void Literal_matches_straddling_the_internal_slice_boundary_are_found_once()
    {
        const string Needle = "needle";
        var text = new char[200_000];
        Array.Fill(text, 'x');
        foreach (var at in new[] { 65529, 65535, 65541, 131060, 131101 })
        {
            Needle.CopyTo(0, text, at, Needle.Length);
        }

        "NEEDLE".CopyTo(0, text, 150000, 6);
        var snapshot = TextSnapshot.FromText(new string(text));

        var expected = NaiveOffsets(new string(text), Needle, StringComparison.OrdinalIgnoreCase);
        var actual = Offsets(TextSearch.FindAll(snapshot, Needle));

        Assert.Equal(expected, actual);
        Assert.Contains(65535, actual);
        Assert.Contains(131060, actual);
    }

    [Fact]
    public void Literal_search_from_inside_a_later_slice_matches_the_naive_scan()
    {
        var random = new Random(3);
        var text = new string(Enumerable.Range(0, 150_000).Select(_ => "ab"[random.Next(2)]).ToArray());
        var snapshot = TextSnapshot.FromText(text);

        foreach (var start in new[] { 0, 65000, 65531, 100_001 })
        {
            var expected = NaiveOffsets(text, "abba", StringComparison.Ordinal).Where(o => o >= start).ToArray();
            Assert.Equal(expected, Offsets(TextSearch.FindAll(snapshot, "abba", SearchOptions.CaseSensitive, start)));
        }
    }

    [Fact]
    public void Whole_word_applies_across_slice_boundaries()
    {
        var text = new string('x', 65533) + " cat " + new string('y', 70000) + "cat";
        var snapshot = TextSnapshot.FromText(text);

        Assert.Equal(new[] { 65534 }, Offsets(TextSearch.FindAll(snapshot, "cat", SearchOptions.WholeWord)));
    }

    [Fact]
    public void Multiline_regex_honours_whole_word()
    {
        var snapshot = TextSnapshot.FromText("foo food");

        var matches = TextSearch.FindAll(snapshot, "foo", SearchOptions.Regex | SearchOptions.Multiline | SearchOptions.WholeWord);

        Assert.Equal(new[] { 0 }, Offsets(matches));
    }

    private static int[] Offsets(IEnumerable<SearchMatch> matches) => matches.Select(m => m.Offset).ToArray();

    private static int[] NaiveOffsets(string text, string pattern, StringComparison comparison)
    {
        var result = new List<int>();
        for (var i = 0; i + pattern.Length <= text.Length; i++)
        {
            if (text.AsSpan(i, pattern.Length).Equals(pattern.AsSpan(), comparison))
            {
                result.Add(i);
            }
        }

        return result.ToArray();
    }
}
