using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class PrCommentReplySmokeTests
{
    private static string Json(string threadId, string verdict, string body, string? commit = null) =>
        commit is null
            ? $$"""{"thread_id":"{{threadId}}","verdict":"{{verdict}}","body":"{{body}}"}"""
            : $$"""{"thread_id":"{{threadId}}","verdict":"{{verdict}}","body":"{{body}}","commit":"{{commit}}"}""";

    /// <summary>
    /// A local repo on branch <c>feature</c> that tracks <c>origin/feature</c> (a local bare remote) with one pushed commit,
    /// plus a fake platform that has the pull request for <c>feature</c> and one open thread.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Sandbox = new RemoteSandbox();
            Repo = Sandbox.Clone("alice");
            Sandbox.CommitFile(Repo, "base.txt", "base\n", "seed");
            IsolatedGit.RunChecked(Repo, "push", "-q", "-u", "origin", "main");
            IsolatedGit.RunChecked(Repo, "checkout", "-q", "-b", "feature");
            Pushed = Sandbox.CommitFile(Repo, "a.txt", "a\n", "feature work");
            IsolatedGit.RunChecked(Repo, "push", "-q", "-u", "origin", "feature");

            Platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));

            // The platform reports the pushed commit as the head of the pull request.
            Platform.PullRequest = PrCommentHarness.PullRequest() with { HeadRefOid = Pushed };
            Ledger = new PrReplyLedger();
            Tools = new PrCommentTools(new GitCli(), Platform, new GitToolSettings(), Ledger, null);
        }

        public RemoteSandbox Sandbox { get; }

        public string Repo { get; }

        /// <summary>The full sha of the commit that is on origin/feature.</summary>
        public string Pushed { get; }

        public FakePrPlatform Platform { get; } = new();

        public PrReplyLedger Ledger { get; }

        public PrCommentTools Tools { get; }

        /// <summary>Moves the head the platform reports for the pull request, as a push would.</summary>
        public void MovePullRequestHead(string sha) =>
            Platform.PullRequest = Platform.PullRequest with { HeadRefOid = sha };

        public Task<ToolResult> ReplyAsync(string json, CancellationToken ct = default) =>
            PrCommentHarness.InvokeAsync(Tools.Reply, Repo, json, ct);

        public void Dispose() => Sandbox.Dispose();
    }

    /// <summary>Where an accepted <c>apply</c> reply reports the commit: the head of the pull request.</summary>
    private static string OnPullRequestHead(string sha) => $"(on the head of pull request #5 at {sha[..7]})";

    [Fact]
    public async Task An_apply_reply_citing_an_unpushed_commit_is_rejected_and_nothing_is_posted()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var unpushed = fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "local only");

        var result = await ToolResultAssertions.RefusedAsync(
            fixture.ReplyAsync(Json("PRRT_1", "apply", "Fixed.", unpushed)),
            "has not been pushed",
            unpushed[..7]);
        Assert.Empty(fixture.Platform.Replies);
        Assert.Null(fixture.Ledger.Get("PRRT_1"));
    }

    [Fact]
    public async Task An_apply_reply_citing_a_pushed_commit_posts_the_marker_and_is_remembered()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Fixed the null check.", fixture.Pushed));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Posted a reply with verdict apply to thread PRRT_1 of pull request #5", result.Content);
        Assert.Contains($"citing {fixture.Pushed[..7]} {OnPullRequestHead(fixture.Pushed)}", result.Content);

        var (threadId, body) = Assert.Single(fixture.Platform.Replies);
        Assert.Equal("PRRT_1", threadId);
        Assert.Equal($"**Verdict: apply** ({fixture.Pushed[..7]})\n\nFixed the null check.", body);

        var record = fixture.Ledger.Get("PRRT_1");
        Assert.NotNull(record);
        Assert.Equal(PrReplyVerdict.Apply, record.Verdict);
        Assert.Equal(fixture.Pushed, record.Commit);
        Assert.NotNull(record.CommentId);
        Assert.Equal("octo", fixture.Ledger.CurrentUser);
    }

    [Fact]
    public async Task An_abbreviated_sha_is_accepted_and_the_marker_cites_the_short_form_of_the_full_sha()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed[..10]));

        Assert.False(result.IsError, result.Content);
        Assert.StartsWith($"**Verdict: apply** ({fixture.Pushed[..7]})", Assert.Single(fixture.Platform.Replies).Body);
        Assert.Equal(fixture.Pushed, fixture.Ledger.Get("PRRT_1")!.Commit);
    }

    [Fact]
    public async Task A_commit_below_the_pull_request_head_counts_as_pushed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "second");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q");
        var tip = IsolatedGit.Head(fixture.Repo);
        fixture.MovePullRequestHead(tip);

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed));

        Assert.False(result.IsError, result.Content);
        Assert.Contains(OnPullRequestHead(tip), result.Content);
    }

    [Fact]
    public async Task The_pull_request_head_is_the_evidence_not_a_local_remote_tracking_ref()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var unpushed = fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "local only");

        // Forge the remote-tracking ref so a lazy check against it would believe the commit is pushed.
        IsolatedGit.RunChecked(fixture.Repo, "update-ref", "refs/remotes/origin/feature", unpushed);

        var result = await ToolResultAssertions.RefusedAsync(
            fixture.ReplyAsync(Json("PRRT_1", "apply", "Fixed.", unpushed)),
            "has not been pushed");
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_commit_somebody_else_pushed_is_fetched_before_the_check()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var bob = fixture.Sandbox.Clone("bob");
        IsolatedGit.RunChecked(bob, "checkout", "-q", "feature");
        var theirs = fixture.Sandbox.CommitFile(bob, "c.txt", "c\n", "from bob");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "feature");
        Assert.NotEqual(0, IsolatedGit.Run(fixture.Repo, "cat-file", "-e", theirs + "^{commit}").ExitCode);
        fixture.MovePullRequestHead(theirs);

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", theirs));

        Assert.False(result.IsError, result.Content);
        Assert.Contains(OnPullRequestHead(theirs), result.Content);
        Assert.Equal(theirs, fixture.Ledger.Get("PRRT_1")!.Commit);
    }

    [Fact]
    public async Task A_commit_pushed_to_another_branch_does_not_count_for_this_pull_request()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        IsolatedGit.RunChecked(fixture.Repo, "checkout", "-q", "-b", "other");
        var elsewhere = fixture.Sandbox.CommitFile(fixture.Repo, "o.txt", "o\n", "other branch");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q", "-u", "origin", "other");
        IsolatedGit.RunChecked(fixture.Repo, "checkout", "-q", "feature");

        var result = await ToolResultAssertions.RefusedAsync(
            fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", elsewhere)),
            "is not on the head of pull request #5");
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_commit_dropped_by_a_force_push_is_no_longer_pushed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var doomed = fixture.Sandbox.CommitFile(fixture.Repo, "d.txt", "d\n", "will be rewritten");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q");
        fixture.MovePullRequestHead(doomed);
        IsolatedGit.RunChecked(fixture.Repo, "reset", "-q", "--hard", "HEAD~1");
        var replacement = fixture.Sandbox.CommitFile(fixture.Repo, "e.txt", "e\n", "replacement");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q", "--force");
        fixture.MovePullRequestHead(replacement);

        var result = await ToolResultAssertions.RefusedAsync(fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", doomed)), "rewritten");
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_sha_that_does_not_exist_is_rejected_even_after_fetching()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await ToolResultAssertions.RefusedAsync(
            fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", "deadbeefdeadbeef")),
            "does not exist in this repository");
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_pull_request_head_that_cannot_be_resolved_is_a_tool_error()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        fixture.MovePullRequestHead("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

        var result = await ToolResultAssertions.RefusedAsync(
            fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed)),
            "The head of pull request #5 (deadbee) is not available locally");
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task An_apply_reply_works_from_a_detached_head_because_the_pull_request_head_is_the_evidence()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        IsolatedGit.RunChecked(fixture.Repo, "checkout", "-q", "--detach");

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed));

        Assert.False(result.IsError, result.Content);
        Assert.Contains(OnPullRequestHead(fixture.Pushed), result.Content);
    }

    [Fact]
    public async Task An_unreachable_remote_is_a_tool_error_and_nothing_is_posted()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        IsolatedGit.RunChecked(fixture.Repo, "remote", "set-url", "origin", Path.Combine(fixture.Sandbox.Root, "missing.git"));

        // The cited commit is local, the head is not, so the head would have to be fetched - which cannot work now.
        fixture.MovePullRequestHead("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        var result = await ToolResultAssertions.RefusedAsync(fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed)));
        Assert.Empty(fixture.Platform.Replies);
        Assert.Null(fixture.Ledger.Get("PRRT_1"));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("feature")]
    [InlineData("HEAD~1")]
    [InlineData("abc12")]
    public async Task A_commit_that_is_not_a_sha_is_rejected_without_a_post(string commit)
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "apply", "Done.", commit)),
            "not a commit sha");
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task Push_back_and_clarify_post_their_marker_without_any_commit_and_are_remembered()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_2"));
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null);

        var pushBack = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "Out of scope for this PR."));
        var clarify = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_2", "clarify", "Which overload do you mean?"));

        Assert.False(pushBack.IsError, pushBack.Content);
        Assert.False(clarify.IsError, clarify.Content);
        Assert.Contains("Close it with pr_comment_resolve", pushBack.Content);
        Assert.Contains("The thread stays open", clarify.Content);
        Assert.Equal("**Verdict: push_back**\n\nOut of scope for this PR.", platform.Replies[0].Body);
        Assert.Equal("**Verdict: clarify**\n\nWhich overload do you mean?", platform.Replies[1].Body);
        Assert.Equal(PrReplyVerdict.PushBack, ledger.Get("PRRT_1")!.Verdict);
        Assert.Null(ledger.Get("PRRT_1")!.Commit);
        Assert.Equal(PrReplyVerdict.Clarify, ledger.Get("PRRT_2")!.Verdict);
    }

    [Theory]
    [InlineData("apply", null, "needs 'commit'")]
    [InlineData("apply", "", "needs 'commit'")]
    [InlineData("push_back", "0123abc", "belongs to verdict apply only")]
    [InlineData("clarify", "0123abc", "belongs to verdict apply only")]
    [InlineData("approve", null, "Unknown verdict 'approve'")]
    [InlineData("Apply", "0123abc", "Unknown verdict 'Apply'")]
    public async Task Invalid_verdict_and_commit_combinations_never_reach_the_platform(string verdict, string? commit, string expected)
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", verdict, "text", commit)),
            expected);
        Assert.Empty(platform.Replies);
        Assert.Equal(0, platform.ThreadReads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_body_is_not_an_answer(string body)
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", body)));
        Assert.Empty(platform.Replies);
    }

    [Theory]
    [InlineData("""{"verdict":"clarify","body":"x"}""")]
    [InlineData("""{"thread_id":"PRRT_1","body":"x"}""")]
    [InlineData("""{"thread_id":"PRRT_1","verdict":"clarify"}""")]
    [InlineData("""{"thread_id":"PRRT_1","verdict":"clarify","body":"x","resolve":true}""")]
    public async Task Missing_required_arguments_and_unknown_properties_are_tool_errors(string json)
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await ToolResultAssertions.RefusedAsync(PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, json));
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task An_unknown_thread_id_is_a_tool_error_that_points_at_the_listing()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_404", "push_back", "no")),
            "'PRRT_404' is not a review thread of pull request #5",
            "pr_comments");
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_thread_that_is_already_resolved_gets_no_reply_and_is_not_an_error()
    {
        using var temp = new TempDirectory();
        var (tools, platform, ledger) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1", resolved: true, resolvedBy: "reviewer"));

        // Apply with a sha that no repository knows: the resolved short-circuit must come before the push gate.
        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "apply", "Done.", "0123abc"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Thread PRRT_1 is already resolved (by reviewer). No reply was posted.", result.Content);
        Assert.Empty(platform.Replies);
        Assert.Null(ledger.Get("PRRT_1"));
    }

    [Fact]
    public async Task The_thread_is_read_again_right_before_replying_so_a_late_resolve_is_noticed()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var listing = await PrCommentHarness.InvokeAsync(tools.Comments, temp.Path, "{}");
        Assert.Contains("Thread PRRT_1 [unresolved]", listing.Content);
        Assert.Equal(1, platform.ThreadReads);

        // Somebody resolves the thread after the agent listed it.
        platform.Threads[0] = platform.Threads[0] with { IsResolved = true, ResolvedBy = "reviewer" };
        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no"));

        Assert.Equal(2, platform.ThreadReads);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("already resolved", result.Content);
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_failed_post_is_a_tool_error_and_leaves_no_trace_in_the_ledger()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform
        {
            ReplyFailure = PrResult<PrReviewComment>.Fail(GitRemoteFailureKind.Network, "could not resolve host"),
        };
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null);

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no")),
            "Could not post the reply.");
        Assert.Null(ledger.Get("PRRT_1"));
        Assert.Null(ledger.CurrentUser);
    }

    [Fact]
    public async Task A_reply_whose_answer_was_lost_is_not_posted_twice()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform { LoseReplyResponse = true };
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "Out of scope."));

        // The comment was created; the timeout did not become a second copy, and the verdict is registered.
        Assert.False(result.IsError, result.Content);
        Assert.Contains("was already on the thread", result.Content);
        Assert.Single(platform.Replies);
        Assert.Single(platform.Threads[0].Comments, comment => comment.Body.StartsWith("**Verdict:", StringComparison.Ordinal));
        Assert.Equal(PrReplyVerdict.PushBack, ledger.Get("PRRT_1")!.Verdict);
        Assert.Null(ledger.PendingReply("PRRT_1"));
    }

    [Fact]
    public async Task A_reply_that_timed_out_without_landing_is_an_error_and_the_retry_posts_exactly_once()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform
        {
            ReplyFailure = PrResult<PrReviewComment>.Fail(GitRemoteFailureKind.TimedOut, "gh api timed out"),
        };
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null);

        var timedOut = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "Out of scope.")),
            "Could not post the reply.");
        Assert.Empty(platform.Replies);
        Assert.Null(ledger.Get("PRRT_1"));
        Assert.NotNull(ledger.PendingReply("PRRT_1"));

        // The agent retries the same call: the thread is read first, the reply is not there, so it is posted once.
        platform.ReplyFailure = null;
        var retried = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "Out of scope."));

        Assert.False(retried.IsError, retried.Content);
        Assert.Single(platform.Replies);
        Assert.Equal(PrReplyVerdict.PushBack, ledger.Get("PRRT_1")!.Verdict);
        Assert.Null(ledger.PendingReply("PRRT_1"));
    }

    [Fact]
    public async Task A_branch_without_a_pull_request_is_a_tool_error()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform
        {
            ViewFailure = PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "none", reason: PrFailureReason.PullRequestNotFound),
        };
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await ToolResultAssertions.RefusedAsync(
            PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no")),
            "pr_create");
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_cancelled_reply_throws_and_posts_nothing()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no"), source.Token));

        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_reply_body_is_trimmed_and_multi_line_text_survives()
    {
        using var temp = new TempDirectory();
        var (tools, platform, _) = PrCommentHarness.Tools(PrCommentHarness.Thread("PRRT_1"));

        var result = await PrCommentHarness.InvokeAsync(
            tools.Reply, temp.Path, """{"thread_id":"PRRT_1","verdict":"push_back","body":"\n  First line.\nSecond line.\n\n"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("**Verdict: push_back**\n\nFirst line.\nSecond line.", Assert.Single(platform.Replies).Body);
    }
}
