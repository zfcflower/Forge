using Forge.App.Git;
using Forge.App.Localization;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="GitReviewThreadItem"/>, the read-only row of the Git panel's "Review" group: where the thread is
/// anchored (current line, the original line of an outdated thread, the bare path, or "the whole pull request"), its
/// resolved/outdated status line, the reviewer's opening comment, the number of replies, and the verdict of the latest
/// reply that carries the agent's marker line together with that reply's text. A marker in the reviewer's own opening
/// comment never counts (the agent only ever replies), the latest marker wins, and long text is cut at the limit.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PrReviewThreadItemTests
{
    // ------------------------------------------------------------ where the thread is anchored

    [Fact]
    public void A_thread_on_a_line_shows_path_and_line()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", path: "src/a.cs", line: 12, comments: PrKit.Comment("x")));

        Assert.Equal("src/a.cs:12", item.LocationText);
    }

    [Fact]
    public void An_outdated_thread_falls_back_to_the_line_it_was_first_placed_on()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread("t1", outdated: true, path: "src/a.cs", line: null, originalLine: 4, comments: PrKit.Comment("x")));

        Assert.Equal("src/a.cs:4", item.LocationText);
        Assert.True(item.IsOutdated);
    }

    [Fact]
    public void A_thread_without_a_line_shows_the_bare_path()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", path: "docs/readme.md", line: null, comments: PrKit.Comment("x")));

        Assert.Equal("docs/readme.md", item.LocationText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_thread_without_a_file_says_it_is_on_the_whole_pull_request(string? path)
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", path: path, line: null, comments: PrKit.Comment("x")));

        Assert.Equal(Loc.Instance[GitStrings.ReviewNoFile], item.LocationText);
    }

    // ------------------------------------------------------------ the status line

    [Fact]
    public void An_unresolved_well_placed_thread_says_unresolved()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", comments: PrKit.Comment("x")));

        Assert.False(item.IsResolved);
        Assert.Equal(Loc.Instance[GitStrings.ReviewUnresolved], item.StatusText);
    }

    [Fact]
    public void A_resolved_thread_says_resolved()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", resolved: true, comments: PrKit.Comment("x")));

        Assert.True(item.IsResolved);
        Assert.Equal(Loc.Instance[GitStrings.ReviewResolved], item.StatusText);
    }

    [Theory]
    [InlineData(false, GitStrings.ReviewUnresolved)]
    [InlineData(true, GitStrings.ReviewResolved)]
    public void An_outdated_thread_says_so_next_to_its_resolution(bool resolved, string key)
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", resolved: resolved, outdated: true, comments: PrKit.Comment("x")));

        Assert.Equal(Loc.Instance[key] + " · " + Loc.Instance[GitStrings.ReviewOutdated], item.StatusText);
    }

    // ------------------------------------------------------------ the opening comment and the replies

    [Fact]
    public void The_opening_comment_is_shown_with_its_author()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread("t1", comments: PrKit.Comment("please rename this", "octocat")));

        Assert.Equal("octocat: please rename this", item.FirstCommentText);
        Assert.False(item.HasReplies);
        Assert.Equal(string.Empty, item.RepliesText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_authorless_comment_gets_a_placeholder_name(string? author)
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", comments: PrKit.Comment("hm", author)));

        Assert.Equal(Loc.Instance[GitStrings.ReviewUnknownAuthor] + ": hm", item.FirstCommentText);
    }

    [Fact]
    public void A_thread_without_comments_has_no_texts_and_no_verdict()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1"));

        Assert.Equal(string.Empty, item.FirstCommentText);
        Assert.Equal(string.Empty, item.RepliesText);
        Assert.False(item.HasReplies);
        Assert.Equal(string.Empty, item.VerdictText);
        Assert.False(item.HasVerdict);
        Assert.Equal(string.Empty, item.VerdictReplyText);
        Assert.False(item.HasVerdictReply);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void Every_comment_after_the_first_counts_as_a_reply(int comments, int replies)
    {
        var thread = PrKit.Thread("t1", comments: Many(comments));

        var item = new GitReviewThreadItem(thread);

        Assert.True(item.HasReplies);
        Assert.Equal(Loc.Instance.Format(GitStrings.ReviewReplies, replies), item.RepliesText);
    }

    // ------------------------------------------------------------ the verdict of the latest marker reply

    [Fact]
    public void An_apply_reply_shows_the_verdict_with_the_commit_and_the_reply_text()
    {
        var thread = PrKit.Thread(
            "t1",
            comments:
            [
                PrKit.Comment("this leaks the handle", "reviewer"),
                PrKit.Comment("a first reply with no marker", "agent"),
                PrKit.Comment(
                    PrReplyLedger.FormatReply(PrReplyVerdict.Apply, "abc1234", "Wrapped it in a using block."),
                    "agent",
                    "c3"),
            ]);

        var item = new GitReviewThreadItem(thread);

        Assert.True(item.HasVerdict);
        Assert.Equal(Loc.Instance.Format(GitStrings.ReviewVerdictApply, "abc1234"), item.VerdictText);
        Assert.True(item.HasVerdictReply);
        Assert.Equal("Wrapped it in a using block.", item.VerdictReplyText);
    }

    [Fact]
    public void A_push_back_reply_says_pushed_back()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("rename it", "reviewer"),
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "The name is public API."), "agent", "c2"),
                ]));

        Assert.Equal(Loc.Instance[GitStrings.ReviewVerdictPushBack], item.VerdictText);
        Assert.Equal("The name is public API.", item.VerdictReplyText);
    }

    [Fact]
    public void A_clarify_reply_says_it_asked_back()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("this is wrong", "reviewer"),
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "Which of the two call sites?"), "agent", "c2"),
                ]));

        Assert.Equal(Loc.Instance[GitStrings.ReviewVerdictClarify], item.VerdictText);
        Assert.Equal("Which of the two call sites?", item.VerdictReplyText);
    }

    [Fact]
    public void The_latest_marker_reply_wins()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("hmm", "reviewer"),
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "Which one?"), "agent", "c2"),
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Apply, "0123abc", "Both of them."), "agent", "c3"),
                ]));

        Assert.Equal(Loc.Instance.Format(GitStrings.ReviewVerdictApply, "0123abc"), item.VerdictText);
        Assert.Equal("Both of them.", item.VerdictReplyText);
    }

    [Fact]
    public void A_marker_in_the_reviewers_opening_comment_does_not_count()
    {
        // The agent only ever replies; a reviewer typing the marker line must not look like a verdict.
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Apply, "abc1234", "looks fine"), "reviewer"),
                    PrKit.Comment("thanks", "agent", "c2"),
                ]));

        Assert.False(item.HasVerdict);
        Assert.Equal(string.Empty, item.VerdictText);
    }

    [Fact]
    public void A_marker_without_a_commit_is_not_a_verdict()
    {
        // "apply" always cites a commit; a reply without one does not parse.
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("fix it", "reviewer"),
                    PrKit.Comment("**Verdict: apply**\n\nno commit cited", "agent", "c2"),
                ]));

        Assert.False(item.HasVerdict);
        Assert.Equal(string.Empty, item.VerdictText);
    }

    [Fact]
    public void A_marker_only_reply_has_a_verdict_but_no_text()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("fix it", "reviewer"),
                    PrKit.Comment(PrReplyLedger.FormatMarker(PrReplyVerdict.PushBack, null), "agent", "c2"),
                ]));

        Assert.True(item.HasVerdict);
        Assert.False(item.HasVerdictReply);
        Assert.Equal(string.Empty, item.VerdictReplyText);
    }

    [Fact]
    public void A_marker_after_a_blank_line_still_parses()
    {
        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("fix it", "reviewer"),
                    PrKit.Comment("\n\n" + PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "Out of scope."), "agent", "c2"),
                ]));

        Assert.Equal(Loc.Instance[GitStrings.ReviewVerdictPushBack], item.VerdictText);
        Assert.Equal("Out of scope.", item.VerdictReplyText);
    }

    // ------------------------------------------------------------ long text

    [Fact]
    public void A_long_comment_is_cut_at_the_limit_and_marked_with_an_ellipsis()
    {
        var longText = new string('a', GitReviewThreadItem.MaxCommentCharacters + 50);

        var item = new GitReviewThreadItem(PrKit.Thread("t1", comments: PrKit.Comment(longText, "reviewer")));

        var shown = item.FirstCommentText["reviewer: ".Length..];
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.Equal(GitReviewThreadItem.MaxCommentCharacters + 1, shown.Length);
        Assert.Equal(new string('a', GitReviewThreadItem.MaxCommentCharacters) + "…", shown);
    }

    [Fact]
    public void A_comment_at_the_limit_is_shown_whole()
    {
        var text = new string('a', GitReviewThreadItem.MaxCommentCharacters);

        var item = new GitReviewThreadItem(PrKit.Thread("t1", comments: PrKit.Comment(text, "reviewer")));

        Assert.Equal("reviewer: " + text, item.FirstCommentText);
        Assert.DoesNotContain("…", item.FirstCommentText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_verdict_reply_is_cut_the_same_way()
    {
        var longText = new string('b', GitReviewThreadItem.MaxCommentCharacters + 10);

        var item = new GitReviewThreadItem(
            PrKit.Thread(
                "t1",
                comments:
                [
                    PrKit.Comment("fix it", "reviewer"),
                    PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, longText), "agent", "c2"),
                ]));

        Assert.True(item.HasVerdict);
        Assert.EndsWith("…", item.VerdictReplyText, StringComparison.Ordinal);
        Assert.Equal(GitReviewThreadItem.MaxCommentCharacters + 1, item.VerdictReplyText.Length);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_from_the_shown_text()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("t1", comments: PrKit.Comment("  spaced out  ", " reviewer ")));

        Assert.Equal("reviewer: spaced out", item.FirstCommentText);
    }

    // ------------------------------------------------------------ identity and arguments

    [Fact]
    public void The_thread_id_and_its_flags_are_carried_over()
    {
        var item = new GitReviewThreadItem(PrKit.Thread("thread-42", resolved: true, outdated: false, comments: PrKit.Comment("x")));

        Assert.Equal("thread-42", item.Id);
        Assert.True(item.IsResolved);
        Assert.False(item.IsOutdated);
    }

    [Fact]
    public void A_missing_thread_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new GitReviewThreadItem(null!));

    // ------------------------------------------------------------ helpers

    private static PrReviewComment[] Many(int count) =>
        [.. Enumerable.Range(0, count).Select(index => PrKit.Comment("comment " + index, index == 0 ? "reviewer" : "agent", "c" + index))];
}
