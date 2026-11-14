using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="GitSyncViewModel"/> (the files panel's git row): attach to a real tracked repo shows branch, upstream
/// and ahead/behind; a plain directory or an empty context clears the row; a newer attach wins over an in-flight read;
/// commands are disabled while busy and only one operation runs at a time; fetch/pull/push, diverged/conflict handling,
/// the fetch-failure state, dispose, turn-completed refresh, the row's text/enablement contract, and the files panel's
/// rendered row (branch label, each count on its own verb). Every wait polls <c>IsBusy == false</c> with a 30 s bound —
/// no <see cref="Thread.Sleep"/> timing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSyncViewModelTests
{
    // ------------------------------------------------------------ attach and cleared states

    [Fact]
    public async Task Attach_to_a_tracked_repo_shows_branch_upstream_aheadbehind_remotes_and_branches()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasRepository,
                    panel.BranchLabel,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                },
                CancellationToken.None);

            Assert.True(state.HasRepository);
            Assert.Equal("main", state.BranchLabel);
            Assert.Equal("origin/main", state.UpstreamText);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), state.AheadBehindText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Attach_with_an_empty_context_clears_and_disables_everything()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            Assert.True(await session.Dispatch(() => panel.HasRepository, CancellationToken.None));

            await session.Dispatch(
                async () =>
                {
                    panel.Attach(PanelContext.Empty);
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasRepository,
                    panel.BranchLabel,
                    Refresh = panel.RefreshCommand.CanExecute(null),
                    Fetch = panel.FetchCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.False(state.HasRepository);
            Assert.Equal(string.Empty, state.BranchLabel);
            Assert.False(state.Refresh);
            Assert.False(state.Fetch);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task The_newer_attach_wins_over_an_in_flight_read()
    {
        using var sandbox = GitTestSandbox.Create();
        var repoA = sandbox.CloneTracked("a");
        GitTestShell.RunChecked(repoA, "checkout", "-q", "-b", "alpha");
        var repoB = sandbox.Clone("b");
        GitTestShell.RunChecked(repoB, "checkout", "-q", "-b", "beta");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            // Both attaches land synchronously on the dispatcher before either read can finish.
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s-a", repoA, null));
                    panel.Attach(new PanelContext("s-b", repoB, null));
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            Assert.Equal("beta", await session.Dispatch(() => panel.BranchLabel, CancellationToken.None));

            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s-b", repoB, null));
                    panel.Attach(new PanelContext("s-a", repoA, null));
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            Assert.Equal("alpha", await session.Dispatch(() => panel.BranchLabel, CancellationToken.None));
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Commands_are_disabled_while_busy_and_only_one_operation_runs()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var outcome = await session.Dispatch(
                async () =>
                {
                    panel.FetchCommand.Execute(null);
                    panel.PullCommand.Execute(null);
                    var busy = new
                    {
                        panel.IsBusy,
                        Fetch = panel.FetchCommand.CanExecute(null),
                        Pull = panel.PullCommand.CanExecute(null),
                        Push = panel.PushCommand.CanExecute(null),
                    };
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return new
                    {
                        Busy = busy,
                        Idle = new
                        {
                            panel.IsBusy,
                            Fetch = panel.FetchCommand.CanExecute(null),
                            panel.AheadBehindText,
                            panel.MessageIsError,
                        },
                    };
                },
                CancellationToken.None);

            Assert.True(outcome.Busy.IsBusy);
            Assert.False(outcome.Busy.Fetch);
            Assert.False(outcome.Busy.Pull);
            Assert.False(outcome.Busy.Push);
            Assert.False(outcome.Idle.IsBusy);
            Assert.True(outcome.Idle.Fetch);
            Assert.False(outcome.Idle.MessageIsError);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), outcome.Idle.AheadBehindText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ fetch / pull / push

    [Fact]
    public async Task Push_moves_ahead_commits_and_fetch_pull_track_the_remote()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            sandbox.CommitFile(work, "file.txt", "v2\n", "second");
            await AttachAndWaitAsync(session, panel, "s1", work);
            Assert.Equal(
                Loc.Instance.Format(GitStrings.AheadBehind, 1, 0),
                await session.Dispatch(() => panel.AheadBehindText, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.PushCommand);

            var pushed = await session.Dispatch(
                () => new { panel.AheadBehindText, panel.MessageIsError, panel.HasResult },
                CancellationToken.None);
            Assert.False(pushed.MessageIsError);
            Assert.True(pushed.HasResult);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), pushed.AheadBehindText);

            // A second clone moves the remote forward; fetch shows behind, pull catches up.
            var other = sandbox.Clone("other");
        sandbox.CommitAndPush(other, "file.txt", "v3\n", "third");

            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);
            Assert.Equal(
                Loc.Instance.Format(GitStrings.AheadBehind, 0, 1),
                await session.Dispatch(() => panel.AheadBehindText, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.PullCommand);

            var pulled = await session.Dispatch(
                () => new { panel.AheadBehindText, panel.MessageIsError },
                CancellationToken.None);
            Assert.False(pulled.MessageIsError);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), pulled.AheadBehindText);
            Assert.Equal("v3\n", await File.ReadAllTextAsync(Path.Combine(work, "file.txt")));
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Push_without_an_upstream_sets_it_on_the_first_push()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "feature");
        sandbox.CommitFile(work, "feature.txt", "new\n", "feature work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            Assert.Equal(
                Loc.Instance[GitStrings.NoUpstream],
                await session.Dispatch(() => panel.UpstreamText, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.PushCommand);

            var state = await session.Dispatch(
                () => new { panel.UpstreamText, panel.AheadBehindText, panel.MessageIsError },
                CancellationToken.None);

            Assert.False(state.MessageIsError);
            Assert.Equal("origin/feature", state.UpstreamText);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), state.AheadBehindText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Pull_on_a_diverged_branch_with_ff_only_reports_an_error_and_keeps_head()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var other = sandbox.Clone("other");
        sandbox.CommitAndPush(other, "file.txt", "remote\n", "remote change");

        sandbox.CommitFile(work, "file.txt", "local\n", "local change");
        var headBefore = GitTestShell.RunChecked(work, "rev-parse", "HEAD").Trim();

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            Assert.Equal(
                Loc.Instance.Format(GitStrings.AheadBehind, 1, 0),
                await session.Dispatch(() => panel.AheadBehindText, CancellationToken.None));

            // A fetch learns the remote side, so the panel now reports the divergence.
            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);
            Assert.Equal(
                Loc.Instance.Format(GitStrings.AheadBehind, 1, 1),
                await session.Dispatch(() => panel.AheadBehindText, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.PullCommand);

            var state = await session.Dispatch(
                () => new
                {
                    panel.MessageIsError,
                    panel.HintText,
                    panel.HasHint,
                    panel.BranchLabel,
                },
                CancellationToken.None);

            Assert.True(state.MessageIsError);
            Assert.True(state.HasHint);
            Assert.False(string.IsNullOrWhiteSpace(state.HintText));
            Assert.Equal("main", state.BranchLabel);
            Assert.Equal(headBefore, GitTestShell.RunChecked(work, "rev-parse", "HEAD").Trim());
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Pull_conflict_with_the_rebase_strategy_lists_files_and_abort_clears_it()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var other = sandbox.Clone("other");
        sandbox.CommitAndPush(other, "file.txt", "remote\n", "remote change");
        sandbox.CommitFile(work, "file.txt", "local\n", "local change");

        var session = SharedUi.Session;
        var panel = NewPanel(strategy: GitPullStrategy.Rebase);
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            await ExecuteAndWaitAsync(session, panel, panel.PullCommand);

            var conflict = await session.Dispatch(
                () => new
                {
                    panel.MessageIsError,
                    panel.OperationText,
                    panel.HasOperation,
                    Files = panel.ConflictedFiles.ToList(),
                },
                CancellationToken.None);

            Assert.True(conflict.MessageIsError);
            Assert.False(string.IsNullOrWhiteSpace(conflict.OperationText));
            Assert.True(conflict.HasOperation);
            Assert.Contains(conflict.Files, file => file.Path == "file.txt");

            await ExecuteAndWaitAsync(session, panel, panel.AbortOperationCommand);

            var cleared = await session.Dispatch(
                () => new
                {
                    panel.OperationText,
                    panel.HasOperation,
                    Files = panel.ConflictedFiles.ToList(),
                },
                CancellationToken.None);

            Assert.Equal(string.Empty, cleared.OperationText);
            Assert.False(cleared.HasOperation);
            Assert.Empty(cleared.Files);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ branches

    // ------------------------------------------------------------ failure, dispose, refresh triggers

    [Fact]
    public async Task Fetch_failure_keeps_the_previous_state_and_shows_the_localized_hint()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            var before = await session.Dispatch(
                () => new
                {
                    panel.BranchLabel,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                },
                CancellationToken.None);

            GitTestShell.RunChecked(work, "remote", "set-url", "origin", Path.Combine(sandbox.Root, "gone.git"));
            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);

            var after = await session.Dispatch(
                () => new
                {
                    panel.BranchLabel,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                    panel.MessageIsError,
                    panel.HintText,
                    panel.HasHint,
                },
                CancellationToken.None);

            Assert.True(after.MessageIsError);
            Assert.True(after.HasHint);
            Assert.Equal(Loc.Instance[GitStrings.HintRemoteNotFound], after.HintText);
            Assert.Equal(before.BranchLabel, after.BranchLabel);
            Assert.Equal(before.UpstreamText, after.UpstreamText);
            Assert.Equal(before.AheadBehindText, after.AheadBehindText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_work_and_ignores_later_calls()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            // The attach starts a read; disposing on the same dispatch cancels it without throwing.
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s1", work, null));
                    panel.Dispose();
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            // Later attaches and commands are no-ops: no throw, no busy, nothing shown.
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s2", work, null));
                    panel.FetchCommand.Execute(null);
                    panel.RefreshCommand.Execute(null);
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            var state = await session.Dispatch(
                () => new { panel.IsBusy, panel.HasRepository, panel.BranchLabel },
                CancellationToken.None);

            Assert.False(state.IsBusy);
            Assert.False(state.HasRepository);
            Assert.Equal(string.Empty, state.BranchLabel);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task NotifyTurnCompleted_refreshes_only_the_attached_session()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            GitTestShell.RunChecked(work, "checkout", "-q", "-b", "external-x");

            // A turn of another session does nothing: no refresh, no busy, no new branch.
            await session.Dispatch(() => { panel.NotifyTurnCompleted("other"); return 0; }, CancellationToken.None);
            var untouched = await session.Dispatch(
                () => new { panel.IsBusy, Branch = panel.BranchLabel },
                CancellationToken.None);
            Assert.False(untouched.IsBusy);
            Assert.NotEqual("external-x", untouched.Branch);

            // A turn of the attached session re-reads the state.
            var refreshed = await session.Dispatch(
                async () =>
                {
                    panel.NotifyTurnCompleted("s1");
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return panel.BranchLabel;
                },
                CancellationToken.None);
            Assert.Equal("external-x", refreshed);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ harness

    [Fact]
    public async Task The_ship_row_offers_to_open_a_pull_request_once_the_branch_is_published()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("ship-none");

        var platform = new StubPullRequestPlatform(PrResult<PullRequestInfo>.Fail(
            GitRemoteFailureKind.Other, "no pull request", reason: PrFailureReason.PullRequestNotFound));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(pullRequests: platform, composePullRequest: _ => Task.FromResult<PullRequestDraft?>(null));
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.PullRequestPhase,
                    panel.CanComposePullRequest,
                    panel.HasPullRequestState,
                    Label = panel.PullRequestLabel,
                    Compose = panel.ShipActions[0],
                    Browser = panel.ShipActions[1],
                    panel.ShowPublish,
                },
                CancellationToken.None);

            Assert.Equal(PullRequestPhase.None, state.PullRequestPhase);
            Assert.True(state.CanComposePullRequest);
            Assert.False(state.HasPullRequestState);
            Assert.Equal(Loc.Instance[GitStrings.ShipTarget], state.Label);
            Assert.Equal("compose", state.Compose.Key);
            Assert.True(state.Compose.IsEnabled);
            Assert.False(state.Browser.IsEnabled);
            Assert.Equal(Loc.Instance[GitStrings.ShipNoPullRequest], state.Browser.DisabledReason);

            // Published already: the publish pill is not shown, the branch verb lives on the git row above.
            Assert.False(state.ShowPublish);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_open_pull_request_becomes_the_button_state_and_blocks_a_second_one()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("ship-open");

        var platform = new StubPullRequestPlatform(PrResult<PullRequestInfo>.Ok(
            new PullRequestInfo(12, "https://example.test/pr/12", PrState.Open, "t", "b", "main", "main", "sha", false,
                PrMergeStateStatus.Clean, PrReviewDecision.None)));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(pullRequests: platform);
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.PullRequestPhase,
                    panel.PullRequestLabel,
                    panel.PullRequestNumberText,
                    panel.PullRequestIsOpen,
                    Url = panel.PullRequestUrl,
                    Compose = panel.ShipActions[0],
                    Browser = panel.ShipActions[1],
                },
                CancellationToken.None);

            Assert.Equal(PullRequestPhase.Open, state.PullRequestPhase);
            Assert.Equal(Loc.Instance[GitStrings.PrStateOpen], state.PullRequestLabel);
            Assert.Equal("#12", state.PullRequestNumberText);
            Assert.True(state.PullRequestIsOpen);
            Assert.Equal("https://example.test/pr/12", state.Url);
            Assert.False(state.Compose.IsEnabled);
            Assert.Equal(Loc.Instance[GitStrings.ShipAlreadyExists], state.Compose.DisabledReason);
            Assert.True(state.Browser.IsEnabled);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Gh_not_ready_is_its_own_state_and_never_looks_like_no_pull_request()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("ship-gh");

        var platform = new StubPullRequestPlatform(PrResult<PullRequestInfo>.Ok(
            new PullRequestInfo(1, "u", PrState.Open, "t", "b", "main", "main", "sha", false,
                PrMergeStateStatus.Clean, PrReviewDecision.None)))
        {
            Availability = new PrPlatformAvailability(PrPlatformState.NotAuthenticated, "run gh auth login"),
        };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(pullRequests: platform);
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.PullRequestPhase,
                    panel.HasPullRequestProblem,
                    Problem = panel.PullRequestProblemText,
                    panel.CanComposePullRequest,
                    Compose = panel.ShipActions[0],
                },
                CancellationToken.None);

            Assert.Equal(PullRequestPhase.Unavailable, state.PullRequestPhase);
            Assert.True(state.HasPullRequestProblem);
            Assert.Equal("run gh auth login", state.Problem);
            Assert.False(state.CanComposePullRequest);
            Assert.Equal("run gh auth login", state.Compose.DisabledReason);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Merging_into_the_default_branch_refuses_a_dirty_root_checkout()
    {
        using var sandbox = GitTestSandbox.Create();
        var (root, sessionDirectory) = MergeFixture(sandbox, "dirty-root");
        File.WriteAllText(Path.Combine(root, "root-dirty.txt"), "not committed");

        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(confirm: (_, _) => Task.FromResult(true));
        try
        {
            await ui.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s1", sessionDirectory, root));
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            await ExecuteAndWaitAsync(ui, panel, panel.MergeLocallyCommand);

            var state = await ui.Dispatch(
                () => new { panel.MessageIsError, Message = panel.MessageText },
                CancellationToken.None);

            Assert.True(state.MessageIsError);
            Assert.Contains("未提交", state.Message);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Merging_into_the_default_branch_refuses_a_root_checkout_on_another_branch()
    {
        using var sandbox = GitTestSandbox.Create();
        var (root, sessionDirectory) = MergeFixture(sandbox, "wrong-checkout");
        GitTestShell.RunChecked(root, "checkout", "-q", "-b", "somewhere-else");

        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(confirm: (_, _) => Task.FromResult(true));
        try
        {
            await ui.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s1", sessionDirectory, root));
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            await ExecuteAndWaitAsync(ui, panel, panel.MergeLocallyCommand);

            var state = await ui.Dispatch(
                () => new { panel.MessageIsError, Message = panel.MessageText },
                CancellationToken.None);

            Assert.True(state.MessageIsError);
            Assert.Contains("somewhere-else", state.Message);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_pull_request_dialog_opens_with_the_description_the_plan_can_already_write()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("pr-prefill");

        PullRequestDraft? seen = null;
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(
            pullRequests: new StubPullRequestPlatform(PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other, "none", reason: PrFailureReason.PullRequestNotFound)),
            composePullRequest: draft =>
            {
                seen = draft;
                return Task.FromResult<PullRequestDraft?>(null);
            },
            describePullRequest: () => Task.FromResult<string?>("## Summary\n" + "planned work"));
        try
        {
            await AttachAndWaitAsync(ui, panel, "s1", work);
            await ExecuteAndWaitAsync(ui, panel, panel.ComposePullRequestCommand);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }

        Assert.NotNull(seen);
        Assert.Equal("main", seen!.Title);
        Assert.Contains("planned work", seen.Body);
        Assert.False(seen.Draft);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge.git", "https://github.com/acme/forge")]
    [InlineData("https://github.com/acme/forge", "https://github.com/acme/forge")]
    [InlineData("git@github.com:acme/forge.git", "https://github.com/acme/forge")]
    [InlineData("ssh://git@gitlab.com/acme/forge.git", null)]
    [InlineData("/srv/git/forge.git", null)]
    [InlineData("", null)]
    public void A_remote_url_becomes_a_web_url_only_when_the_page_shape_is_known(string remote, string? expected)
    {
        Assert.Equal(expected, GitSyncViewModel.WebUrlOf(remote));
    }

    [Fact]
    public async Task The_remote_readout_tracks_published_pending_and_synced()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.Clone("readout");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");

        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(ui, panel, "s1", work);

            var unpublished = await ui.Dispatch(() => panel.RemoteStateText, CancellationToken.None);
            Assert.Equal(Loc.Instance[GitStrings.RemoteNotPublished], unpublished);

            // Publish, then read again: nothing is waiting, so the row says it is in sync.
            await ExecuteAndWaitAsync(ui, panel, panel.PushCommand);
            var synced = await ui.Dispatch(() => panel.RemoteStateText, CancellationToken.None);
            Assert.Equal(Loc.Instance[GitStrings.RemoteInSync], synced);

            // One local commit: the row says how much is waiting to go out.
            sandbox.CommitFile(work, "second.txt", "v2\n", "second");
            await ui.Dispatch(
                async () =>
                {
                    panel.NotifyTurnCompleted("s1");
                    await GitPanelTesting.WaitIdleAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            var pending = await ui.Dispatch(() => panel.RemoteStateText, CancellationToken.None);
            Assert.Equal(Loc.Instance.Format(GitStrings.RemoteToPush, 1), pending);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pushing_to_the_shared_branch_asks_first_and_declining_pushes_nothing()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.Clone("shared-push");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");

        var asked = 0;
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(confirm: (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        });
        try
        {
            await AttachAndWaitAsync(ui, panel, "s1", work);

            // The clone is on main, which is this repository's default branch: pushing it would skip any review.
            await ExecuteAndWaitAsync(ui, panel, panel.PushCommand);

            var state = await ui.Dispatch(
                () => new { panel.HasUpstream, panel.HasResult },
                CancellationToken.None);

            Assert.Equal(1, asked);
            Assert.False(state.HasUpstream);
            Assert.False(state.HasResult);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pushing_a_feature_branch_does_not_ask()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.Clone("feature-push");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "feature-x");

        var asked = 0;
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(confirm: (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        });
        try
        {
            await AttachAndWaitAsync(ui, panel, "s1", work);
            await ExecuteAndWaitAsync(ui, panel, panel.PushCommand);

            var state = await ui.Dispatch(() => new { panel.HasUpstream, panel.Ahead }, CancellationToken.None);

            Assert.Equal(0, asked);
            Assert.True(state.HasUpstream);
            Assert.Equal(0, state.Ahead);
        }
        finally
        {
            await ui.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_merge_pull_request_row_follows_the_platform_merge_state()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("merge-state");

        var platform = new StubPullRequestPlatform(PrResult<PullRequestInfo>.Ok(
            new PullRequestInfo(7, "u", PrState.Open, "t", "b", "main", "main", "sha", false,
                PrMergeStateStatus.Blocked, PrReviewDecision.ReviewRequired)));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(
            pullRequests: platform,
            composeMerge: _ => Task.FromResult<PullRequestMergeChoice?>(null),
            confirm: (_, _) => Task.FromResult(false));
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var blocked = await session.Dispatch(
                () => new { panel.CanMergePullRequest, Row = panel.ShipActions.First(action => action.Key == "merge-pr") },
                CancellationToken.None);
            Assert.False(blocked.CanMergePullRequest);
            Assert.False(blocked.Row.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(blocked.Row.DisabledReason));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Refresh_requests_raised_during_a_read_collapse_into_one_more_read()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneWithPushedCommit("row-queue", "file.txt", "v1" + (char)10, "initial");
        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var applies = await session.Dispatch(
                async () =>
                {
                    var applied = 0;
                    void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(GitSyncViewModel.HasRepository))
                        {
                            applied++;
                        }
                    }

                    panel.PropertyChanged += OnChanged;
                    try
                    {
                        // Two turn completions while the first read is in flight: one slot, one extra read.
                        panel.NotifyTurnCompleted("s1");
                        panel.NotifyTurnCompleted("s1");
                        await GitPanelTesting.WaitIdleAsync(panel);
                        return applied;
                    }
                    finally
                    {
                        panel.PropertyChanged -= OnChanged;
                    }
                },
                CancellationToken.None);

            Assert.Equal(2, applies);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_files_panel_row_puts_the_branch_and_each_count_on_its_own_verb()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("row-view");
        var other = sandbox.Clone("row-view-other");
        sandbox.CommitAndPush(other, "remote.txt", "remote work\n", "remote work");

        var session = SharedUi.Session;
        var git = NewPanel();
        var files = new FilesPanelViewModel(
            new GitCli(),
            _ => { },
            _ => { },
            _ => { },
            lineCountsFactory: null,
            changeReview: null,
            gitSync: git);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    var context = new PanelContext("s1", work, null);
                    git.Attach(context);
                    files.Attach(context);
                    await GitPanelTesting.WaitIdleAsync(git);
                    return 0;
                },
                CancellationToken.None);
            await ExecuteAndWaitAsync(session, git, git.FetchCommand);

            var rendered = await session.Dispatch(
                () =>
                {
                    var window = new Window
                    {
                        Width = 275,
                        Height = 480,
                        Content = new FilesToolView { DataContext = files },
                    };
                    window.Show();
                    SharedUi.Settle(window);
                    try
                    {
                        return new
                        {
                            Row = ControlOf<Border>(window, "git-status-row") is not null,
                            Branch = TextOf(window, "git-branch"),
                            Fetch = ControlOf<Button>(window, "git-fetch") is not null,
                            Pull = ControlOf<Button>(window, "git-pull") is not null,
                            Push = ControlOf<Button>(window, "git-push") is not null,
                            PullCount = VisibleTextOf(window, "git-pull"),
                            PushCount = VisibleTextOf(window, "git-push"),
                        };
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

            Assert.True(rendered.Row);
            Assert.Equal("main", rendered.Branch);
            Assert.True(rendered.Fetch);
            Assert.True(rendered.Pull);
            Assert.True(rendered.Push);

            // One commit is waiting on the remote: the number rides on pull, and push draws nothing at all.
            Assert.Equal("1", rendered.PullCount);
            Assert.Equal(string.Empty, rendered.PushCount);
        }
        finally
        {
            await session.Dispatch(() => { files.Dispose(); git.Dispose(); return 0; }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_re_read_outranks_the_stale_result_of_the_last_write()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("row-stale");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);

            var afterFetch = await session.Dispatch(
                () => new { panel.HasResult, panel.HasRefreshProblem, panel.MessageText, panel.LastResultText },
                CancellationToken.None);
            Assert.True(afterFetch.HasResult);
            Assert.False(afterFetch.HasRefreshProblem);
            Assert.Equal(afterFetch.LastResultText, afterFetch.MessageText);

            // Break the repository so the next read fails: the row must report the read problem, not the stale success.
            File.WriteAllText(Path.Combine(work, ".git", "index"), "this is not an index file");
            await ExecuteAndWaitAsync(session, panel, panel.RefreshCommand);

            var broken = await session.Dispatch(
                () => new { panel.HasRefreshProblem, panel.MessageIsError, panel.MessageText, panel.LastResultText },
                CancellationToken.None);
            Assert.True(broken.HasRefreshProblem);
            Assert.True(broken.MessageIsError);
            Assert.NotEqual(broken.LastResultText, broken.MessageText);
            Assert.False(string.IsNullOrWhiteSpace(broken.MessageText));
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_row_enables_every_verb_even_when_both_numbers_are_zero()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("row-sync");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasAhead,
                    panel.HasBehind,
                    Fetch = panel.FetchCommand.CanExecute(null),
                    Pull = panel.PullCommand.CanExecute(null),
                    Push = panel.PushCommand.CanExecute(null),
                },
                CancellationToken.None);

            // Ahead/behind only reflect the last fetch, so they never gate a verb: pulling is the one way to learn them.
            Assert.False(state.HasAhead);
            Assert.False(state.HasBehind);
            Assert.True(state.Fetch);
            Assert.True(state.Pull);
            Assert.True(state.Push);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Fetching_turns_the_behind_count_into_the_pull_button_number()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("row-counts");
        var other = sandbox.Clone("row-counts-other");
        sandbox.CommitFile(other, "remote-a.txt", "a\n", "remote a");
        sandbox.CommitFile(other, "remote-b.txt", "b\n", "remote b");
        GitTestShell.RunChecked(other, "push", "-q", "origin", "main");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);

            var state = await session.Dispatch(
                () => new { panel.Ahead, panel.Behind, panel.BehindText, panel.HasBehind, panel.PullTooltip },
                CancellationToken.None);

            Assert.Equal(0, state.Ahead);
            Assert.Equal(2, state.Behind);
            Assert.Equal("2", state.BehindText);
            Assert.True(state.HasBehind);
            Assert.Contains("2", state.PullTooltip);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_branch_tooltip_names_the_push_target_and_the_upstream_side_by_side()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("row-tooltip");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            var tooltip = await session.Dispatch(
                () => new { panel.BranchTooltip, panel.PushTooltip, panel.RowAutomationName },
                CancellationToken.None);

            // The refspec is name-matched; the upstream need not be. The tooltip is the only place both are visible.
            // The refspec is name-matched, so the push target is the branch name; naming a remote would be a guess
            // (GitCli.ResolvePushRemoteAsync, not this row, decides it).
            Assert.Contains("同名远端分支 main", tooltip.BranchTooltip);
            Assert.Contains("同名远端分支 main", tooltip.PushTooltip);
            Assert.Contains("origin/main", tooltip.BranchTooltip);
            Assert.Contains("main", tooltip.RowAutomationName);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    private static GitSyncViewModel NewPanel(
        GitPullStrategy strategy = GitPullStrategy.FastForwardOnly,
        IPrPlatform? pullRequests = null,
        Func<PullRequestDraft, Task<PullRequestDraft?>>? composePullRequest = null,
        Func<PullRequestMergeRequest, Task<PullRequestMergeChoice?>>? composeMerge = null,
        Func<string, string, Task<bool>>? confirm = null,
        Func<Task<string?>>? describePullRequest = null) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => strategy, () => TimeSpan.FromSeconds(30)),
            openFile: null,
            notifications: null,
            pullRequests: pullRequests,
            composePullRequest: composePullRequest,
            composeMerge: composeMerge,
            confirm: confirm,
            describePullRequest: describePullRequest);

    /// <summary>
    /// Attaches and waits for the trailing refresh inside one UI dispatch: the panel's continuations only
    /// drain while the dispatcher yields, so the poll must await on the UI thread itself.
    /// </summary>
    private static Task AttachAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, string sessionId, string directory) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext(sessionId, directory, null));
                await GitPanelTesting.WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>Executes a command and waits for the operation plus its trailing refresh on the UI thread.</summary>
    private static Task ExecuteAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, System.Windows.Input.ICommand command) =>
        session.Dispatch(
            async () =>
            {
                command.Execute(null);
                await GitPanelTesting.WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>
    /// The visible text inside one of the row's verb buttons: the count that rides on pull/push. A hidden count is not
    /// part of it, which is exactly the "0 draws nothing" rule.
    /// </summary>
    private static string VisibleTextOf(Window window, string buttonAutomationId) =>
        ControlOf<Button>(window, buttonAutomationId) is { } button
            ? string.Concat(button.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text))
                .Select(text => text.Text))
            : string.Empty;


    private static T? ControlOf<T>(Window window, string automationId)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control =>
                string.Equals(Avalonia.Automation.AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private static string? TextOf(Window window, string automationId) =>
        ControlOf<TextBlock>(window, automationId)?.Text;

    /// <summary>A bare <c>origin.git</c> plus clones with pinned local identity.</summary>
    /// <summary>
    /// The topology the local merge assumes: a root checkout on <c>main</c> and a session directory that is a linked
    /// worktree of it (refs shared, like a Forge session), with one commit on the session branch.
    /// </summary>
    private static (string Root, string Session) MergeFixture(GitTestSandbox sandbox, string name)
    {
        var seed = sandbox.Clone(name + "-seed");
        sandbox.CommitFile(seed, "readme.txt", "seed\n", "seed");
        GitTestShell.RunChecked(seed, "push", "-q", "-u", "origin", "main");

        var root = sandbox.Clone(name + "-root");
        var session = Path.Combine(sandbox.Root, name + "-session");
        GitTestShell.RunChecked(root, "worktree", "add", "-q", "-b", "session-x", session);
        sandbox.CommitFile(session, "feature.txt", "f\n", "feature");
        return (root, session);
    }

    /// <summary>
    /// The smallest pull-request platform the ship row talks to: availability and the branch's pull request are answered,
    /// every write throws so a test that accidentally creates something fails loudly.
    /// </summary>
    private sealed class StubPullRequestPlatform(PrResult<PullRequestInfo> pullRequest) : ThrowingPrPlatform
    {
        public override string Name => "Stub";

        public PrPlatformAvailability Availability { get; set; } = new(PrPlatformState.Ready, string.Empty);

        public override Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(Availability);

        public override Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
            Task.FromResult(pullRequest);
    }

}
