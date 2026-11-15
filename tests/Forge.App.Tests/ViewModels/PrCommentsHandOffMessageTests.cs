using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the agent-facing hand-off message of one review thread: it is English model input (never localized), it carries
/// the thread id, the location the user saw, every comment with its author and time, and the closing instruction naming
/// <c>pr_comment_reply</c>/<c>pr_comment_resolve</c>. Bounds keep one thread from blowing up the turn's context.
/// </summary>
public sealed class PrCommentsHandOffMessageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 15, 10, 20, 0, TimeSpan.Zero);

    [Fact]
    public void Build_carries_thread_id_location_and_the_reply_instruction()
    {
        var thread = new PrReviewThread(
            "T1", false, false, "src/one.cs", 10, null, null, "RIGHT", null,
            [new PrReviewComment("C1", null, "alice", "please fix this", At, null)]);

        var message = PrCommentsHandOffMessage.Build(12, "https://example.test/acme/forge/pull/12", thread);

        Assert.Contains("thread_id: T1", message, StringComparison.Ordinal);
        Assert.Contains("src/one.cs:10", message, StringComparison.Ordinal);
        Assert.Contains("pr_comment_reply", message, StringComparison.Ordinal);
        Assert.Contains("pr_comment_resolve", message, StringComparison.Ordinal);
        Assert.Contains("state: unresolved", message, StringComparison.Ordinal);
        Assert.Contains("alice", message, StringComparison.Ordinal);
        Assert.Contains("pull request #12", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_reports_the_original_line_for_an_outdated_thread()
    {
        var thread = new PrReviewThread(
            "T-o", false, true, "src/o.cs", null, null, 42, "RIGHT", null,
            [new PrReviewComment("C1", null, "carol", "this moved", At, null)]);

        var message = PrCommentsHandOffMessage.Build(12, null, thread);

        Assert.Contains("src/o.cs:42 (original line)", message, StringComparison.Ordinal);
        Assert.Contains("state: outdated", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_names_a_missing_path_instead_of_inventing_one()
    {
        var thread = new PrReviewThread(
            "T-x", false, false, null, 10, null, null, "RIGHT", null,
            [new PrReviewComment("C1", null, null, string.Empty, At, null)]);

        var message = PrCommentsHandOffMessage.Build(7, null, thread);

        Assert.Contains("unknown (the platform reported no path)", message, StringComparison.Ordinal);
        Assert.Contains("unknown author", message, StringComparison.Ordinal);
        Assert.Contains("(empty)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_truncates_one_long_body_and_counts_the_dropped_comments()
    {
        var longBody = new string('x', PrCommentsHandOffMessage.MaxBodyCharacters + 100);
        var comments = new List<PrReviewComment>();
        for (var index = 0; index < PrCommentsHandOffMessage.MaxComments + 5; index++)
        {
            comments.Add(new PrReviewComment("C" + index, null, "alice", index == 0 ? longBody : "short", At, null));
        }

        var thread = new PrReviewThread(
            "T-big", false, false, "src/big.cs", 1, null, null, "RIGHT", null, comments);

        var message = PrCommentsHandOffMessage.Build(12, null, thread);

        // One body is cut with a marker; only the first MaxComments are written out, the rest are counted.
        Assert.Contains("…(cut)", message, StringComparison.Ordinal);
        Assert.Contains("more comment(s) were left out", message, StringComparison.Ordinal);
        Assert.True(message.Length <= PrCommentsHandOffMessage.MaxMessageCharacters, "the hand-off must stay bounded");
    }

    [Fact]
    public void Build_caps_the_whole_message_under_the_limit()
    {
        var comments = new List<PrReviewComment>();
        for (var index = 0; index < 30; index++)
        {
            comments.Add(new PrReviewComment("C" + index, null, "alice", new string('y', 3_000), At, null));
        }

        var thread = new PrReviewThread(
            "T-huge", false, false, "src/huge.cs", 1, null, null, "RIGHT", null, comments);

        var message = PrCommentsHandOffMessage.Build(12, null, thread);

        Assert.True(message.Length <= PrCommentsHandOffMessage.MaxMessageCharacters, "the hand-off must stay bounded");
        Assert.Contains("pr_comment_reply", message, StringComparison.Ordinal);
    }
}
