using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class PrCommentResolveSmokeTests
{
    private const string Sha = "0123abc";

    private static string ReplyJson(string threadId, string verdict, string body) =>
        $$"""{"thread_id":"{{threadId}}","verdict":"{{verdict}}","body":"{{body}}"}""";

    private static string ResolveJson(string threadId) => $$"""{"thread_id":"{{threadId}}"}""";

    private static PrReviewThread ThreadWithReply(string id, PrReplyVerdict verdict, string author = "octo", params PrReviewComment[] after)
    {
        var commit = verdict == PrReplyVerdict.Apply ? Sha : null;
        PrReviewComment[] comments =
        [
            PrCommentHarness.Comment("c0-" + id, "reviewer", "please fix"),
            PrCommentHarness.Comment("c1-" + id, author, PrReplyLedger.FormatReply(verdict, commit, "answer"), 1),
            .. after,
        ];
        return PrCommentHarness.Thread(id, comments: comments);
    }

    private static (PrCommentTools Tools, FakePrPlatform Platform, PrReplyLedger Ledger) Create(
        string? user = "octo",
        params PrReviewThread[] threads)
    {
        var platform = new FakePrPlatform();
        platform.Threads.AddRange(threads);
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(
            new GitCli(), platform, new GitToolSettings(), ledger, null, user is null ? null : new PrCommentHarness.FixedUser(user).Resolver);
        return (tools, platform, ledger);
    }

    [Fact]
    public async Task A_thread_without_any_agent_reply_cannot_be_resolved()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("has no verdict reply from you", result.Content);
        Assert.Contains("pr_comment_reply", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_clarify_reply_never_unlocks_resolving()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"));

        var reply = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "clarify", "Which one?"));
        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(reply.IsError, reply.Content);
        Assert.True(result.IsError);
        Assert.Contains("is a clarify", result.Content);
        Assert.Empty(platform.Resolves);
        Assert.False(platform.Threads[0].IsResolved);
    }

    [Fact]
    public async Task A_push_back_reply_through_the_tool_unlocks_resolving_even_without_a_user_lookup()
    {
        using var temp = new TempDirectory();
        var (tools, platform, ledger) = Create(user: null, PrCommentHarness.Thread("PRRT_1"));

        await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "Out of scope."));
        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Resolved thread PRRT_1 of pull request #5 (your verdict: push_back).", result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
        Assert.True(platform.Threads[0].IsResolved);
        Assert.Equal("octo", ledger.CurrentUser);
    }

    [Fact]
    public async Task An_apply_marker_by_the_current_user_unlocks_resolving_and_names_the_commit()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.Apply));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"(your verdict: apply ({Sha}))", result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task The_evidence_survives_a_restart_because_it_is_rebuilt_from_the_platform()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));

        // First "process": posts the reply.
        var first = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);
        var posted = await PrCommentHarness.InvokeAsync(first.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "Out of scope."));
        Assert.False(posted.IsError, posted.Content);

        // Second "process": empty ledger, but it can ask who the current user is.
        var restarted = new PrCommentTools(
            new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null, new PrCommentHarness.FixedUser("octo").Resolver);
        var result = await PrCommentHarness.InvokeAsync(restarted.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task After_a_restart_without_a_known_user_the_gate_stays_closed()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create(user: null, ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("has no verdict reply from you", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_failing_user_lookup_falls_back_to_what_the_ledger_learned()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(
            new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null,
            (_, _) => throw new InvalidOperationException("gh exploded"));

        await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "no"));
        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task A_verdict_marker_written_by_somebody_else_does_not_unlock_resolving()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack, author: "mallory"));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("has no verdict reply from you", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_reply_that_was_deleted_from_the_platform_proves_nothing()
    {
        using var temp = new TempDirectory();
        var (tools, platform, ledger) = Create("octo", PrCommentHarness.Thread("PRRT_1"));
        ledger.Record("PRRT_1", PrReplyVerdict.PushBack, null, "PRRC_gone");

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task The_latest_verdict_decides_clarify_after_apply_blocks_and_apply_after_clarify_allows()
    {
        using var temp = new TempDirectory();
        var applyThenClarify = PrCommentHarness.Thread(
            "PRRT_1",
            comments:
            [
                PrCommentHarness.Comment("a0", "reviewer", "fix"),
                PrCommentHarness.Comment("a1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Apply, Sha, "done"), 1),
                PrCommentHarness.Comment("a2", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "wait, which?"), 2),
            ]);
        var clarifyThenApply = PrCommentHarness.Thread(
            "PRRT_2",
            comments:
            [
                PrCommentHarness.Comment("b0", "reviewer", "fix"),
                PrCommentHarness.Comment("b1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "which?"), 1),
                PrCommentHarness.Comment("b2", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Apply, Sha, "done"), 2),
            ]);
        var (tools, platform, _) = Create("octo", applyThenClarify, clarifyThenApply);

        var blocked = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));
        var allowed = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_2"));

        Assert.True(blocked.IsError);
        Assert.Contains("is a clarify", blocked.Content);
        Assert.False(allowed.IsError, allowed.Content);
        Assert.Equal("PRRT_2", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task A_new_comment_from_the_reviewer_after_the_verdict_blocks_resolving()
    {
        using var temp = new TempDirectory();
        var thread = ThreadWithReply(
            "PRRT_1",
            PrReplyVerdict.PushBack,
            after: [PrCommentHarness.Comment("c2", "reviewer", "I disagree, please change it", 2)]);
        var (tools, platform, _) = Create("octo", thread);

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("1 comment(s) after your push_back reply", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_reviewer_comment_that_arrives_between_reply_and_resolve_is_noticed_because_the_thread_is_re_read()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"));
        await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "no"));
        var readsBefore = platform.ThreadReads;

        platform.Threads[0] = platform.Threads[0] with
        {
            Comments = [.. platform.Threads[0].Comments, PrCommentHarness.Comment("late", "reviewer", "but why?", 30)],
        };
        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(platform.ThreadReads > readsBefore);
        Assert.True(result.IsError);
        Assert.Contains("after your push_back reply", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_follow_up_comment_with_the_same_login_also_blocks_resolving()
    {
        // The reviewer and the agent may share one gh account, so "same author as the verdict reply" proves nothing; any
        // comment written after the verdict has to be read and answered first.
        using var temp = new TempDirectory();
        var thread = ThreadWithReply(
            "PRRT_1",
            PrReplyVerdict.PushBack,
            after: [PrCommentHarness.Comment("c2", "Octo", "One more thing: also fix the log line.", 2)]);
        var (tools, platform, _) = Create("octo", thread);

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("1 comment(s) after your push_back reply", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_thread_whose_verdict_reply_is_the_last_comment_still_resolves()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task A_thread_that_is_already_resolved_is_left_alone()
    {
        using var temp = new TempDirectory();
        var thread = ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack) with { IsResolved = true, ResolvedBy = "reviewer" };
        var (tools, platform, _) = Create("octo", thread);

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Thread PRRT_1 is already resolved (by reviewer). Nothing to do.", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_thread_somebody_else_resolved_meanwhile_is_not_resolved_again()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"));
        await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "no"));
        platform.Threads[0] = platform.Threads[0] with { IsResolved = true, ResolvedBy = "reviewer" };

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("already resolved", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_verdict_on_one_thread_does_not_unlock_another()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"), PrCommentHarness.Thread("PRRT_2"));
        await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, ReplyJson("PRRT_1", "push_back", "no"));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_2"));

        Assert.True(result.IsError);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task An_unknown_thread_id_is_a_tool_error()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", PrCommentHarness.Thread("PRRT_1"));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_404"));

        Assert.True(result.IsError);
        Assert.Contains("is not a review thread of pull request #5", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_platform_failure_while_resolving_is_a_tool_error()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));
        platform.ResolveFailure = PrResult<PrThreadResolution>.Fail(GitRemoteFailureKind.Network, "timeout talking to github");

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("Could not resolve the thread.", result.Content);
    }

    [Fact]
    public async Task A_resolve_that_the_platform_does_not_confirm_is_not_reported_as_success()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));
        platform.ResolveDoesNotStick = true;

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("still unresolved", result.Content);
        Assert.False(platform.Threads[0].IsResolved);
    }

    [Fact]
    public async Task A_branch_without_a_pull_request_is_a_tool_error()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo");
        platform.ViewFailure = PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "none", reason: PrFailureReason.PullRequestNotFound);

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"));

        Assert.True(result.IsError);
        Assert.Contains("pr_create", result.Content);
        Assert.Empty(platform.Resolves);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"thread_id":"PRRT_1","force":true}""")]
    [InlineData("""{"thread_id":1}""")]
    public async Task Bad_arguments_are_tool_errors_that_resolve_nothing(string json)
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, json);

        Assert.True(result.IsError);
        Assert.Empty(platform.Resolves);
    }

    [Fact]
    public async Task A_cancelled_resolve_throws_and_resolves_nothing()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = Create("octo", ThreadWithReply("PRRT_1", PrReplyVerdict.PushBack));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, ResolveJson("PRRT_1"), source.Token));

        Assert.Empty(platform.Resolves);
    }
}
