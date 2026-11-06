using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class PrCommentListSmokeTests
{
    private const string PullRequestJson = """
        {"number":5,"url":"https://github.com/acme/forge/pull/5","state":"OPEN","title":"T","body":"B","headRefName":"feature","baseRefName":"main","headRefOid":"abc","isDraft":false,"mergeStateStatus":"CLEAN","reviewDecision":"CHANGES_REQUESTED"}
        """;

    private const string RepoJson = """{"name":"forge","owner":{"login":"acme"},"url":"https://github.com/acme/forge"}""";

    // Thread 1: open, a reviewer comment plus a recognisable apply reply by octo. Thread 2: resolved and outdated, on the
    // old side. Thread 3: open, never answered, a multi-line range.
    private const string ThreadsJson = """
        {"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":"c1"},"nodes":[
          {"id":"PRRT_1","isResolved":false,"isOutdated":false,"path":"src/A.cs","line":10,"startLine":null,"originalLine":10,"diffSide":"RIGHT","resolvedBy":null,
           "comments":{"pageInfo":{"hasNextPage":false,"endCursor":"x"},"nodes":[
             {"id":"PRRC_1","databaseId":111,"author":{"login":"reviewer"},"body":"please fix\nthis null check","createdAt":"2026-05-01T10:00:00Z","url":"https://github.com/acme/forge/pull/5#discussion_r111"},
             {"id":"PRRC_2","databaseId":112,"author":{"login":"octo"},"body":"**Verdict: apply** (0123abc)\n\nFixed in the latest push.","createdAt":"2026-05-01T11:00:00Z","url":null}]}},
          {"id":"PRRT_2","isResolved":true,"isOutdated":true,"path":"src/B.cs","line":null,"startLine":null,"originalLine":7,"diffSide":"LEFT","resolvedBy":{"login":"reviewer"},
           "comments":{"pageInfo":{"hasNextPage":false,"endCursor":"y"},"nodes":[
             {"id":"PRRC_3","databaseId":113,"author":{"login":"reviewer"},"body":"old remark","createdAt":"2026-04-30T09:00:00Z","url":null}]}},
          {"id":"PRRT_3","isResolved":false,"isOutdated":false,"path":"src/C.cs","line":30,"startLine":28,"originalLine":30,"diffSide":"RIGHT","resolvedBy":null,
           "comments":{"pageInfo":{"hasNextPage":false,"endCursor":"z"},"nodes":[
             {"id":"PRRC_4","databaseId":114,"author":{"login":"reviewer"},"body":"rename this","createdAt":"2026-05-01T12:00:00Z","url":null}]}}]}}}}}
        """;

    private static PrCommentTools Tools(IPrPlatform platform, PrReplyLedger? ledger = null, string? user = "octo") =>
        new(new GitCli(), platform, new GitToolSettings(), ledger ?? new PrReplyLedger(), reviews: null, new PrCommentHarness.FixedUser(user).Resolver);

    [Fact]
    public async Task Threads_read_through_gh_are_listed_with_location_comments_and_the_recovered_verdict()
    {
        using var fake = new SmokeFakeGh(stdout: ThreadsJson);
        fake.SetOutputFor("pr", PullRequestJson);
        fake.SetOutputFor("repo", RepoJson);
        var tools = Tools(new GitHubPlatform(fake.Cli));

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, fake.WorkingDirectory, "{}");

        Assert.False(result.IsError, result.Content);
        var text = result.Content;
        Assert.Contains("Pull request #5 (open): feature into main https://github.com/acme/forge/pull/5", text);
        Assert.Contains("3 review thread(s): 2 unresolved, 1 resolved.", text);
        Assert.Contains("Showing unresolved threads", text);

        Assert.Contains("Thread PRRT_1 [unresolved] src/A.cs:10", text);
        Assert.Contains("1. reviewer · 2026-05-01 10:00Z", text);
        Assert.Contains("     please fix\n     this null check", text);
        Assert.Contains("2. octo · 2026-05-01 11:00Z", text);
        Assert.Contains("your last verdict: apply (0123abc)", text);

        Assert.Contains("Thread PRRT_3 [unresolved] src/C.cs:28-30", text);
        Assert.Contains("no verdict from you yet", text);

        Assert.DoesNotContain("PRRT_2", text);
        Assert.DoesNotContain("old remark", text);
        Assert.Contains("Next: judge each unresolved thread first", text);
        Assert.DoesNotContain("== Pull request diff ==", text);
    }

    [Fact]
    public async Task All_includes_resolved_and_outdated_threads_with_who_resolved_them()
    {
        using var fake = new SmokeFakeGh(stdout: ThreadsJson);
        fake.SetOutputFor("pr", PullRequestJson);
        fake.SetOutputFor("repo", RepoJson);
        var tools = Tools(new GitHubPlatform(fake.Cli));

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, fake.WorkingDirectory, """{"all":true}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Showing all threads.", result.Content);
        Assert.Contains("Thread PRRT_2 [resolved, outdated] src/B.cs (was line 7) [old side]", result.Content);
        Assert.Contains("resolved by reviewer", result.Content);
        Assert.Contains("old remark", result.Content);
    }

    [Fact]
    public async Task A_marker_from_another_user_is_not_reported_as_your_verdict()
    {
        using var fake = new SmokeFakeGh(stdout: ThreadsJson);
        fake.SetOutputFor("pr", PullRequestJson);
        fake.SetOutputFor("repo", RepoJson);
        var tools = Tools(new GitHubPlatform(fake.Cli), user: "somebody-else");

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, fake.WorkingDirectory, "{}");

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("your last verdict", result.Content);
        Assert.Contains("no verdict from you yet", result.Content);
    }

    [Fact]
    public async Task Only_the_ledger_remembers_the_verdict_when_the_user_is_unknown()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "no"), 1),
            ]));
        var ledger = new PrReplyLedger();
        var tools = Tools(platform, ledger, user: null);

        var forgotten = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");
        ledger.Record("t1", PrReplyVerdict.PushBack, null, "c1");
        var remembered = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.Contains("no verdict from you yet", forgotten.Content);
        Assert.Contains("your last verdict: push_back", remembered.Content);
    }

    [Fact]
    public async Task Every_page_of_a_large_pull_request_reaches_the_listing()
    {
        var platform = new FakePrPlatform();
        for (var i = 0; i < 250; i++)
        {
            var comments = Enumerable.Range(0, 3)
                .Select(index => PrCommentHarness.Comment($"c{i}-{index}", index == 0 ? "reviewer" : "someone", $"remark {i}.{index}", index))
                .ToArray();
            platform.Threads.Add(PrCommentHarness.Thread($"t{i:000}", $"src/F{i}.cs", i + 1, comments: comments));
        }

        var tools = Tools(platform);
        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("250 review thread(s): 250 unresolved, 0 resolved.", result.Content);
        Assert.Contains("Thread t000 [unresolved] src/F0.cs:1", result.Content);
        Assert.Contains("Thread t249 [unresolved] src/F249.cs:250", result.Content);
        Assert.Contains("remark 249.2", result.Content);
        Assert.Equal(250, CountOccurrences(result.Content, "Thread t"));
    }

    [Fact]
    public async Task A_pull_request_without_threads_says_so()
    {
        var tools = Tools(new FakePrPlatform());

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("0 review thread(s)", result.Content);
        Assert.Contains("There are no review threads on this pull request.", result.Content);
    }

    [Fact]
    public async Task When_every_thread_is_resolved_the_default_listing_says_nothing_is_open()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("t1", resolved: true, resolvedBy: "reviewer"));
        var tools = Tools(platform);

        var open = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");
        var all = await PrCommentHarness.InvokeAsync(tools.Comments, "work", """{"all":true}""");

        Assert.Contains("There are no unresolved review threads.", open.Content);
        Assert.DoesNotContain("Thread t1", open.Content);
        Assert.Contains("Thread t1 [resolved]", all.Content);
    }

    [Fact]
    public async Task A_merged_pull_request_is_labelled_so()
    {
        var platform = new FakePrPlatform { PullRequest = PrCommentHarness.PullRequest(state: PrState.Merged) };
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.Contains("Pull request #5 (merged)", result.Content);
    }

    [Fact]
    public async Task Include_diff_appends_the_full_diff_after_the_threads()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("t1"));
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", """{"include_diff":true}""");

        Assert.False(result.IsError, result.Content);
        var threadAt = result.Content.IndexOf("Thread t1", StringComparison.Ordinal);
        var diffAt = result.Content.IndexOf("== Pull request diff ==", StringComparison.Ordinal);
        Assert.True(threadAt >= 0 && diffAt > threadAt);
        Assert.Contains("diff --git a/src/A.cs b/src/A.cs", result.Content[diffAt..]);
        Assert.DoesNotContain("WARNING", result.Content);
    }

    [Fact]
    public async Task A_truncated_diff_is_flagged_not_silently_cut()
    {
        var platform = new FakePrPlatform { Diff = PrResult<PrText>.Ok(new PrText("diff --git a/x b/x\n+head part", Truncated: true)) };
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", """{"include_diff":true}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("+head part", result.Content);
        Assert.Contains("WARNING: the diff was cut at the size cap", result.Content);
    }

    [Fact]
    public async Task A_failed_diff_read_is_a_warning_and_never_hides_the_threads()
    {
        var platform = new FakePrPlatform { Diff = PrResult<PrText>.Fail(GitRemoteFailureKind.Network, "connection reset") };
        platform.Threads.Add(PrCommentHarness.Thread("t1"));
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", """{"include_diff":true}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Thread t1", result.Content);
        Assert.Contains("WARNING: the diff could not be read, so it is NOT included", result.Content);
        Assert.Contains("connection reset", result.Content);
    }

    [Fact]
    public async Task A_branch_without_a_pull_request_points_at_pr_create()
    {
        var platform = new FakePrPlatform
        {
            ViewFailure = PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "none", reason: PrFailureReason.PullRequestNotFound),
        };
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.True(result.IsError);
        Assert.Contains("No pull request exists for this branch", result.Content);
        Assert.Contains("pr_create", result.Content);
        Assert.Equal(0, platform.ThreadReads);
    }

    [Fact]
    public async Task A_missing_gh_is_reported_with_install_guidance()
    {
        var platform = new FakePrPlatform
        {
            ViewFailure = PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "gh not found", reason: PrFailureReason.PlatformNotInstalled),
        };
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.True(result.IsError);
        Assert.Contains("GitHub CLI", result.Content);
        Assert.Contains("Local git tools are unaffected", result.Content);
    }

    [Fact]
    public async Task A_missing_login_while_reading_threads_is_reported_with_login_guidance()
    {
        var platform = new FakePrPlatform
        {
            ThreadsFailure = PrResult<IReadOnlyList<PrReviewThread>>.Fail(GitRemoteFailureKind.AuthenticationFailed, "bad credentials"),
        };
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.True(result.IsError);
        Assert.Contains("Could not read the review threads.", result.Content);
        Assert.Contains("gh auth login", result.Content);
    }

    [Fact]
    public async Task A_failing_user_lookup_only_costs_the_verdict_not_the_listing()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread(
            "t1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "why?"), 1),
            ]));
        var tools = new PrCommentTools(
            new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null,
            (_, _) => throw new InvalidOperationException("gh exploded"));

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Thread t1", result.Content);
        Assert.Contains("no verdict from you yet", result.Content);
    }

    [Fact]
    public async Task The_user_is_only_looked_up_when_a_thread_carries_a_marker_reply()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("t1"));
        var user = new PrCommentHarness.FixedUser("octo");
        var tools = new PrCommentTools(new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null, user.Resolver);

        await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");
        Assert.Equal(0, user.Calls);

        platform.Threads.Add(PrCommentHarness.Thread(
            "t2",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.Clarify, null, "why?"), 1),
            ]));
        await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");
        Assert.True(user.Calls >= 1);
    }

    [Fact]
    public async Task A_comment_without_an_author_or_body_still_renders()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread(
            "t1",
            comments:
            [
                new PrReviewComment("c0", null, null, "", null, null),
                PrCommentHarness.Comment("c1", "reviewer", "line one\r\nline two\r\n"),
            ]));
        var tools = Tools(platform);

        var result = await PrCommentHarness.InvokeAsync(tools.Comments, "work", "{}");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("(unknown author) · unknown time", result.Content);
        Assert.Contains("(empty)", result.Content);
        Assert.Contains("     line one\n     line two\n", result.Content);
    }

    [Fact]
    public async Task The_listing_only_reads_it_never_posts_or_resolves()
    {
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread("t1"));
        var tools = Tools(platform);

        await PrCommentHarness.InvokeAsync(tools.Comments, "work", """{"all":true,"include_diff":true}""");

        Assert.Empty(platform.Replies);
        Assert.Empty(platform.Resolves);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
