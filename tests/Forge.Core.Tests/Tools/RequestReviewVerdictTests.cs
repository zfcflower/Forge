using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>The reviewer's one contract: a verdict line at the end of its text, read back and capped for the tool result.</summary>
public sealed class RequestReviewVerdictTests
{
    [Theory]
    [InlineData("verdict: approve", ReviewVerdict.Approve)]
    [InlineData("verdict: request changes", ReviewVerdict.RequestChanges)]
    [InlineData("Verdict: APPROVED.", ReviewVerdict.Approve)]
    [InlineData("verdict: request_changes", ReviewVerdict.RequestChanges)]
    [InlineData("`verdict: approve`", ReviewVerdict.Approve)]
    [InlineData("**verdict: request changes**", ReviewVerdict.RequestChanges)]
    [InlineData("- verdict: approve", ReviewVerdict.Approve)]
    [InlineData("## verdict: request changes", ReviewVerdict.RequestChanges)]
    [InlineData("verdict: changes requested", ReviewVerdict.RequestChanges)]
    [InlineData("  verdict:   approve!  ", ReviewVerdict.Approve)]
    [InlineData("verdict: approve\r\n", ReviewVerdict.Approve)]
    public void A_known_verdict_line_is_read_through_its_markup_and_punctuation(string text, ReviewVerdict expected)
    {
        Assert.Equal(expected, RequestReviewVerdict.Parse(text));
    }

    [Fact]
    public void The_last_verdict_line_of_a_text_is_the_one_that_counts()
    {
        Assert.Equal(ReviewVerdict.RequestChanges, RequestReviewVerdict.Parse("verdict: approve\nsome prose\nverdict: request changes"));
        Assert.Equal(ReviewVerdict.Approve, RequestReviewVerdict.Parse("verdict: request changes\nsome prose\nverdict: approve"));
    }

    [Theory]
    [InlineData("verdict: partial")]
    [InlineData("no verdict anywhere in here\njust findings")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData("the verdict was fine, honestly")]
    public void Text_without_a_known_verdict_line_is_unstructured(string text)
    {
        Assert.Equal(ReviewVerdict.Unstructured, RequestReviewVerdict.Parse(text));
    }

    [Fact]
    public void A_verdict_line_far_above_the_end_is_still_found()
    {
        // No scan window: a reviewer that puts a findings summary after its verdict still said approve, and reading that
        // as unstructured would cost a whole extra review round.
        var text = "verdict: approve\n" + string.Join("\n", Enumerable.Range(1, 200).Select(number => $"prose {number}"));

        Assert.Equal(ReviewVerdict.Approve, RequestReviewVerdict.Parse(text));
    }

    [Theory]
    [InlineData(ReviewVerdict.Unstructured, "verdict: unstructured")]
    [InlineData(ReviewVerdict.Approve, "verdict: approve")]
    [InlineData(ReviewVerdict.RequestChanges, "verdict: request changes")]
    public void The_verdict_line_is_the_first_line_of_the_tool_result(ReviewVerdict verdict, string expected)
    {
        Assert.Equal(expected, RequestReviewVerdict.VerdictLine(verdict));
    }

    [Fact]
    public void An_unstructured_verdict_is_never_described_as_an_approval()
    {
        var nextStep = RequestReviewVerdict.NextStep(ReviewVerdict.Unstructured);

        Assert.Contains("not an approval", nextStep, StringComparison.Ordinal);
        Assert.Contains("verdict line", nextStep, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing blocking", nextStep, StringComparison.Ordinal);
        Assert.Contains("approved", RequestReviewVerdict.NextStep(ReviewVerdict.Approve), StringComparison.Ordinal);
    }

    [Fact]
    public void A_review_shorter_than_the_cap_is_returned_unchanged()
    {
        var text = "blocking: the cap is off by one\nverdict: request changes";

        Assert.Equal(text, RequestReviewVerdict.Clamp(text));
    }

    [Fact]
    public void A_review_over_the_cap_is_cut_on_a_line_boundary_and_says_both_lengths()
    {
        var line = new string('r', 29) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, 400));

        var clamped = RequestReviewVerdict.Clamp(text);

        Assert.Equal(12_000, text.Length);
        Assert.StartsWith(string.Concat(Enumerable.Repeat(line, 265)), clamped, StringComparison.Ordinal);
        Assert.EndsWith(
            "\n[the review was cut: it is 12000 characters long and only the first 7979 fit in this result]",
            clamped,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_never_leaves_half_a_surrogate_pair()
    {
        // No newline before the cap, so the hard cut lands inside the pair at the cap boundary.
        var text = new string('a', 7_999) + "\uD83D\uDE00" + new string('b', 100);

        var clamped = RequestReviewVerdict.Clamp(text);

        Assert.Equal(8_101, text.Length);
        Assert.Equal(
            new string('a', 7_999) + "\n[the review was cut: it is 8101 characters long and only the first 7999 fit in this result]",
            clamped);
        Assert.DoesNotContain("\uD83D", clamped);
    }
}
