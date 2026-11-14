using System.Windows.Input;
using Avalonia.Headless;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the two write paths W5 added to the files panel's 提交目标 row: 合并 PR together with the post-merge follow-up on the
/// repository root checkout, and 标记为可评审. Every test drives the row against real repositories on disk (a bare origin, a
/// session directory on a published branch, a separate root checkout of the same origin) and a scripted
/// <see cref="IPrPlatform"/>; what the follow-up did is read back from the repositories themselves, never from the row's own
/// words alone. The follow-up's four outcomes are pinned one test each — skipped off the pull request's base branch (the
/// fetch still ran, the checkout did not move), fast-forwarded, dirty (fetched but not pulled), and never run after a failed
/// merge — plus the merge queue's message that outranks it, the ship-write discipline (busy, one write per click, command
/// disabled while it runs) and mark-ready (draft only, one call, success re-reads, a refusal reports the platform's message).
/// Nothing sleeps: a merge a test wants to observe in flight is held by a completion source, and every wait polls with a
/// 30 s bound.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSyncShipFinaleTests
{
    private const int Number = 7;

    // ------------------------------------------------------------ the follow-up after a successful merge

    [Fact]
    public async Task A_merge_follow_up_skips_a_root_checkout_that_is_not_on_the_pull_requests_base_branch()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-skip");
        var merged = MergeLandsOnMain(sandbox, "finale-skip");
        var headBefore = HeadOf(root);

        var platform = new ShipPlatform { Current = PullRequest(baseRefName: "release/1") };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);
            Assert.Equal(
                PullRequestPhase.Open,
                await session.Dispatch(() => panel.PullRequestPhase, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.MessageIsError, panel.PullRequestPhase },
                CancellationToken.None);

            // The base branch the follow-up follows is the pull request's ("release/1"), not the repository's default: the
            // fetch ran, but the root checkout is on main, so pulling it would integrate into a branch nobody merged into.
            Assert.False(state.MessageIsError);
            Assert.Equal(
                Loc.Instance.Format(
                    GitStrings.ShipMergedWithFinale,
                    Number,
                    Loc.Instance.Format(GitStrings.ShipFinaleSkipped, "release/1", "main")),
                state.MessageText);
            Assert.Equal("main", BranchOf(root));
            Assert.Equal(headBefore, HeadOf(root));
            Assert.Equal(merged, RefOf(root, "origin/main"));
            Assert.Equal(PullRequestPhase.Merged, state.PullRequestPhase);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_merge_follow_up_fast_forwards_a_clean_root_checkout_that_is_on_the_base_branch()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-pull");
        var merged = MergeLandsOnMain(sandbox, "finale-pull");

        var platform = new ShipPlatform { Current = PullRequest(baseRefName: "main") };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.MessageIsError },
                CancellationToken.None);

            // The root checkout followed origin/main by a fast-forward and says where it landed: git's short commit.
            Assert.False(state.MessageIsError);
            Assert.Equal(
                Loc.Instance.Format(
                    GitStrings.ShipMergedWithFinale,
                    Number,
                    Loc.Instance.Format(GitStrings.ShipFinaleUpdated, "main", merged[..7])),
                state.MessageText);
            Assert.Equal(merged, HeadOf(root));
            Assert.Equal(merged, RefOf(root, "origin/main"));
            Assert.True(File.Exists(Path.Combine(root, "merged.txt")));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_merge_follow_up_only_fast_forwards_even_when_the_pull_setting_rebases()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-ff-only");
        var merged = MergeLandsOnMain(sandbox, "finale-ff-only");
        // A local commit on the root's main that origin does not have: a rebase would rewrite it behind the user's back.
        sandbox.CommitFile(root, "local.txt", "local\n", "a local commit on the root checkout");
        var localHead = HeadOf(root);

        var platform = new ShipPlatform { Current = PullRequest(baseRefName: "main") };
        var session = SharedUi.Session;
        var panel = NewPanel(platform, GitPullStrategy.Rebase);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.MessageIsError },
                CancellationToken.None);

            // The fast-forward is refused and reported; the merge itself still reads as done, and the checkout is untouched.
            Assert.True(state.MessageIsError);
            Assert.StartsWith(
                Loc.Instance.Format(GitStrings.ShipMergedFinaleProblem, Number, string.Empty).TrimEnd(),
                state.MessageText,
                StringComparison.Ordinal);
            Assert.Equal(localHead, HeadOf(root));
            Assert.Equal(merged, RefOf(root, "origin/main"));
            Assert.False(File.Exists(Path.Combine(root, "merged.txt")));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_dirty_root_checkout_is_fetched_but_the_base_branch_is_not_pulled()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-dirty");
        var merged = MergeLandsOnMain(sandbox, "finale-dirty");
        // An uncommitted change in the root checkout: nothing the follow-up may fast-forward over.
        File.WriteAllText(Path.Combine(root, "root-dirty.txt"), "not committed");
        var headBefore = HeadOf(root);

        var platform = new ShipPlatform { Current = PullRequest(baseRefName: "main") };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.MessageIsError },
                CancellationToken.None);

            Assert.False(state.MessageIsError);
            Assert.Equal(
                Loc.Instance.Format(
                    GitStrings.ShipMergedWithFinale,
                    Number,
                    Loc.Instance.Format(GitStrings.ShipFinaleDirty, "main")),
                state.MessageText);

            // Fetched, and then stopped: an uncommitted change is the user's work, so the checkout keeps its commit and the
            // merged commit never reaches the working tree.
            Assert.Equal(merged, RefOf(root, "origin/main"));
            Assert.Equal(headBefore, HeadOf(root));
            Assert.False(File.Exists(Path.Combine(root, "merged.txt")));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_merge_reports_the_platform_message_and_never_runs_the_follow_up()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-nomerge");
        var merged = MergeLandsOnMain(sandbox, "finale-nomerge");
        var headBefore = HeadOf(root);

        var platform = new ShipPlatform
        {
            Current = PullRequest(baseRefName: "main"),
            MergeResult = PrResult<PrReference>.Fail(
                GitRemoteFailureKind.Other,
                "gh: base branch protection rules rejected the merge",
                "protected branch"),
        };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.MessageIsError, panel.PullRequestPhase },
                CancellationToken.None);

            Assert.True(state.MessageIsError);
            Assert.Equal("gh: base branch protection rules rejected the merge", state.MessageText);
            Assert.NotEqual(
                Loc.Instance.Format(
                    GitStrings.ShipMergedWithFinale,
                    Number,
                    Loc.Instance.Format(GitStrings.ShipFinaleUpdated, "main", merged[..7])),
                state.MessageText);

            // No fetch at all: the root checkout's origin/main still points where it was cloned, so the commit the merge
            // would have landed was never asked for, and the checkout did not move either.
            Assert.Equal(headBefore, RefOf(root, "origin/main"));
            Assert.Equal(headBefore, HeadOf(root));
            Assert.False(File.Exists(Path.Combine(root, "merged.txt")));
            Assert.Equal(PullRequestPhase.Open, state.PullRequestPhase);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_merge_queue_keeps_its_message_instead_of_the_follow_up()
    {
        using var sandbox = GitTestSandbox.Create();
        var (work, root) = SessionAndRoot(sandbox, "finale-queue");
        var merged = MergeLandsOnMain(sandbox, "finale-queue");

        // A repository with a merge queue accepts the merge and keeps the pull request open until the queue drains.
        var platform = new ShipPlatform { Current = PullRequest(baseRefName: "main"), KeepOpenAfterMerge = true };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root);

            await ExecuteAndWaitAsync(session, panel, panel.MergePullRequestCommand);

            var state = await session.Dispatch(
                () => new { panel.MessageText, panel.PullRequestPhase },
                CancellationToken.None);

            // The follow-up did run (the checkout pulled), and its message is still not what the row shows: the row says
            // "queued" because the pull request it read back is open.
            Assert.Equal(merged, HeadOf(root));
            Assert.Equal(Loc.Instance.Format(GitStrings.ShipQueuedNumber, Number), state.MessageText);
            Assert.NotEqual(
                Loc.Instance.Format(
                    GitStrings.ShipMergedWithFinale,
                    Number,
                    Loc.Instance.Format(GitStrings.ShipFinaleUpdated, "main", merged[..7])),
                state.MessageText);
            Assert.Equal(PullRequestPhase.Open, state.PullRequestPhase);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ the write discipline

    [Fact]
    public async Task The_merge_write_is_busy_and_disables_itself_until_the_platform_answers()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("ship-write");

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new ShipPlatform
        {
            Current = PullRequest(baseRefName: "main"),
            KeepOpenAfterMerge = true,
            Hold = hold,
        };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            // No project root: this test is about the write, not about what follows it.
            await AttachAndWaitAsync(session, panel, work, root: null);

            var outcome = await session.Dispatch(
                async () =>
                {
                    var before = new { panel.IsBusy, Can = panel.MergePullRequestCommand.CanExecute(null) };

                    panel.MergePullRequestCommand.Execute(null);
                    var during = new { panel.IsBusy, Can = panel.MergePullRequestCommand.CanExecute(null) };

                    // The second click a user makes while the first merge is still on the wire.
                    panel.MergePullRequestCommand.Execute(null);

                    hold.SetResult();
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return new
                    {
                        before,
                        during,
                        after = new { panel.IsBusy, Can = panel.MergePullRequestCommand.CanExecute(null) },
                    };
                },
                CancellationToken.None);

            Assert.False(outcome.before.IsBusy);
            Assert.True(outcome.before.Can);
            Assert.True(outcome.during.IsBusy);
            Assert.False(outcome.during.Can);
            Assert.False(outcome.after.IsBusy);
            Assert.True(outcome.after.Can);

            // Two clicks, one write on this checkout.
            var merge = Assert.Single(platform.Merges);
            Assert.Equal(Number, merge.Number);
            Assert.Equal(PrMergeMethod.Squash, merge.Method);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ 标记为可评审

    [Fact]
    public async Task A_draft_is_marked_ready_once_and_the_row_reads_the_pull_request_again()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("mark-ready");

        var platform = new ShipPlatform { Current = PullRequest(draft: true) };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root: null);
            var attached = await session.Dispatch(
                () => new
                {
                    panel.PullRequestIsDraft,
                    panel.CanMarkPullRequestReady,
                    Row = panel.ShipActions.First(action => action.Key == "ready").IsEnabled,
                },
                CancellationToken.None);
            Assert.True(attached.PullRequestIsDraft);
            Assert.True(attached.CanMarkPullRequestReady);
            Assert.True(attached.Row);

            var state = await session.Dispatch(
                async () =>
                {
                    panel.MarkPullRequestReadyCommand.Execute(null);
                    // The row publishes the re-read only once it has applied it, and that is what flips the pill.
                    await GitPanelTesting.WaitUntilAsync(
                        () => !panel.PullRequestIsDraft,
                        "the row to read the pull request as ready");
                    return new
                    {
                        panel.MessageText,
                        panel.MessageIsError,
                        panel.PullRequestIsDraft,
                        panel.PullRequestIsOpen,
                    };
                },
                CancellationToken.None);

            Assert.False(state.MessageIsError);
            Assert.Equal(Loc.Instance.Format(GitStrings.ShipReadyNumber, Number), state.MessageText);

            // The row read the pull request again after the platform took it out of draft, so the pill flips with it.
            Assert.False(state.PullRequestIsDraft);
            Assert.True(state.PullRequestIsOpen);
            Assert.Equal(2, platform.ViewCalls);

            var ready = Assert.Single(platform.Readies);
            Assert.Equal(Number, ready.Number);
            Assert.Equal(work, ready.Directory);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_refused_mark_ready_reports_the_platform_message_and_leaves_the_draft_pill_alone()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("mark-ready-refused");

        var platform = new ShipPlatform
        {
            Current = PullRequest(draft: true),
            ReadyResult = PrResult<PrReference>.Fail(
                GitRemoteFailureKind.Other,
                "gh: pull request #7 is not a draft",
                "not a draft"),
        };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root: null);

            var state = await session.Dispatch(
                async () =>
                {
                    panel.MarkPullRequestReadyCommand.Execute(null);
                    await GitPanelTesting.WaitUntilAsync(() => panel.HasResult, "the mark-ready result");
                    await GitPanelTesting.QuietWindowAsync();
                    return new { panel.MessageText, panel.MessageIsError, panel.PullRequestIsDraft };
                },
                CancellationToken.None);

            // The row reports the platform's own words and nothing about the pull request changes: the pill is still the
            // draft it read on attach, and the refusal was asked for exactly once.
            Assert.True(state.MessageIsError);
            Assert.Equal("gh: pull request #7 is not a draft", state.MessageText);
            Assert.True(state.PullRequestIsDraft);
            Assert.Single(platform.Readies);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Only_a_draft_pull_request_can_be_marked_ready()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("mark-ready-guard");

        var open = NewPanel(new ShipPlatform { Current = PullRequest(draft: false) });
        var none = NewPanel(new ShipPlatform { Current = null });
        var refused = NewPanel(new ShipPlatform
        {
            ViewResult = PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Network,
                "gh: could not resolve api.github.com"),
        });
        var session = SharedUi.Session;
        try
        {
            var state = await session.Dispatch(
                async () =>
                {
                    open.Attach(new PanelContext("open", work, null));
                    await GitPanelTesting.WaitIdleAsync(open);
                    none.Attach(new PanelContext("none", work, null));
                    await GitPanelTesting.WaitIdleAsync(none);
                    refused.Attach(new PanelContext("refused", work, null));
                    await GitPanelTesting.WaitIdleAsync(refused);
                    return new MarkReadyGuard(
                        open.PullRequestPhase,
                        open.CanMarkPullRequestReady,
                        open.MarkPullRequestReadyCommand.CanExecute(null),
                        open.ShipActions.First(action => action.Key == "ready").IsEnabled,
                        none.PullRequestPhase,
                        none.CanMarkPullRequestReady,
                        refused.PullRequestPhase,
                        refused.CanMarkPullRequestReady);
                },
                CancellationToken.None);

            // An open pull request has nothing to take out of draft.
            Assert.Equal(PullRequestPhase.Open, state.OpenPhase);
            Assert.False(state.OpenAllowed);
            Assert.False(state.OpenCommand);
            Assert.False(state.OpenRow);

            // No pull request at all.
            Assert.Equal(PullRequestPhase.None, state.NonePhase);
            Assert.False(state.NoneAllowed);

            // A failed read is its own state and never offered as "a draft is ready to be marked".
            Assert.Equal(PullRequestPhase.Unavailable, state.RefusedPhase);
            Assert.False(state.RefusedAllowed);
        }
        finally
        {
            open.Dispose();
            none.Dispose();
            refused.Dispose();
        }
    }

    // ------------------------------------------------------------ the writes release the row

    [Fact]
    public async Task Mark_ready_releases_the_row_and_only_re_reads_after_a_success()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("release-ready");
        var session = SharedUi.Session;

        // Accepted: the platform takes the draft out of draft, so the row reads the pull request again and is free after.
        var accepted = new ShipPlatform { Current = PullRequest(draft: true) };
        var panel = NewPanel(accepted);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root: null);

            var done = await session.Dispatch(
                async () =>
                {
                    panel.MarkPullRequestReadyCommand.Execute(null);
                    await GitPanelTesting.WaitUntilAsync(
                        () => !panel.PullRequestIsDraft,
                        "the row to read the pull request as ready");
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return new { panel.IsBusy, Readable = panel.RefreshCommand.CanExecute(null), Views = accepted.ViewCalls };
                },
                CancellationToken.None);

            // The write releases the row: busy would leave every other verb of this worktree disabled until a re-attach.
            Assert.False(done.IsBusy);
            Assert.True(done.Readable);
            Assert.Equal(2, done.Views);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }

        // Refused: nothing changed on the platform, so the pull request is not read a second time and the row still frees up.
        var refused = new ShipPlatform
        {
            Current = PullRequest(draft: true),
            ReadyResult = PrResult<PrReference>.Fail(GitRemoteFailureKind.Other, "gh: not a draft", "not a draft"),
        };
        var refusing = NewPanel(refused);
        try
        {
            await AttachAndWaitAsync(session, refusing, work, root: null);

            var done = await session.Dispatch(
                async () =>
                {
                    refusing.MarkPullRequestReadyCommand.Execute(null);
                    await GitPanelTesting.WaitUntilAsync(() => refusing.HasResult, "the refusal");
                    await GitPanelTesting.QuietWindowAsync();
                    await GitPanelTesting.WaitIdleAsync(refusing);
                    return new { refusing.IsBusy, refusing.MessageIsError, Views = refused.ViewCalls };
                },
                CancellationToken.None);

            Assert.False(done.IsBusy);
            Assert.True(done.MessageIsError);
            Assert.Equal(1, done.Views);
        }
        finally
        {
            await session.Dispatch(() => refusing.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Opening_a_pull_request_releases_the_row()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("release-compose");

        // No pull request for the branch yet: that is what makes 打开 PR applicable.
        var platform = new ShipPlatform { Current = null };
        var session = SharedUi.Session;
        var panel = NewComposingPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root: null);

            var done = await session.Dispatch(
                async () =>
                {
                    panel.ComposePullRequestCommand.Execute(null);
                    await GitPanelTesting.WaitUntilAsync(() => platform.CreateCalls == 1, "the pull request creation");
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return new { panel.IsBusy, Readable = panel.RefreshCommand.CanExecute(null), panel.MessageText };
                },
                CancellationToken.None);

            // The same discipline as the other ship writes: free afterwards, and the platform was asked exactly once.
            Assert.False(done.IsBusy);
            Assert.True(done.Readable);
            Assert.Equal(1, platform.CreateCalls);
            Assert.Contains("#91", done.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Deleting_the_remote_branch_releases_the_row()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("release-delete");
        var branch = "forge/session";
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", branch);
        GitTestShell.RunChecked(work, "push", "-q", "-u", "origin", branch);

        var platform = new ShipPlatform { Current = PullRequest() with { State = PrState.Merged } };
        var session = SharedUi.Session;
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work, root: null);

            var done = await session.Dispatch(
                async () =>
                {
                    var row = panel.ShipActions.Single(action => action.Key == "delete-remote");
                    var offered = row.IsEnabled;
                    row.Command.Execute(null);
                    await GitPanelTesting.WaitUntilAsync(() => panel.HasResult, "the delete result");
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return new { offered, panel.IsBusy, Readable = panel.RefreshCommand.CanExecute(null) };
                },
                CancellationToken.None);

            Assert.True(done.offered);
            Assert.False(done.IsBusy);
            Assert.True(done.Readable);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ the repositories

    /// <summary>
    /// The two checkouts a merge follow-up spans: a session directory whose branch is published (the row reads a pull
    /// request only for a branch with an upstream) and a separate root checkout of the same bare origin, sitting on
    /// <c>main</c> and tracking it.
    /// </summary>
    private static (string Work, string Root) SessionAndRoot(GitTestSandbox sandbox, string name)
    {
        var work = sandbox.CloneTracked(name + "-work");
        var root = sandbox.Clone(name + "-root");
        return (work, root);
    }

    /// <summary>
    /// The commit the platform's merge puts on <c>main</c>: committed in a third clone and pushed after the root checkout
    /// was made, so only a fetch can bring it into that checkout.
    /// </summary>
    private static string MergeLandsOnMain(GitTestSandbox sandbox, string name)
    {
        var platform = sandbox.Clone(name + "-platform");
        sandbox.CommitFile(platform, "merged.txt", "merged\n", "the merged commit");
        GitTestShell.RunChecked(platform, "push", "-q", "origin", "main");
        return HeadOf(platform);
    }

    private static string HeadOf(string repository) =>
        GitTestShell.RunChecked(repository, "rev-parse", "HEAD").Trim();

    private static string BranchOf(string repository) =>
        GitTestShell.RunChecked(repository, "rev-parse", "--abbrev-ref", "HEAD").Trim();

    private static string RefOf(string repository, string reference) =>
        GitTestShell.RunChecked(repository, "rev-parse", reference).Trim();

    // ------------------------------------------------------------ the row

    private static GitSyncViewModel NewPanel(ShipPlatform platform, GitPullStrategy strategy = GitPullStrategy.FastForwardOnly) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => strategy, () => TimeSpan.FromSeconds(30)),
            openFile: null,
            notifications: null,
            pullRequests: platform,
            composePullRequest: null,
            composeMerge: _ => Task.FromResult<PullRequestMergeChoice?>(
                new PullRequestMergeChoice(PrMergeMethod.Squash, DeleteBranch: false)),
            confirm: (_, _) => Task.FromResult(true));

    /// <summary>The same row with the pull-request dialog wired, so 打开 PR can be executed.</summary>
    private static GitSyncViewModel NewComposingPanel(ShipPlatform platform) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => GitPullStrategy.FastForwardOnly, () => TimeSpan.FromSeconds(30)),
            openFile: null,
            notifications: null,
            pullRequests: platform,
            composePullRequest: draft => Task.FromResult<PullRequestDraft?>(draft),
            composeMerge: _ => Task.FromResult<PullRequestMergeChoice?>(
                new PullRequestMergeChoice(PrMergeMethod.Squash, DeleteBranch: false)),
            confirm: (_, _) => Task.FromResult(true));

    /// <summary>A pull request of the session branch, as the scripted platform reports it.</summary>
    private static PullRequestInfo PullRequest(bool draft = false, string baseRefName = "main") =>
        new(
            Number,
            "https://example.test/acme/forge/pull/" + Number,
            PrState.Open,
            "Ship the finale",
            "Body",
            "main",
            baseRefName,
            "0123456789abcdef0123456789abcdef01234567",
            draft,
            PrMergeStateStatus.Clean,
            PrReviewDecision.None);

    private static Task AttachAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, string directory, string? root) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext("s1", directory, root));
                await GitPanelTesting.WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>Executes a command and waits for the operation plus its trailing refresh on the UI thread.</summary>
    private static Task ExecuteAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, ICommand command) =>
        session.Dispatch(
            async () =>
            {
                command.Execute(null);
                await GitPanelTesting.WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>What a row offers in 标记为可评审 for each pull-request state, read on the UI thread.</summary>
    private sealed record MarkReadyGuard(
        PullRequestPhase OpenPhase,
        bool OpenAllowed,
        bool OpenCommand,
        bool OpenRow,
        PullRequestPhase NonePhase,
        bool NoneAllowed,
        PullRequestPhase RefusedPhase,
        bool RefusedAllowed);

    /// <summary>
    /// The scripted pull request platform: it answers the availability check and the branch's pull request from
    /// <see cref="Current"/>, records every write, and throws for everything the row never calls. A successful merge turns
    /// the pull request into <see cref="PrState.Merged"/> unless <see cref="KeepOpenAfterMerge"/> pins a merge queue; a
    /// successful mark-ready clears the draft flag. <see cref="Hold"/> keeps the merge call in flight, so a test can look at
    /// the row mid-write instead of sleeping.
    /// </summary>
    private sealed class ShipPlatform : ThrowingPrPlatform, IPrPlatform
    {
        private readonly Lock gate = new();
        private readonly List<(string Directory, int Number, PrMergeMethod Method, bool DeleteBranch)> merges = [];
        private readonly List<(string Directory, int Number)> readies = [];
        private int views;
        private int creates;

        public override string Name => "ForgeHub";

        /// <summary>The pull request the branch has right now; null is "no pull request for this branch".</summary>
        public PullRequestInfo? Current { get; set; }

        /// <summary>Overrides the read, so a test can pin a failed one.</summary>
        public PrResult<PullRequestInfo>? ViewResult { get; set; }

        /// <summary>A merge queue: the merge succeeds and the pull request stays open.</summary>
        public bool KeepOpenAfterMerge { get; set; }

        /// <summary>What the merge answers; success unless a test pins a failure.</summary>
        public PrResult<PrReference>? MergeResult { get; set; }

        /// <summary>What mark-ready answers; success unless a test pins a failure.</summary>
        public PrResult<PrReference>? ReadyResult { get; set; }

        /// <summary>Completed by the test to end a held merge; null answers at once.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>How many times the branch's pull request was read (the attach read plus every re-read).</summary>
        public int ViewCalls => Volatile.Read(ref views);

        /// <summary>Every merge the row asked for, in order.</summary>
        public IReadOnlyList<(string Directory, int Number, PrMergeMethod Method, bool DeleteBranch)> Merges
        {
            get
            {
                lock (gate)
                {
                    return merges.ToArray();
                }
            }
        }

        /// <summary>Every mark-ready the row asked for, in order.</summary>
        public IReadOnlyList<(string Directory, int Number)> Readies
        {
            get
            {
                lock (gate)
                {
                    return readies.ToArray();
                }
            }
        }

        public override Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(new PrPlatformAvailability(PrPlatformState.Ready, "logged in"));

        public override Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(
            string workingDirectory,
            string? selector = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref views);
            if (ViewResult is { } scripted)
            {
                return Task.FromResult(scripted);
            }

            return Task.FromResult(Current is { } current
                ? PrResult<PullRequestInfo>.Ok(current)
                : PrResult<PullRequestInfo>.Fail(
                    GitRemoteFailureKind.Other,
                    "no pull requests found",
                    reason: PrFailureReason.PullRequestNotFound));
        }

        public async Task<PrResult<PrReference>> MergePullRequestAsync(
            string workingDirectory,
            int number,
            PrMergeMethod method,
            bool deleteBranch = false,
            CancellationToken ct = default)
        {
            lock (gate)
            {
                merges.Add((workingDirectory, number, method, deleteBranch));
            }

            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }

            var answer = MergeResult ?? PrResult<PrReference>.Ok(new PrReference(number, UrlOf(number)));
            lock (gate)
            {
                if (answer.Success && !KeepOpenAfterMerge && Current is { } current)
                {
                    Current = current with
                    {
                        State = PrState.Merged,
                        IsDraft = false,
                        MergeStateStatus = PrMergeStateStatus.Unknown,
                    };
                }
            }

            return answer;
        }

        public Task<PrResult<PrReference>> MarkPullRequestReadyAsync(
            string workingDirectory,
            int number,
            CancellationToken ct = default)
        {
            lock (gate)
            {
                readies.Add((workingDirectory, number));
                if (ReadyResult is null && Current is { } current)
                {
                    Current = current with { IsDraft = false };
                }
            }

            return Task.FromResult(ReadyResult ?? PrResult<PrReference>.Ok(new PrReference(number, UrlOf(number))));
        }

        /// <summary>What 打开 PR answers; null throws, so a test that never opens one cannot pass by accident.</summary>
        public PrResult<PrReference>? CreateResult { get; set; }

        /// <summary>How many pull requests the row asked to create.</summary>
        public int CreateCalls => Volatile.Read(ref creates);

        public override Task<PrResult<PrReference>> CreatePullRequestAsync(
            string workingDirectory,
            string title,
            string body,
            string baseBranch,
            string headBranch,
            bool draft = false,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref creates);
            return Task.FromResult(CreateResult ?? PrResult<PrReference>.Ok(new PrReference(91, UrlOf(91))));
        }

        private static string UrlOf(int number) => "https://example.test/acme/forge/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
