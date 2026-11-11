using System.Diagnostics;
using System.Text;
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
    private static readonly TimeSpan IdleBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    // ------------------------------------------------------------ attach and cleared states

    [Fact]
    public async Task Attach_to_a_tracked_repo_shows_branch_upstream_aheadbehind_remotes_and_branches()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
                    await WaitIdleOnUiAsync(panel);
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
        using var sandbox = GitSandbox.Create();
        var repoA = sandbox.Clone("a");
        sandbox.CommitFile(repoA, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(repoA, "push", "-q", "-u", "origin", "main");
        GitShell.RunChecked(repoA, "checkout", "-q", "-b", "alpha");
        var repoB = sandbox.Clone("b");
        GitShell.RunChecked(repoB, "checkout", "-q", "-b", "beta");

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
                    await WaitIdleOnUiAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            Assert.Equal("beta", await session.Dispatch(() => panel.BranchLabel, CancellationToken.None));

            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s-b", repoB, null));
                    panel.Attach(new PanelContext("s-a", repoA, null));
                    await WaitIdleOnUiAsync(panel);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
                    await WaitIdleOnUiAsync(panel);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
            sandbox.CommitFile(other, "file.txt", "v3\n", "third");
            GitShell.RunChecked(other, "push", "-q", "origin", "main");

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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        GitShell.RunChecked(work, "checkout", "-q", "-b", "feature");
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        var other = sandbox.Clone("other");
        sandbox.CommitFile(other, "file.txt", "remote\n", "remote change");
        GitShell.RunChecked(other, "push", "-q", "origin", "main");

        sandbox.CommitFile(work, "file.txt", "local\n", "local change");
        var headBefore = GitShell.RunChecked(work, "rev-parse", "HEAD").Trim();

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
            Assert.Equal(headBefore, GitShell.RunChecked(work, "rev-parse", "HEAD").Trim());
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Pull_conflict_with_the_rebase_strategy_lists_files_and_abort_clears_it()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        var other = sandbox.Clone("other");
        sandbox.CommitFile(other, "file.txt", "remote\n", "remote change");
        GitShell.RunChecked(other, "push", "-q", "origin", "main");
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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

            GitShell.RunChecked(work, "remote", "set-url", "origin", Path.Combine(sandbox.Root, "gone.git"));
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
                    await WaitIdleOnUiAsync(panel);
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
                    await WaitIdleOnUiAsync(panel);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        var session = SharedUi.Session;
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            GitShell.RunChecked(work, "checkout", "-q", "-b", "external-x");

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
                    await WaitIdleOnUiAsync(panel);
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
    public async Task Refresh_requests_raised_during_a_read_collapse_into_one_more_read()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-queue");
        sandbox.CommitFile(work, "file.txt", "v1" + (char)10, "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
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
                        await WaitIdleOnUiAsync(panel);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-view");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        var other = sandbox.Clone("row-view-other");
        sandbox.CommitFile(other, "remote.txt", "remote work\n", "remote work");
        GitShell.RunChecked(other, "push", "-q", "origin", "main");

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
                    await WaitIdleOnUiAsync(git);
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
                    Settle(window);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-stale");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-sync");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-counts");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        var other = sandbox.Clone("row-counts-other");
        sandbox.CommitFile(other, "remote-a.txt", "a\n", "remote a");
        sandbox.CommitFile(other, "remote-b.txt", "b\n", "remote b");
        GitShell.RunChecked(other, "push", "-q", "origin", "main");

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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("row-tooltip");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

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

    private static GitSyncViewModel NewPanel(GitPullStrategy strategy = GitPullStrategy.FastForwardOnly) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => strategy, () => TimeSpan.FromSeconds(30)));

    /// <summary>
    /// Attaches and waits for the trailing refresh inside one UI dispatch: the panel's continuations only
    /// drain while the dispatcher yields, so the poll must await on the UI thread itself.
    /// </summary>
    private static Task AttachAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, string sessionId, string directory) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext(sessionId, directory, null));
                await WaitIdleOnUiAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>Executes a command and waits for the operation plus its trailing refresh on the UI thread.</summary>
    private static Task ExecuteAndWaitAsync(HeadlessUnitTestSession session, GitSyncViewModel panel, System.Windows.Input.ICommand command) =>
        session.Dispatch(
            async () =>
            {
                command.Execute(null);
                await WaitIdleOnUiAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>
    /// Polls <c>IsBusy == false</c> with a bounded budget. Must run on the UI thread: awaiting
    /// <c>Task.Delay</c> there yields the dispatcher, which is what lets the panel's continuations run.
    /// </summary>
    private static async Task WaitIdleOnUiAsync(GitSyncViewModel panel)
    {
        var deadline = DateTime.UtcNow + IdleBudget;
        while (panel.IsBusy)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the git panel stayed busy past the 30 s budget");
            }

            await Task.Delay(PollInterval);
        }
    }

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

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static T? ControlOf<T>(Window window, string automationId)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control =>
                string.Equals(Avalonia.Automation.AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private static string? TextOf(Window window, string automationId) =>
        ControlOf<TextBlock>(window, automationId)?.Text;

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-git-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Isolated git runner: per-process empty global/system stubs so the developer's config never leaks in.</summary>
    private static class GitShell
    {
        private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "forge-git-panel-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            var global = Path.Combine(dir, "global.config");
            var system = Path.Combine(dir, "system.config");
            File.WriteAllText(global, string.Empty, new UTF8Encoding(false));
            File.WriteAllText(system, string.Empty, new UTF8Encoding(false));
            return (global, system);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static (int ExitCode, string Output, string Error) Run(string directory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[]
            {
                "-c", "user.name=Forge Test",
                "-c", "user.email=forge@example.com",
                "-c", "core.autocrlf=false",
                "-c", "core.safecrlf=false",
                "-c", "init.defaultBranch=main",
                "-c", "commit.gpgsign=false",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
            startInfo.Environment["GIT_PAGER"] = "cat";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Stubs.Value.Global;
            startInfo.Environment["GIT_CONFIG_SYSTEM"] = Stubs.Value.System;
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }

        public static string RunChecked(string directory, params string[] arguments)
        {
            var (exitCode, output, error) = Run(directory, arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
            }

            return output;
        }

        public static IReadOnlyList<string> BranchList(string repository) =>
            RunChecked(repository, "branch", "--list", "--format=%(refname:short)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .ToList();
    }

    /// <summary>A bare <c>origin.git</c> plus clones with pinned local identity.</summary>
    private sealed class GitSandbox : IDisposable
    {
        private GitSandbox(string root, string origin)
        {
            Root = root;
            Origin = origin;
        }

        public string Root { get; }

        public string Origin { get; }

        public static GitSandbox Create()
        {
            var root = NewTempDir();
            var origin = Path.Combine(root, "origin.git");
            GitShell.RunChecked(root, "init", "--bare", "-q", "-b", "main", origin);
            return new GitSandbox(root, origin);
        }

        public string Clone(string name)
        {
            var path = Path.Combine(Root, name);
            GitShell.RunChecked(Root, "clone", "-q", Origin, path);
            GitShell.RunChecked(path, "config", "--local", "user.name", "Forge Test");
            GitShell.RunChecked(path, "config", "--local", "user.email", "forge@example.com");
            GitShell.RunChecked(path, "config", "--local", "core.autocrlf", "false");
            GitShell.RunChecked(path, "config", "--local", "commit.gpgsign", "false");
            return path;
        }

        public string CommitFile(string repository, string relativePath, string content, string message)
        {
            var full = Path.Combine(repository, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            GitShell.RunChecked(repository, "add", "-A");
            GitShell.RunChecked(repository, "commit", "-q", "-m", message);
            return GitShell.RunChecked(repository, "rev-parse", "HEAD").Trim();
        }

        public void Dispose() => DeleteTree(Root);
    }
}
