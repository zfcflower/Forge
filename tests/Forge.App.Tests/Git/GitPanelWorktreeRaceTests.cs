using System.Collections.Specialized;
using Avalonia.Headless;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;
using static Forge.App.Tests.Git.GitPanelTesting;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the per-worktree behaviour of <see cref="GitPanelViewModel"/> (commit 77137e6): a write keeps running for its own
/// worktree when another session is attached, <see cref="GitPanelViewModel.IsBusy"/> describes only the attached
/// worktree, the outcome of every worktree's last operation is remembered and shown again on attach, a context change
/// cancels only the read, a queued refresh runs once, and a delete-branch confirmation that outlives its context drops
/// the request without a trace.
/// <para>A write is held in flight without sleeping: <see cref="StubHttpRemote"/> is the <c>origin</c> of the repository
/// and keeps the fetch's HTTP request unanswered until the test calls <see cref="StubHttpRemote.Release"/>; it then
/// answers 401, which git (prompts disabled) reports as an authentication failure. A delete-branch write is held by the
/// <c>confirm</c> delegate's task. Every scenario is one UI dispatch because the panel's continuations only run while
/// the dispatcher yields.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelWorktreeRaceTests
{
    // ------------------------------------------------------------ 1. session switch with a write in flight

    [Fact]
    public async Task A_write_in_flight_neither_blocks_nor_stains_the_session_switched_to_and_its_result_waits_for_the_way_back()
    {
        using var rig = Rig.Create();
        await using var stub = new StubHttpRemote(hold: true);
        GitTestSandbox.PointOriginAt(rig.A, stub.Url);

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.Equal("main", panel.BranchText);

                    panel.FetchCommand.Execute(null);
                    Assert.True(panel.IsBusy);
                    await stub.WaitForRequestAsync();

                    // Switch away while A's fetch is blocked inside git.
                    panel.Attach(Context("sB", rig.B));

                    // The previous session's snapshot is gone at once; the new read has started.
                    Assert.False(panel.HasRepository);
                    Assert.Equal(string.Empty, panel.BranchText);
                    Assert.Equal(string.Empty, panel.UpstreamText);
                    Assert.Empty(panel.Branches);
                    Assert.True(panel.IsBusy, "the read of the new worktree is running");

                    await WaitIdleAsync(panel);

                    // A's git call is still blocked: it was neither cancelled nor finished by the switch.
                    Assert.False(stub.ClientGone.IsCompleted, "the switch killed the write of the other worktree");
                    Assert.Equal(0, stub.ResponseCount);

                    // The new session is idle, shows its own data and carries nothing of A.
                    Assert.False(panel.IsBusy);
                    Assert.Equal("feature-b", panel.BranchText);
                    Assert.False(panel.HasResult);
                    Assert.Equal(string.Empty, panel.LastResultText);
                    Assert.Equal(string.Empty, panel.LastResultDetail);
                    Assert.False(panel.HasLastResultDetail);
                    Assert.False(panel.LastResultIsError);
                    Assert.Equal(string.Empty, panel.HintText);
                    Assert.False(panel.HasHint);
                    Assert.False(panel.HasRefreshProblem);
                    Assert.True(panel.FetchCommand.CanExecute(null), "B must not be blocked by A's running write");

                    // B can run its own write while A's is still running.
                    panel.FetchCommand.Execute(null);
                    await WaitIdleAsync(panel);
                    Assert.True(panel.HasResult);
                    Assert.False(panel.LastResultIsError);
                    Assert.Equal(string.Empty, panel.HintText);
                    var bResult = panel.LastResultText;
                    Assert.Equal(0, stub.ResponseCount);

                    // A's write ends while B is attached: its outcome is stored, nothing on screen moves.
                    var changes = new List<string>();
                    void Record(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => changes.Add(e.PropertyName ?? string.Empty);
                    panel.PropertyChanged += Record;
                    stub.Release();
                    await stub.ClientGone.WaitAsync(Budget);
                    await QuietWindowAsync();
                    panel.PropertyChanged -= Record;

                    Assert.True(changes.Count == 0, "a write of a switched-away worktree raised: " + string.Join(", ", changes));
                    Assert.Equal(bResult, panel.LastResultText);
                    Assert.False(panel.LastResultIsError);
                    Assert.False(panel.IsBusy);

                    // Back on A: its own outcome is shown, B's is not.
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.Equal("main", panel.BranchText);
                    Assert.True(panel.HasResult);
                    Assert.True(panel.LastResultIsError);
                    Assert.NotEqual(bResult, panel.LastResultText);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
                    Assert.True(panel.HasHint);
                    Assert.True(panel.HasLastResultDetail);
                    Assert.False(panel.HasRefreshProblem, "a successful re-read must not hide the write result nor add a problem");

                    // And B still has its own.
                    panel.Attach(Context("sB", rig.B));
                    await WaitIdleAsync(panel);
                    Assert.Equal(bResult, panel.LastResultText);
                    Assert.False(panel.LastResultIsError);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Switching_to_a_worktree_with_its_own_write_running_queues_the_refresh_until_that_write_ends()
    {
        using var rig = Rig.Create();
        await using var stub = new StubHttpRemote(hold: true);
        GitTestSandbox.PointOriginAt(rig.B, stub.Url);

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sB", rig.B));
                    await WaitIdleAsync(panel);
                    panel.FetchCommand.Execute(null);
                    Assert.True(panel.IsBusy);
                    await stub.WaitForRequestAsync();

                    // Leave B (its write keeps running) for the idle A: A is not busy.
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.False(panel.IsBusy);
                    Assert.Equal("main", panel.BranchText);
                    Assert.False(panel.HasResult);

                    // Come back to B while its write is still running: busy at once, snapshot empty, no read started.
                    var resets = 0;
                    panel.Branches.CollectionChanged += (_, e) =>
                    {
                        if (e.Action == NotifyCollectionChangedAction.Reset)
                        {
                            resets++;
                        }
                    };
                    panel.Attach(Context("sB", rig.B));
                    var resetsAfterAttach = resets;
                    Assert.True(panel.IsBusy, "B's own write is running");
                    Assert.False(panel.FetchCommand.CanExecute(null));
                    Assert.Equal(string.Empty, panel.BranchText);
                    Assert.False(panel.HasResult, "B's result does not exist before its write ended");

                    await QuietWindowAsync();
                    Assert.True(panel.IsBusy);
                    Assert.Equal(string.Empty, panel.BranchText);
                    Assert.Equal(resetsAfterAttach, resets);
                    Assert.Equal(0, stub.ResponseCount);

                    // The write ends: its result is shown and the queued refresh runs - once.
                    stub.Release();
                    await WaitUntilAsync(() => panel.HasResult, "the held write to report its result");
                    await WaitIdleAsync(panel);
                    Assert.Equal("feature-b", panel.BranchText);
                    Assert.True(panel.LastResultIsError);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
                    Assert.Equal(resetsAfterAttach + 1, resets);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Attaching_a_different_context_cancels_only_the_read_in_flight_and_never_applies_it()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    var branchTexts = new List<string>();
                    panel.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(GitPanelViewModel.BranchText))
                        {
                            branchTexts.Add(panel.BranchText);
                        }
                    };

                    // The read of A is started and replaced by B in the same dispatcher turn: it cannot have been applied.
                    panel.Attach(Context("sA", rig.A));
                    Assert.True(panel.IsBusy);
                    panel.Attach(Context("sB", rig.B));
                    Assert.False(panel.HasRepository);
                    Assert.Equal(string.Empty, panel.BranchText);
                    Assert.True(panel.IsBusy, "the read of B replaces the read of A");

                    await WaitIdleAsync(panel);
                    await QuietWindowAsync(200);

                    Assert.Equal("feature-b", panel.BranchText);
                    Assert.DoesNotContain("main", branchTexts);
                    Assert.False(panel.IsBusy);
                    Assert.Equal(["feature-b", "main"], panel.Branches.ToArray());
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Attaching_an_empty_context_during_a_write_is_idle_and_the_result_survives_the_clear()
    {
        using var rig = Rig.Create();
        await using var stub = new StubHttpRemote(hold: true);
        GitTestSandbox.PointOriginAt(rig.A, stub.Url);

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.FetchCommand.Execute(null);
                    await stub.WaitForRequestAsync();

                    panel.Attach(PanelContext.Empty);
                    Assert.False(panel.IsBusy, "no session is attached, so nothing is busy");
                    Assert.False(panel.HasRepository);
                    Assert.False(panel.HasResult);
                    Assert.False(panel.FetchCommand.CanExecute(null));
                    Assert.False(stub.ClientGone.IsCompleted, "clearing the panel must not kill the write");

                    stub.Release();
                    await stub.ClientGone.WaitAsync(Budget);
                    await QuietWindowAsync();
                    Assert.False(panel.HasResult, "the result of the left worktree is not shown for the empty context");
                    Assert.Equal(string.Empty, panel.HintText);

                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.True(panel.LastResultIsError);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
                    Assert.Equal("main", panel.BranchText);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failing_re_read_after_a_successful_write_keeps_the_write_result_and_reports_its_own_problem()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var entered = NewSignal();
        var gate = NewSignal<bool>();
        var panel = await session.Dispatch(
            () => NewPanel(confirm: (_, _) =>
            {
                entered.TrySetResult();
                return gate.Task;
            }),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);
                    await entered.Task.WaitAsync(Budget);

                    // Corrupt the index while the dialog is open: `git branch -d` does not read it, the re-read does.
                    File.WriteAllText(Path.Combine(rig.A, ".git", "index"), "this is not an index file");
                    gate.SetResult(true);
                    await WaitIdleAsync(panel);

                    Assert.Equal(Loc.Instance.Format(GitStrings.ResultBranchDeleted, "topic"), panel.LastResultText);
                    Assert.False(panel.LastResultIsError);
                    Assert.True(panel.HasRefreshProblem, "the re-read of a corrupt index must be reported");
                    Assert.NotEqual(string.Empty, panel.RefreshProblemText);
                    Assert.DoesNotContain("topic", GitTestShell.BranchList(rig.A));
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ 2. delete-branch race

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_confirmation_that_outlives_its_context_makes_no_git_call_and_changes_no_result(bool answer)
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var entered = NewSignal();
        var gate = NewSignal<bool>();
        var asked = 0;
        var panel = await session.Dispatch(
            () => NewPanel(confirm: (_, _) =>
            {
                asked++;
                entered.TrySetResult();
                return gate.Task;
            }),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);

                    // The dialog is open: that is a write of the attached worktree.
                    await entered.Task.WaitAsync(Budget);
                    Assert.True(panel.IsBusy, "the open confirm dialog is a running write");
                    Assert.False(panel.DeleteBranchCommand.CanExecute(null));

                    // The user switches session while the dialog is open; B is idle and has no result.
                    panel.Attach(Context("sB", rig.B));
                    await WaitIdleAsync(panel);
                    Assert.False(panel.IsBusy, "the dialog belongs to the worktree that was left");
                    Assert.Equal("feature-b", panel.BranchText);
                    Assert.False(panel.HasResult);

                    var changes = new List<string>();
                    void Record(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => changes.Add(e.PropertyName ?? string.Empty);
                    panel.PropertyChanged += Record;
                    gate.SetResult(answer);

                    // The dialog's continuation runs on the dispatcher; going back to A and waiting for idle also waits
                    // for it (A is busy until the dropped flow unwinds).
                    await QuietWindowAsync(300);
                    panel.PropertyChanged -= Record;
                    Assert.True(
                        changes.All(static name => !ResultProperties.Contains(name)),
                        "the dropped delete changed the result of B: " + string.Join(", ", changes));
                    Assert.False(panel.HasResult);
                    Assert.Equal(string.Empty, panel.LastResultText);
                    Assert.False(panel.IsBusy);

                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.False(panel.HasResult, "no result was recorded for A either");
                    Assert.Equal(string.Empty, panel.LastResultText);
                    Assert.False(panel.LastResultIsError);
                    Assert.Equal(1, asked);
                    Assert.Contains("topic", GitTestShell.BranchList(rig.A));
                    Assert.Contains("topic", panel.Branches);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Delete_confirmation_answered_after_a_round_trip_to_another_session_is_dropped_too()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var entered = NewSignal();
        var gate = NewSignal<bool>();
        var panel = await session.Dispatch(
            () => NewPanel(confirm: (_, _) =>
            {
                entered.TrySetResult();
                return gate.Task;
            }),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);
                    await entered.Task.WaitAsync(Budget);

                    // A -> B -> A: the dialog was opened for an attachment that no longer exists.
                    panel.Attach(Context("sB", rig.B));
                    await WaitIdleAsync(panel);
                    panel.Attach(Context("sA", rig.A));
                    Assert.True(panel.IsBusy, "A's confirmation is still pending");
                    gate.SetResult(true);
                    await WaitIdleAsync(panel);

                    Assert.Contains("topic", GitTestShell.BranchList(rig.A));
                    Assert.False(panel.HasResult);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Delete_confirmation_answered_in_an_unchanged_context_deletes_the_branch_and_reports_it()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);
                    await WaitIdleAsync(panel);

                    Assert.Equal(Loc.Instance.Format(GitStrings.ResultBranchDeleted, "topic"), panel.LastResultText);
                    Assert.False(panel.LastResultIsError);
                    Assert.DoesNotContain("topic", GitTestShell.BranchList(rig.A));
                    Assert.DoesNotContain("topic", panel.Branches);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ execute while busy, queued refresh

    [Fact]
    public async Task Commands_executed_while_busy_are_no_ops()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var entered = NewSignal();
        var gate = NewSignal<bool>();
        var asked = 0;
        var panel = await session.Dispatch(
            () => NewPanel(confirm: (_, _) =>
            {
                asked++;
                entered.TrySetResult();
                return gate.Task;
            }),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);
                    await entered.Task.WaitAsync(Budget);
                    Assert.True(panel.IsBusy);

                    panel.NewBranchName = "must-not-exist";
                    panel.CreateBranchCommand.Execute(null);
                    panel.DeleteBranchCommand.Execute(null);
                    panel.SwitchBranchCommand.Execute(null);
                    panel.FetchCommand.Execute(null);
                    panel.PullCommand.Execute(null);
                    panel.PushCommand.Execute(null);
                    panel.SetUpstreamCommand.Execute(null);
                    panel.AbortOperationCommand.Execute(null);
                    panel.ContinueOperationCommand.Execute(null);
                    panel.RefreshCommand.Execute(null);

                    Assert.Equal(1, asked);
                    Assert.Equal("must-not-exist", panel.NewBranchName);
                    Assert.False(panel.HasResult, "a no-op command must not report a failure either");

                    gate.SetResult(false);
                    await WaitIdleAsync(panel);
                    Assert.Equal(1, asked);
                    Assert.DoesNotContain("must-not-exist", GitTestShell.BranchList(rig.A));
                    Assert.Equal("must-not-exist", panel.NewBranchName);
                    Assert.False(panel.HasResult);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Panel_internal_refresh_requests_during_work_queue_exactly_one_read()
    {
        using var rig = Rig.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var entered = NewSignal();
        var gate = NewSignal<bool>();
        var panel = await session.Dispatch(
            () => NewPanel(confirm: (_, _) =>
            {
                entered.TrySetResult();
                return gate.Task;
            }),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    panel.SelectedBranch = "topic";
                    panel.DeleteBranchCommand.Execute(null);
                    await entered.Task.WaitAsync(Budget);

                    var resets = 0;
                    panel.Branches.CollectionChanged += (_, e) =>
                    {
                        if (e.Action == NotifyCollectionChangedAction.Reset)
                        {
                            resets++;
                        }
                    };

                    // Three turn notifications, two attaches of the same context and a refresh command while the work
                    // item is running: none starts a read now.
                    panel.NotifyTurnCompleted("sA");
                    panel.NotifyTurnCompleted("sA");
                    panel.NotifyTurnCompleted("sA");
                    panel.Attach(Context("sA", rig.A));
                    panel.Attach(Context("sA", rig.A));
                    panel.RefreshCommand.Execute(null);
                    await QuietWindowAsync(200);
                    Assert.Equal(0, resets);
                    Assert.True(panel.IsBusy);

                    // A notification for another session is not ours and queues nothing.
                    panel.NotifyTurnCompleted("someone-else");

                    // The work item ends (declined dialog, no trailing read): the queued refresh runs once.
                    gate.SetResult(false);
                    await WaitIdleAsync(panel);
                    await QuietWindowAsync(200);
                    Assert.Equal(1, resets);
                    Assert.False(panel.IsBusy);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ helpers

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Repo A on <c>main</c> with a pushed commit, upstream <c>origin/main</c> and a merged local branch <c>topic</c>;
    /// repo B a second clone sitting on <c>feature-b</c>. Different branch names tell the two apart on screen.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly GitTestSandbox sandbox;

        private Rig(GitTestSandbox sandbox, string a, string b)
        {
            this.sandbox = sandbox;
            A = a;
            B = b;
        }

        public string A { get; }

        public string B { get; }

        public static Rig Create()
        {
            var sandbox = GitTestSandbox.Create();
            try
            {
                var a = sandbox.CloneTracked("a");
                GitTestShell.RunChecked(a, "branch", "topic");
                var b = sandbox.Clone("b");
                GitTestShell.RunChecked(b, "checkout", "-q", "-b", "feature-b");
                return new Rig(sandbox, a, b);
            }
            catch
            {
                sandbox.Dispose();
                throw;
            }
        }

        public void Dispose() => sandbox.Dispose();
    }
}
