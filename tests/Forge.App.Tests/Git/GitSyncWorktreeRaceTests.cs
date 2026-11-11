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
/// Pins the per-worktree behaviour of <see cref="GitSyncViewModel"/>: a write keeps running for its own worktree when
/// another session is attached, <see cref="GitSyncViewModel.IsBusy"/> and the running verb describe only the attached
/// worktree, the outcome of every worktree's last operation is remembered and shown again on attach, a context change
/// cancels only the read, a queued refresh runs once, and a failed re-read outranks the stale result of the last write.
/// <para>A write is held in flight without sleeping: <see cref="StubHttpRemote"/> is the <c>origin</c> of the repository
/// and keeps the fetch's HTTP request unanswered until the test calls <see cref="StubHttpRemote.Release"/>; it then
/// answers 401, which git (prompts disabled) reports as an authentication failure. Every scenario is one UI dispatch
/// because the row's continuations only run while the dispatcher yields.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSyncWorktreeRaceTests
{
    // ------------------------------------------------------------ 1. session switch with a write in flight

    [Fact]
    public async Task A_write_in_flight_neither_blocks_nor_stains_the_session_switched_to_and_its_result_waits_for_the_way_back()
    {
        using var rig = Rig.Create();
        await using var stub = new StubHttpRemote(hold: true);
        GitTestSandbox.PointOriginAt(rig.A, stub.Url);

        var session = SharedUi.Session;
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.Equal("main", panel.BranchLabel);

                    panel.FetchCommand.Execute(null);
                    Assert.True(panel.IsBusy);
                    await stub.WaitForRequestAsync();

                    // Switch away while A's fetch is blocked inside git.
                    panel.Attach(Context("sB", rig.B));

                    // The previous session's snapshot is gone at once; the new read has started.
                    Assert.False(panel.HasRepository);
                    Assert.Equal(string.Empty, panel.BranchLabel);
                    Assert.Equal(string.Empty, panel.UpstreamText);
                    Assert.True(panel.IsBusy, "the read of the new worktree is running");

                    await WaitIdleAsync(panel);

                    // A's git call is still blocked: it was neither cancelled nor finished by the switch.
                    Assert.False(stub.ClientGone.IsCompleted, "the switch killed the write of the other worktree");
                    Assert.Equal(0, stub.ResponseCount);

                    // The new session is idle, shows its own data and carries nothing of A.
                    Assert.False(panel.IsBusy);
                    Assert.Equal("feature-b", panel.BranchLabel);
                    Assert.False(panel.HasResult);
                    Assert.Equal(string.Empty, panel.LastResultText);
                    Assert.Equal(string.Empty, panel.MessageDetail);
                    Assert.False(panel.HasMessageDetail);
                    Assert.False(panel.MessageIsError);
                    Assert.Equal(string.Empty, panel.HintText);
                    Assert.False(panel.HasHint);
                    Assert.False(panel.HasRefreshProblem);
                    Assert.True(panel.FetchCommand.CanExecute(null), "B must not be blocked by A's running write");

                    // B can run its own write while A's is still running.
                    panel.FetchCommand.Execute(null);
                    await WaitIdleAsync(panel);
                    Assert.True(panel.HasResult);
                    Assert.False(panel.MessageIsError);
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
                    Assert.False(panel.MessageIsError);
                    Assert.False(panel.IsBusy);

                    // Back on A: its own outcome is shown, B's is not.
                    panel.Attach(Context("sA", rig.A));
                    await WaitIdleAsync(panel);
                    Assert.Equal("main", panel.BranchLabel);
                    Assert.True(panel.HasResult);
                    Assert.True(panel.MessageIsError);
                    Assert.NotEqual(bResult, panel.LastResultText);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
                    Assert.True(panel.HasHint);
                    Assert.True(panel.HasMessageDetail);
                    Assert.False(panel.HasRefreshProblem, "a successful re-read must not hide the write result nor add a problem");

                    // And B still has its own.
                    panel.Attach(Context("sB", rig.B));
                    await WaitIdleAsync(panel);
                    Assert.Equal(bResult, panel.LastResultText);
                    Assert.False(panel.MessageIsError);
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

        var session = SharedUi.Session;
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
                    Assert.Equal("main", panel.BranchLabel);
                    Assert.False(panel.HasResult);

                    // Come back to B while its write is still running: busy at once, snapshot empty, no read started.
                    panel.Attach(Context("sB", rig.B));
                    Assert.True(panel.IsBusy, "B's own write is running");
                    Assert.False(panel.FetchCommand.CanExecute(null));
                    Assert.Equal(string.Empty, panel.BranchLabel);
                    Assert.False(panel.HasResult, "B's result does not exist before its write ended");

                    await QuietWindowAsync();
                    Assert.True(panel.IsBusy);
                    Assert.Equal(string.Empty, panel.BranchLabel);
                    Assert.Equal(0, stub.ResponseCount);

                    // The write ends: its result is shown and the queued refresh runs - once.
                    stub.Release();
                    await WaitUntilAsync(() => panel.HasResult, "the held write to report its result");
                    await WaitIdleAsync(panel);
                    Assert.Equal("feature-b", panel.BranchLabel);
                    Assert.True(panel.MessageIsError);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
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
        var session = SharedUi.Session;
        var panel = await session.Dispatch(() => NewPanel(), CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    var branchTexts = new List<string>();
                    panel.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(GitSyncViewModel.BranchLabel))
                        {
                            branchTexts.Add(panel.BranchLabel);
                        }
                    };

                    // The read of A is started and replaced by B in the same dispatcher turn: it cannot have been applied.
                    panel.Attach(Context("sA", rig.A));
                    Assert.True(panel.IsBusy);
                    panel.Attach(Context("sB", rig.B));
                    Assert.False(panel.HasRepository);
                    Assert.Equal(string.Empty, panel.BranchLabel);
                    Assert.True(panel.IsBusy, "the read of B replaces the read of A");

                    await WaitIdleAsync(panel);
                    await QuietWindowAsync(200);

                    Assert.Equal("feature-b", panel.BranchLabel);
                    Assert.DoesNotContain("main", branchTexts);
                    Assert.False(panel.IsBusy);
                    Assert.Equal("feature-b", panel.BranchLabel);
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

        var session = SharedUi.Session;
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
                    Assert.True(panel.MessageIsError);
                    Assert.Equal(Loc.Instance[GitStrings.HintAuthentication], panel.HintText);
                    Assert.Equal("main", panel.BranchLabel);
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
