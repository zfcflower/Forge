using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;
using static Forge.App.Tests.Git.GitPanelTesting;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the pull request half of the Git panel (<c>GitPanelViewModel.PullRequest.cs</c>): the branch's pull request read
/// in a second phase after the local snapshot (its header, state and every notice: reading, none, read failed, gh not
/// installed), the fact that local git keeps working when gh does not, the review group (unresolved threads first, the
/// summary, the empty and the failed read, and the verdict and reply of every thread), and that a read belongs to the
/// context it was started for. The platform is a fake, so no test starts gh; the panel and everything it touches live on
/// one headless UI thread, and every wait is a bounded poll inside a dispatch (no sleeps).
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelPullRequestTests
{
    // ------------------------------------------------------------ the pull request of the branch

    [Fact]
    public async Task The_pull_request_of_the_branch_is_shown_with_its_number_title_and_state()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => Task.FromResult(PrKit.FoundPull(12, "Add the thing")),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request to load");

                    Assert.True(panel.ShowPullRequest);
                    Assert.Equal(Loc.Instance.Format(GitStrings.PullRequestHeader, 12, "Add the thing"), panel.PullRequestHeaderText);
                    Assert.Equal(Loc.Instance[GitStrings.PullRequestStateOpen], panel.PullRequestStateText);
                    Assert.Equal(string.Empty, panel.PullRequestNoticeText);
                    Assert.False(panel.HasPullRequestNotice);
                    Assert.False(panel.HasGhNotReady);
                    Assert.True(panel.HasRepository);
                    Assert.True(panel.ShowWaitButton);
                    Assert.True(panel.WaitForChecksCommand.CanExecute(null));
                    Assert.True(panel.ShowReview);
                    return 0;
                },
                CancellationToken.None);

            // One availability check, one view, one thread read - each for this worktree.
            Assert.Contains("availability:" + work, platform.Calls);
            Assert.Contains("view:" + work, platform.Calls);
            Assert.Contains("threads:" + work, platform.Calls);
        }
        finally
        {
            panel.Dispose();
        }
    }

    public static TheoryData<PrState, bool, string> StateTexts() => new()
    {
        { PrState.Open, false, GitStrings.PullRequestStateOpen },
        { PrState.Open, true, GitStrings.PullRequestStateDraft },
        { PrState.Closed, false, GitStrings.PullRequestStateClosed },
        { PrState.Closed, true, GitStrings.PullRequestStateClosed },
        { PrState.Merged, false, GitStrings.PullRequestStateMerged },
    };

    [Theory]
    [MemberData(nameof(StateTexts))]
    public async Task The_state_text_follows_the_platforms_state_and_draft_flag(PrState state, bool draft, string key)
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = _ => Task.FromResult(PrResult<PullRequestInfo>.Ok(PrKit.Pull(3, "Title", state, draft))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            var text = await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request to load");
                    return panel.PullRequestStateText;
                },
                CancellationToken.None);

            Assert.Equal(Loc.Instance[key], text);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_branch_without_a_pull_request_says_so_and_offers_no_wait_button()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => Task.FromResult(PrKit.NoPull()),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequestNotice, "the notice to arrive");

                    Assert.False(panel.HasPullRequest);
                    Assert.Equal(Loc.Instance[GitStrings.PullRequestNone], panel.PullRequestNoticeText);
                    Assert.False(panel.ShowWaitButton);
                    Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                    Assert.False(panel.ShowReview);
                    Assert.False(panel.HasReviewThreads);
                    Assert.False(panel.HasGhNotReady);
                    Assert.True(panel.HasRepository);

                    // No pull request means nothing to read reviews of.
                    Assert.DoesNotContain("threads:" + work, platform.Calls);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_failed_read_shows_the_platforms_message_and_keeps_local_git_working()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => Task.FromResult(
                PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Network, "could not resolve host: api.github.com")),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequestNotice, "the failure to arrive");

                    Assert.Equal(
                        Loc.Instance.Format(GitStrings.PullRequestReadFailed, "could not resolve host: api.github.com"),
                        panel.PullRequestNoticeText);
                    Assert.False(panel.HasPullRequest);
                    Assert.False(panel.ShowWaitButton);
                    Assert.False(panel.HasGhNotReady);

                    // The local half of the panel is unaffected by a gh that cannot answer.
                    Assert.True(panel.HasRepository);
                    Assert.Equal("main", panel.BranchText);
                    Assert.Contains("origin", panel.Remotes);
                    Assert.True(panel.RefreshCommand.CanExecute(null));
                    Assert.True(panel.FetchCommand.CanExecute(null));
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_platform_that_throws_instead_of_reporting_is_shown_as_a_failed_read()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => throw new InvalidOperationException("boom-from-platform"),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequestNotice, "the failure to arrive");

                    Assert.Equal(
                        Loc.Instance.Format(GitStrings.PullRequestReadFailed, "boom-from-platform"),
                        panel.PullRequestNoticeText);
                    Assert.False(panel.HasPullRequest);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ gh is missing or not signed in

    [Theory]
    [InlineData(PrPlatformState.NotInstalled, "gh is not installed")]
    [InlineData(PrPlatformState.NotAuthenticated, "not logged in to any GitHub host")]
    public async Task A_gh_that_cannot_be_used_shows_the_install_hint_and_no_pull_request(
        PrPlatformState state,
        string detail)
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnAvailability = _ => Task.FromResult(new PrPlatformAvailability(state, detail, null)),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasGhNotReady, "the gh hint to arrive");

                    Assert.Equal(Loc.Instance[GitStrings.PullRequestGhNotReady], panel.GhNotReadyText);
                    Assert.Equal(detail, panel.GhNotReadyDetail);
                    Assert.True(panel.HasGhNotReadyDetail);
                    Assert.False(panel.HasPullRequest);
                    Assert.False(panel.HasPullRequestNotice);
                    Assert.False(panel.ShowWaitButton);
                    Assert.False(panel.ShowReview);

                    // Local git is untouched by a gh that cannot be used.
                    Assert.True(panel.HasRepository);
                    Assert.True(panel.PushCommand.CanExecute(null));
                    return 0;
                },
                CancellationToken.None);

            // The pull request was never even asked for.
            Assert.DoesNotContain("view:" + work, platform.Calls);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_view_that_reports_a_missing_cli_shows_the_install_hint_too()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => Task.FromResult(
                PrResult<PullRequestInfo>.Fail(
                    GitRemoteFailureKind.Other,
                    "gh: command not found",
                    "stderr",
                    PrFailureReason.PlatformNotInstalled)),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasGhNotReady, "the gh hint to arrive");

                    Assert.Equal(Loc.Instance[GitStrings.PullRequestGhNotReady], panel.GhNotReadyText);
                    Assert.Equal("gh: command not found", panel.GhNotReadyDetail);
                    Assert.False(panel.HasPullRequestNotice);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ nothing to look for

    [Fact]
    public async Task A_repository_without_a_remote_shows_nothing_about_pull_requests()
    {
        var directory = GitTestFiles.NewTempDir("forge-pr-noremote-");
        try
        {
            GitTestShell.RunChecked(directory, "init", "-q", "-b", "main");
            var platform = new PrAppPlatform();

            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var panel = NewPanel(platform);
            try
            {
                await session.Dispatch(
                    async () =>
                    {
                        panel.Attach(Context("s1", directory));
                        await WaitUntilAsync(() => panel.HasRepository, "the local snapshot");

                        Assert.False(panel.ShowPullRequest);
                        Assert.False(panel.ShowWaitButton);
                        Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                        Assert.Equal(string.Empty, panel.ChecksText);
                        return 0;
                    },
                    CancellationToken.None);

                Assert.Equal(0, platform.TotalCalls);
            }
            finally
            {
                panel.Dispose();
            }
        }
        finally
        {
            GitTestFiles.DeleteTree(directory);
        }
    }

    [Fact]
    public async Task Without_the_pull_request_services_the_panel_is_purely_local()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = new GitPanelViewModel(
            new GitCli(),
            new GitPanelSettings(() => GitPullStrategy.FastForwardOnly, () => TimeSpan.FromSeconds(30)),
            static (_, _) => Task.FromResult(true));
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitIdleAsync(panel);
                    await QuietWindowAsync();

                    Assert.True(panel.HasRepository);
                    Assert.False(panel.ShowPullRequest);
                    Assert.False(panel.HasPullRequest);
                    Assert.False(panel.HasGhNotReady);
                    Assert.False(panel.ShowReview);
                    Assert.False(panel.IsWatching);
                    Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                    Assert.False(panel.CancelWaitCommand.CanExecute(null));
                    Assert.Equal(string.Empty, panel.ChecksText);
                    Assert.Equal(string.Empty, panel.WatchVerdictText);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ the review group

    [Fact]
    public async Task The_review_group_lists_unresolved_threads_first_and_summarizes_them()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnThreads = static _ => Task.FromResult(
                PrKit.Threads(
                    PrKit.Thread("resolved-1", resolved: true, path: "src/r.cs", line: 1, comments: PrKit.Comment("done", "a")),
                    PrKit.Thread("unresolved-1", path: "src/a.cs", line: 10, comments: PrKit.Comment("first", "b")),
                    PrKit.Thread("resolved-2", resolved: true, path: "src/s.cs", line: 2, comments: PrKit.Comment("done too", "c")),
                    PrKit.Thread("unresolved-2", path: "src/b.cs", line: 20, comments: PrKit.Comment("second", "d")))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the review threads");

                    Assert.True(panel.ShowReview);
                    Assert.Equal(
                        Loc.Instance.Format(GitStrings.ReviewSummary, 4, 2),
                        panel.ReviewSummaryText);
                    Assert.Equal(string.Empty, panel.ReviewNoticeText);
                    Assert.False(panel.HasReviewNotice);

                    // Unresolved first, the platform's order kept inside each group.
                    Assert.Equal(
                        ["unresolved-1", "unresolved-2", "resolved-1", "resolved-2"],
                        panel.ReviewThreads.Select(static item => item.Id).ToArray());
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Every_thread_shows_its_location_comment_replies_and_verdict()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnThreads = static _ => Task.FromResult(
                PrKit.Threads(
                    PrKit.Thread(
                        "applied",
                        path: "src/a.cs",
                        line: 12,
                        comments:
                        [
                            PrKit.Comment("this leaks the handle", "reviewer"),
                            PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Apply, "abc1234", "Wrapped it."), "agent", "c2"),
                        ]),
                    PrKit.Thread(
                        "pushed-back",
                        path: "src/b.cs",
                        line: 7,
                        comments:
                        [
                            PrKit.Comment("rename", "reviewer"),
                            PrKit.Comment("why not", "agent", "c2"),
                            PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "It is public API."), "agent", "c3"),
                        ]),
                    PrKit.Thread("silent", resolved: true, path: null, line: null, comments: PrKit.Comment("nit", "reviewer")))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            var rows = await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the review threads");
                    return panel.ReviewThreads
                        .Select(static item => new
                        {
                            item.Id,
                            item.LocationText,
                            item.FirstCommentText,
                            item.RepliesText,
                            item.VerdictText,
                            item.VerdictReplyText,
                            item.HasVerdict,
                        })
                        .ToArray();
                },
                CancellationToken.None);

            var applied = Assert.Single(rows, static row => row.Id == "applied");
            Assert.Equal("src/a.cs:12", applied.LocationText);
            Assert.Equal("reviewer: this leaks the handle", applied.FirstCommentText);
            Assert.Equal(Loc.Instance.Format(GitStrings.ReviewReplies, 1), applied.RepliesText);
            Assert.Equal(Loc.Instance.Format(GitStrings.ReviewVerdictApply, "abc1234"), applied.VerdictText);
            Assert.Equal("Wrapped it.", applied.VerdictReplyText);

            var pushedBack = Assert.Single(rows, static row => row.Id == "pushed-back");
            Assert.Equal(Loc.Instance.Format(GitStrings.ReviewReplies, 2), pushedBack.RepliesText);
            Assert.Equal(Loc.Instance[GitStrings.ReviewVerdictPushBack], pushedBack.VerdictText);
            Assert.Equal("It is public API.", pushedBack.VerdictReplyText);

            var silent = Assert.Single(rows, static row => row.Id == "silent");
            Assert.Equal(Loc.Instance[GitStrings.ReviewNoFile], silent.LocationText);
            Assert.False(silent.HasVerdict);
            Assert.Equal(string.Empty, silent.RepliesText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_pull_request_without_review_threads_says_there_are_none()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform();

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewNotice, "the review notice");

                    Assert.Equal(Loc.Instance[GitStrings.ReviewEmpty], panel.ReviewNoticeText);
                    Assert.Equal(string.Empty, panel.ReviewSummaryText);
                    Assert.False(panel.HasReviewThreads);
                    Assert.True(panel.ShowReview);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_failed_review_read_says_why_and_lists_nothing()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnThreads = static _ => Task.FromResult(
                PrResult<IReadOnlyList<PrReviewThread>>.Fail(GitRemoteFailureKind.Network, "graphql: bad gateway")),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewNotice, "the review failure");

                    Assert.Equal(
                        Loc.Instance.Format(GitStrings.ReviewReadFailed, "graphql: bad gateway"),
                        panel.ReviewNoticeText);
                    Assert.False(panel.HasReviewThreads);
                    Assert.Equal(string.Empty, panel.ReviewSummaryText);

                    // The pull request itself is still shown: only the review read failed.
                    Assert.True(panel.HasPullRequest);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task The_review_group_says_it_is_reading_while_the_threads_load()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform
        {
            OnThreads = async call =>
            {
                await gate.WaitAsync(call.Ct);
                return PrKit.Threads(PrKit.Thread("t1", comments: PrKit.Comment("hi", "reviewer")));
            },
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    await WaitUntilAsync(() => panel.HasReviewNotice, "the reading notice");
                    Assert.Equal(Loc.Instance[GitStrings.ReviewLoading], panel.ReviewNoticeText);
                    Assert.False(panel.HasReviewThreads);
                    Assert.Equal(string.Empty, panel.ReviewSummaryText);

                    gate.Release();
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the threads");
                    Assert.Equal(string.Empty, panel.ReviewNoticeText);
                    Assert.False(panel.HasReviewNotice);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ a read belongs to its context

    [Fact]
    public async Task Switching_to_another_session_drops_the_pull_request_read_of_the_previous_one()
    {
        using var sandbox = GitTestSandbox.Create();
        var workA = sandbox.CloneTracked("a");
        var workB = sandbox.Clone("b");
        sandbox.CommitFile(workB, "other.txt", "b" + Environment.NewLine, "b initial");
        GitTestShell.RunChecked(workB, "push", "-q", "-u", "origin", "main");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform
        {
            OnAvailability = async call =>
            {
                if (string.Equals(call.Directory, workA, StringComparison.OrdinalIgnoreCase))
                {
                    await gate.WaitAsync(call.Ct);
                }

                return PrKit.Ready;
            },
            OnView = call => Task.FromResult(
                string.Equals(call.Directory, workA, StringComparison.OrdinalIgnoreCase)
                    ? PrKit.FoundPull(1, "pull of A")
                    : PrKit.FoundPull(2, "pull of B")),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", workA));
                    await WaitUntilAsync(() => gate.Entered.Count >= 1, "the read of A to reach the platform");
                    Assert.False(panel.HasPullRequest);

                    panel.Attach(Context("s2", workB));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request of B");
                    Assert.Contains("pull of B", panel.PullRequestHeaderText, StringComparison.Ordinal);

                    // The cancelled read of A unwinds and applies nothing, even after it is let through.
                    gate.Release();
                    await QuietWindowAsync();
                    Assert.Contains("pull of B", panel.PullRequestHeaderText, StringComparison.Ordinal);
                    return 0;
                },
                CancellationToken.None);

            Assert.DoesNotContain("view:" + workA, platform.Calls);
            Assert.True(gate.Cancelled.Count >= 1);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Moving_to_an_empty_context_forgets_the_pull_request_and_the_threads()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnThreads = static _ => Task.FromResult(
                PrKit.Threads(PrKit.Thread("t1", comments: PrKit.Comment("hi", "reviewer")))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the threads");

                    panel.Attach(PanelContext.Empty);
                    await QuietWindowAsync();

                    Assert.False(panel.ShowPullRequest);
                    Assert.False(panel.HasPullRequest);
                    Assert.False(panel.HasReviewThreads);
                    Assert.Empty(panel.ReviewThreads);
                    Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ the localized row texts

    [Fact]
    public async Task The_review_rows_and_the_verdicts_are_re_read_after_a_language_switch()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnThreads = static _ => Task.FromResult(
                PrKit.Threads(
                    PrKit.Thread(
                        "t1",
                        outdated: true,
                        comments:
                        [
                            PrKit.Comment("rename", "reviewer"),
                            PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "Public API."), "agent", "c2"),
                        ]))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        var previous = Loc.Instance.Language;
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the threads");
                    var english = Assert.Single(panel.ReviewThreads);
                    var englishStatus = english.StatusText;
                    var englishVerdict = english.VerdictText;

                    Loc.Instance.Language = UiLanguage.Chinese;
                    var chinese = Assert.Single(panel.ReviewThreads);
                    Assert.Equal(
                        Loc.Instance[GitStrings.ReviewUnresolved] + " · " + Loc.Instance[GitStrings.ReviewOutdated],
                        chinese.StatusText);
                    Assert.Equal(Loc.Instance[GitStrings.ReviewVerdictPushBack], chinese.VerdictText);

                    Loc.Instance.Language = UiLanguage.English;
                    var again = Assert.Single(panel.ReviewThreads);
                    Assert.Equal(englishStatus, again.StatusText);
                    Assert.Equal(englishVerdict, again.VerdictText);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            Loc.Instance.Language = previous;
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ rendering

    [Fact]
    public async Task The_tool_view_renders_the_pull_request_the_review_group_and_its_verdicts()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnView = static _ => Task.FromResult(PrKit.FoundPull(21, "Fix the leak")),
            OnThreads = static _ => Task.FromResult(
                PrKit.Threads(
                    PrKit.Thread(
                        "t1",
                        path: "src/a.cs",
                        line: 10,
                        comments:
                        [
                            PrKit.Comment("please rename", "reviewer"),
                            PrKit.Comment(PrReplyLedger.FormatReply(PrReplyVerdict.Apply, "abc1234", "Renamed it."), "agent", "c2"),
                        ]),
                    PrKit.Thread("t2", resolved: true, path: "src/b.cs", line: 3, comments: PrKit.Comment("ok", "reviewer")))),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasReviewThreads, "the threads");
                    return 0;
                },
                CancellationToken.None);

            var rendered = await session.Dispatch(
                () =>
                {
                    var view = new GitToolView { DataContext = panel };
                    var window = new Window { Width = 480, Height = 900, Content = view };
                    window.Show();
                    Settle(window);
                    try
                    {
                        return new
                        {
                            Texts = window.GetVisualDescendants()
                                .OfType<TextBlock>()
                                .Select(static block => block.Text)
                                .OfType<string>()
                                .ToList(),
                            PullRequestNotice = TextOf(window, "git-pr-notice"),
                            ReviewNotice = TextOf(window, "git-review-notice"),
                            ReviewSummary = TextOf(window, "git-review-summary"),
                        };
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

            Assert.Contains(Loc.Instance.Format(GitStrings.PullRequestHeader, 21, "Fix the leak"), rendered.Texts);
            Assert.Equal(Loc.Instance.Format(GitStrings.ReviewSummary, 2, 1), rendered.ReviewSummary);
            Assert.Contains("src/a.cs:10", rendered.Texts);
            Assert.Contains("reviewer: please rename", rendered.Texts);
            Assert.Contains(Loc.Instance.Format(GitStrings.ReviewVerdictApply, "abc1234"), rendered.Texts);
            Assert.Contains("Renamed it.", rendered.Texts);
            Assert.Contains("src/b.cs:3", rendered.Texts);
            Assert.Contains(Loc.Instance[GitStrings.ReviewResolved], rendered.Texts);
            Assert.Equal(string.Empty, rendered.PullRequestNotice ?? string.Empty);
            Assert.Equal(string.Empty, rendered.ReviewNotice ?? string.Empty);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task The_tool_view_renders_the_no_pull_request_notice_and_the_gh_hint()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform
        {
            OnAvailability = static _ => Task.FromResult(
                new PrPlatformAvailability(PrPlatformState.NotInstalled, "gh: not found on PATH")),
        };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasGhNotReady, "the gh hint");
                    return 0;
                },
                CancellationToken.None);

            var rendered = await session.Dispatch(
                () =>
                {
                    var view = new GitToolView { DataContext = panel };
                    var window = new Window { Width = 480, Height = 900, Content = view };
                    window.Show();
                    Settle(window);
                    try
                    {
                        return new
                        {
                            Hint = TextOf(window, "git-gh-not-ready"),
                            Detail = TextOf(window, "git-gh-not-ready-detail"),
                        };
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

            Assert.Equal(Loc.Instance[GitStrings.PullRequestGhNotReady], rendered.Hint);
            Assert.Equal("gh: not found on PATH", rendered.Detail);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ harness

    private static GitPanelViewModel NewPanel(
        PrAppPlatform platform,
        Func<PrChecksPollerOptions>? options = null,
        PrAppNotifier? notifier = null) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => GitPullStrategy.FastForwardOnly, () => TimeSpan.FromSeconds(30)),
            static (_, _) => Task.FromResult(true),
            new GitPrServices(
                platform,
                new PrChecksPoller(platform, new PrAppManualTime()),
                options ?? (static () => new PrChecksPollerOptions()),
                notifier));

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static string? TextOf(Window window, string automationId) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(control => string.Equals(
                Avalonia.Automation.AutomationProperties.GetAutomationId(control),
                automationId,
                StringComparison.Ordinal))?.Text;
}
