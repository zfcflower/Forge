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
/// Pins <see cref="GitPanelViewModel"/> (forge-app Git tab contract): attach to a real tracked repo shows
/// branch/upstream/ahead-behind/remotes/branches; a plain directory or an empty context clears the panel;
/// a newer attach wins over an in-flight read (generation guard); commands are disabled while busy and only
/// one operation runs at a time; fetch/pull/push, upstream setting, diverged/conflict handling, branch
/// create/switch/delete (with confirm), validation errors, fetch failure state, dispose and turn-completed
/// refresh. Every wait polls <c>IsBusy == false</c> with a 30 s bound — no <see cref="Thread.Sleep"/> timing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelViewModelTests
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasRepository,
                    panel.BranchText,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                    Remotes = panel.Remotes.ToList(),
                    Branches = panel.Branches.ToList(),
                },
                CancellationToken.None);

            Assert.True(state.HasRepository);
            Assert.Equal("main", state.BranchText);
            Assert.Equal("origin/main", state.UpstreamText);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), state.AheadBehindText);
            Assert.Contains("origin", state.Remotes);
            Assert.Equal("main", Assert.Single(state.Branches));
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Attach_to_a_plain_directory_clears_the_panel_and_disables_commands()
    {
        var plain = NewTempDir();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", plain);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasRepository,
                    panel.BranchText,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                    Remotes = panel.Remotes.Count,
                    Branches = panel.Branches.Count,
                    Fetch = panel.FetchCommand.CanExecute(null),
                    Pull = panel.PullCommand.CanExecute(null),
                    Push = panel.PushCommand.CanExecute(null),
                    Create = panel.CreateBranchCommand.CanExecute(null),
                    Switch = panel.SwitchBranchCommand.CanExecute(null),
                    Delete = panel.DeleteBranchCommand.CanExecute(null),
                    Abort = panel.AbortOperationCommand.CanExecute(null),
                    Continue = panel.ContinueOperationCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.False(state.HasRepository);
            Assert.Equal(string.Empty, state.BranchText);
            Assert.Equal(string.Empty, state.UpstreamText);
            Assert.Equal(string.Empty, state.AheadBehindText);
            Assert.Equal(0, state.Remotes);
            Assert.Equal(0, state.Branches);
            Assert.False(state.Fetch);
            Assert.False(state.Pull);
            Assert.False(state.Push);
            Assert.False(state.Create);
            Assert.False(state.Switch);
            Assert.False(state.Delete);
            Assert.False(state.Abort);
            Assert.False(state.Continue);
        }
        finally
        {
            panel.Dispose();
            DeleteTree(plain);
        }
    }

    [Fact]
    public async Task Attach_with_an_empty_context_clears_and_disables_everything()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                    panel.BranchText,
                    Refresh = panel.RefreshCommand.CanExecute(null),
                    Fetch = panel.FetchCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.False(state.HasRepository);
            Assert.Equal(string.Empty, state.BranchText);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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

            Assert.Equal("beta", await session.Dispatch(() => panel.BranchText, CancellationToken.None));

            await session.Dispatch(
                async () =>
                {
                    panel.Attach(new PanelContext("s-b", repoB, null));
                    panel.Attach(new PanelContext("s-a", repoA, null));
                    await WaitIdleOnUiAsync(panel);
                    return 0;
                },
                CancellationToken.None);

            Assert.Equal("alpha", await session.Dispatch(() => panel.BranchText, CancellationToken.None));
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                            panel.LastResultIsError,
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
            Assert.False(outcome.Idle.LastResultIsError);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                () => new { panel.AheadBehindText, panel.LastResultIsError, panel.HasResult },
                CancellationToken.None);
            Assert.False(pushed.LastResultIsError);
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
                () => new { panel.AheadBehindText, panel.LastResultIsError },
                CancellationToken.None);
            Assert.False(pulled.LastResultIsError);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            Assert.Equal(
                Loc.Instance[GitStrings.NoUpstream],
                await session.Dispatch(() => panel.UpstreamText, CancellationToken.None));

            await ExecuteAndWaitAsync(session, panel, panel.PushCommand);

            var state = await session.Dispatch(
                () => new { panel.UpstreamText, panel.AheadBehindText, panel.LastResultIsError },
                CancellationToken.None);

            Assert.False(state.LastResultIsError);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                    panel.LastResultIsError,
                    panel.HintText,
                    panel.HasHint,
                    panel.BranchText,
                },
                CancellationToken.None);

            Assert.True(state.LastResultIsError);
            Assert.True(state.HasHint);
            Assert.False(string.IsNullOrWhiteSpace(state.HintText));
            Assert.Equal("main", state.BranchText);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(strategy: GitPullStrategy.Rebase);
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            await ExecuteAndWaitAsync(session, panel, panel.PullCommand);

            var conflict = await session.Dispatch(
                () => new
                {
                    panel.LastResultIsError,
                    panel.OperationText,
                    panel.HasOperation,
                    Files = panel.ConflictedFiles.ToList(),
                },
                CancellationToken.None);

            Assert.True(conflict.LastResultIsError);
            Assert.False(string.IsNullOrWhiteSpace(conflict.OperationText));
            Assert.True(conflict.HasOperation);
            Assert.Contains("file.txt", conflict.Files);

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

    [Fact]
    public async Task Create_branch_then_switch_moves_head()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            await session.Dispatch(() => { panel.NewBranchName = "feature/x"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.CreateBranchCommand);

            var created = await session.Dispatch(
                () => new { Branches = panel.Branches.ToList(), panel.NewBranchName, panel.LastResultIsError },
                CancellationToken.None);
            Assert.False(created.LastResultIsError);
            Assert.Contains("feature/x", created.Branches);
            Assert.Equal(string.Empty, created.NewBranchName);
            Assert.Contains("feature/x", GitShell.BranchList(work));

            await session.Dispatch(() => { panel.SelectedBranch = "feature/x"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.SwitchBranchCommand);

            Assert.Equal("feature/x", await session.Dispatch(() => panel.BranchText, CancellationToken.None));
            Assert.Equal("feature/x", GitShell.RunChecked(work, "branch", "--show-current").Trim());
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Switch_with_a_dirty_tracked_file_fails_and_keeps_the_branch()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        GitShell.RunChecked(work, "branch", "other");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            await File.WriteAllTextAsync(Path.Combine(work, "file.txt"), "dirty\n");

            await session.Dispatch(() => { panel.SelectedBranch = "other"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.SwitchBranchCommand);

            var state = await session.Dispatch(
                () => new { panel.BranchText, panel.LastResultIsError, panel.HintText },
                CancellationToken.None);

            Assert.True(state.LastResultIsError);
            Assert.False(string.IsNullOrWhiteSpace(state.HintText));
            Assert.Equal("main", state.BranchText);
            Assert.Equal("main", GitShell.RunChecked(work, "branch", "--show-current").Trim());
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Delete_branch_asks_for_confirmation_and_never_confirms_the_current_branch()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        GitShell.RunChecked(work, "branch", "spare");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var confirms = 0;
        var answer = false;
        var panel = NewPanel(confirm: (_, _) =>
        {
            confirms++;
            return Task.FromResult(answer);
        });
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            // Decline: the branch is kept and nothing is reported as deleted.
            await session.Dispatch(() => { panel.SelectedBranch = "spare"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.DeleteBranchCommand);
            Assert.Equal(1, Volatile.Read(ref confirms));
            Assert.Contains("spare", GitShell.BranchList(work));

            // Accept: the branch is gone and the result names it.
            answer = true;
            await ExecuteAndWaitAsync(session, panel, panel.DeleteBranchCommand);
            Assert.Equal(2, Volatile.Read(ref confirms));
            Assert.DoesNotContain("spare", GitShell.BranchList(work));
            var deleted = await session.Dispatch(() => panel.LastResultText, CancellationToken.None);
            Assert.Contains("spare", deleted, StringComparison.Ordinal);

            // The current branch never reaches the dialog and reports an error instead.
            await session.Dispatch(() => { panel.SelectedBranch = "main"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.DeleteBranchCommand);
            Assert.Equal(2, Volatile.Read(ref confirms));
            var current = await session.Dispatch(
                () => new { panel.LastResultIsError, panel.LastResultText, panel.BranchText },
                CancellationToken.None);
            Assert.True(current.LastResultIsError);
            Assert.False(string.IsNullOrWhiteSpace(current.LastResultText));
            Assert.Equal("main", current.BranchText);
            Assert.Contains("main", GitShell.BranchList(work));
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Blank_and_invalid_branch_names_report_a_localized_error_without_creating()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            await session.Dispatch(() => { panel.NewBranchName = "   "; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.CreateBranchCommand);
            Assert.Equal(
                Loc.Instance[GitStrings.ErrorBranchNameRequired],
                await session.Dispatch(() => panel.LastResultText, CancellationToken.None));
            Assert.True(await session.Dispatch(() => panel.LastResultIsError, CancellationToken.None));

            await session.Dispatch(() => { panel.NewBranchName = "bad..name"; return 0; }, CancellationToken.None);
            await ExecuteAndWaitAsync(session, panel, panel.CreateBranchCommand);

            var invalid = await session.Dispatch(
                () => new { panel.LastResultIsError, panel.LastResultText },
                CancellationToken.None);
            Assert.True(invalid.LastResultIsError);
            Assert.False(string.IsNullOrWhiteSpace(invalid.LastResultText));
            Assert.DoesNotContain("bad..name", GitShell.BranchList(work));
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ failure, dispose, refresh triggers

    [Fact]
    public async Task Fetch_failure_keeps_the_previous_state_and_shows_the_localized_hint()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            var before = await session.Dispatch(
                () => new
                {
                    panel.BranchText,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                    Remotes = panel.Remotes.ToList(),
                    Branches = panel.Branches.ToList(),
                },
                CancellationToken.None);

            GitShell.RunChecked(work, "remote", "set-url", "origin", Path.Combine(sandbox.Root, "gone.git"));
            await ExecuteAndWaitAsync(session, panel, panel.FetchCommand);

            var after = await session.Dispatch(
                () => new
                {
                    panel.BranchText,
                    panel.UpstreamText,
                    panel.AheadBehindText,
                    Remotes = panel.Remotes.ToList(),
                    Branches = panel.Branches.ToList(),
                    panel.LastResultIsError,
                    panel.HintText,
                    panel.HasHint,
                },
                CancellationToken.None);

            Assert.True(after.LastResultIsError);
            Assert.True(after.HasHint);
            Assert.Equal(Loc.Instance[GitStrings.HintRemoteNotFound], after.HintText);
            Assert.Equal(before.BranchText, after.BranchText);
            Assert.Equal(before.UpstreamText, after.UpstreamText);
            Assert.Equal(before.AheadBehindText, after.AheadBehindText);
            Assert.Equal(before.Remotes, after.Remotes);
            Assert.Equal(before.Branches, after.Branches);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                () => new { panel.IsBusy, panel.HasRepository, panel.BranchText },
                CancellationToken.None);

            Assert.False(state.IsBusy);
            Assert.False(state.HasRepository);
            Assert.Equal(string.Empty, state.BranchText);
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

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);
            GitShell.RunChecked(work, "branch", "external-x");

            // A turn of another session does nothing: no refresh, no busy, no new branch.
            await session.Dispatch(() => { panel.NotifyTurnCompleted("other"); return 0; }, CancellationToken.None);
            var untouched = await session.Dispatch(
                () => new { panel.IsBusy, Branches = panel.Branches.ToList() },
                CancellationToken.None);
            Assert.False(untouched.IsBusy);
            Assert.DoesNotContain("external-x", untouched.Branches);

            // A turn of the attached session re-reads the state.
            var refreshed = await session.Dispatch(
                async () =>
                {
                    panel.NotifyTurnCompleted("s1");
                    await WaitIdleOnUiAsync(panel);
                    return panel.Branches.ToList();
                },
                CancellationToken.None);
            Assert.Contains("external-x", refreshed);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task The_tool_view_shows_the_panel_branch_upstream_and_ahead_behind()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel();
        try
        {
            await AttachAndWaitAsync(session, panel, "s1", work);

            var rendered = await session.Dispatch(
                () =>
                {
                    var view = new GitToolView { DataContext = panel };
                    var window = new Window { Width = 420, Height = 640, Content = view };
                    window.Show();
                    Settle(window);
                    try
                    {
                        return new
                        {
                            Branch = TextOf(window, "git-branch"),
                            Upstream = TextOf(window, "git-upstream"),
                            AheadBehind = TextOf(window, "git-ahead-behind"),
                            HasPicker = ControlOf<ComboBox>(window, "git-branch-picker") is not null,
                        };
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

            Assert.Equal("main", rendered.Branch);
            Assert.Equal("origin/main", rendered.Upstream);
            Assert.Equal(Loc.Instance.Format(GitStrings.AheadBehind, 0, 0), rendered.AheadBehind);
            Assert.True(rendered.HasPicker);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ harness

    private static GitPanelViewModel NewPanel(
        GitPullStrategy strategy = GitPullStrategy.FastForwardOnly,
        Func<string, string, Task<bool>>? confirm = null) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => strategy, () => TimeSpan.FromSeconds(30)),
            confirm ?? ((_, _) => Task.FromResult(true)));

    /// <summary>
    /// Attaches and waits for the trailing refresh inside one UI dispatch: the panel's continuations only
    /// drain while the dispatcher yields, so the poll must await on the UI thread itself.
    /// </summary>
    private static Task AttachAndWaitAsync(HeadlessUnitTestSession session, GitPanelViewModel panel, string sessionId, string directory) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext(sessionId, directory, null));
                await WaitIdleOnUiAsync(panel);
                return 0;
            },
            CancellationToken.None);

    /// <summary>Executes a command and waits for the operation plus its trailing refresh on the UI thread.</summary>
    private static Task ExecuteAndWaitAsync(HeadlessUnitTestSession session, GitPanelViewModel panel, System.Windows.Input.ICommand command) =>
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
    private static async Task WaitIdleOnUiAsync(GitPanelViewModel panel)
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
