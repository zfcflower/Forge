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

        public Task<ToolResult> ReplyAsync(string json, CancellationToken ct = default) =>
            PrCommentHarness.InvokeAsync(Tools.Reply, Repo, json, ct);

        public void Dispose() => Sandbox.Dispose();
    }

    [Fact]
    public async Task An_apply_reply_citing_an_unpushed_commit_is_rejected_and_nothing_is_posted()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var unpushed = fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "local only");

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Fixed.", unpushed));

        Assert.True(result.IsError);
        Assert.Contains("has not been pushed", result.Content);
        Assert.Contains(unpushed[..7], result.Content);
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
        Assert.Contains($"citing {fixture.Pushed[..7]} (on origin/feature at {fixture.Pushed[..7]})", result.Content);

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
    public async Task A_commit_below_the_remote_tip_counts_as_pushed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "second");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q");
        var tip = IsolatedGit.Head(fixture.Repo);

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed));

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"(on origin/feature at {tip[..7]})", result.Content);
    }

    [Fact]
    public async Task The_remote_is_read_fresh_so_a_stale_tracking_ref_does_not_fake_a_push()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        var unpushed = fixture.Sandbox.CommitFile(fixture.Repo, "b.txt", "b\n", "local only");

        // Forge the remote-tracking ref so a lazy check against it would believe the commit is pushed.
        IsolatedGit.RunChecked(fixture.Repo, "update-ref", "refs/remotes/origin/feature", unpushed);

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Fixed.", unpushed));

        Assert.True(result.IsError);
        Assert.Contains("has not been pushed", result.Content);
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

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", theirs));

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"(on origin/feature at {theirs[..7]})", result.Content);
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

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", elsewhere));

        Assert.True(result.IsError);
        Assert.Contains("is not on origin/feature", result.Content);
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
        IsolatedGit.RunChecked(fixture.Repo, "reset", "-q", "--hard", "HEAD~1");
        fixture.Sandbox.CommitFile(fixture.Repo, "e.txt", "e\n", "replacement");
        IsolatedGit.RunChecked(fixture.Repo, "push", "-q", "--force");

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", doomed));

        Assert.True(result.IsError);
        Assert.Contains("rewritten", result.Content);
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

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", "deadbeefdeadbeef"));

        Assert.True(result.IsError);
        Assert.Contains("does not exist in this repository", result.Content);
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_branch_that_was_never_pushed_has_no_pushed_commits()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        IsolatedGit.RunChecked(fixture.Repo, "checkout", "-q", "-b", "feature2");
        var local = fixture.Sandbox.CommitFile(fixture.Repo, "f.txt", "f\n", "never pushed");
        fixture.Platform.PullRequest = PrCommentHarness.PullRequest(head: "feature2");

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", local));

        Assert.True(result.IsError);
        Assert.Contains("feature2 does not exist on origin", result.Content);
        Assert.Empty(fixture.Platform.Replies);
    }

    [Fact]
    public async Task A_detached_head_falls_back_to_the_branch_of_the_pull_request()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new Fixture();
        IsolatedGit.RunChecked(fixture.Repo, "checkout", "-q", "--detach");

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("(on origin/feature at", result.Content);
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

        var result = await fixture.ReplyAsync(Json("PRRT_1", "apply", "Done.", fixture.Pushed));

        Assert.True(result.IsError);
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
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "apply", "Done.", commit));

        Assert.True(result.IsError);
        Assert.Contains("not a commit sha", result.Content);
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
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", verdict, "text", commit));

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Empty(platform.Replies);
        Assert.Equal(0, platform.ThreadReads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_body_is_not_an_answer(string body)
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", body));

        Assert.True(result.IsError);
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
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, json);

        Assert.True(result.IsError);
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task An_unknown_thread_id_is_a_tool_error_that_points_at_the_listing()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_404", "push_back", "no"));

        Assert.True(result.IsError);
        Assert.Contains("'PRRT_404' is not a review thread of pull request #5", result.Content);
        Assert.Contains("pr_comments", result.Content);
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_thread_that_is_already_resolved_gets_no_reply_and_is_not_an_error()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1", resolved: true, resolvedBy: "reviewer"));
        var ledger = new PrReplyLedger();
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), ledger, null);

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
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

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

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no"));

        Assert.True(result.IsError);
        Assert.Contains("Could not post the reply.", result.Content);
        Assert.Null(ledger.Get("PRRT_1"));
        Assert.Null(ledger.CurrentUser);
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

        var result = await PrCommentHarness.InvokeAsync(tools.Reply, temp.Path, Json("PRRT_1", "push_back", "no"));

        Assert.True(result.IsError);
        Assert.Contains("pr_create", result.Content);
        Assert.Empty(platform.Replies);
    }

    [Fact]
    public async Task A_cancelled_reply_throws_and_posts_nothing()
    {
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);
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
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("PRRT_1"));
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null);

        var result = await PrCommentHarness.InvokeAsync(
            tools.Reply, temp.Path, """{"thread_id":"PRRT_1","verdict":"push_back","body":"\n  First line.\nSecond line.\n\n"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("**Verdict: push_back**\n\nFirst line.\nSecond line.", Assert.Single(platform.Replies).Body);
    }
}
