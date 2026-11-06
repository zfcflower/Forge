using Avalonia.Automation;
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
using static Forge.App.Tests.Git.GitPanelTesting;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the "Wait for checks" half of the Git panel: the button gating (a pull request, no watch running), the live
/// progress and merge-state lines, the verdict of the finished wait and the status-bar marker it drives, the cancel
/// button, and the four ways a watch of this worktree ends - ready, failed, timed out and cancelled. It also pins the
/// watch's lifetime against the sessions: closing the session that started it stops it, the bounded table of remembered
/// worktrees never drops a worktree while its watch runs, and <c>Dispose</c> stops everything without a late reading
/// reaching the panel.
/// <para>The poller is the real <see cref="PrChecksPoller"/> over a fake platform and a virtual clock, and the panel's post
/// seam is the app's UI-thread dispatcher, so every wait of this suite is a bounded poll inside one dispatch and nothing
/// sleeps and nothing starts gh.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelPullRequestWatchTests
{
    // ------------------------------------------------------------ the button and the live progress

    [Fact]
    public async Task Waiting_shows_the_progress_the_verdict_and_the_status_bar_marker()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform, notifier: notifier);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");

                    // Nothing is waiting yet.
                    Assert.True(panel.ShowWaitButton);
                    Assert.False(panel.ShowCancelWait);
                    Assert.False(panel.IsWatching);
                    Assert.False(panel.HasWatchProgress);
                    Assert.Equal(string.Empty, panel.ChecksText);
                    Assert.Equal(string.Empty, panel.WatchVerdictText);

                    panel.WaitForChecksCommand.Execute(null);

                    // The button flips at once, before the first reading arrives.
                    Assert.True(panel.IsWatching);
                    Assert.True(panel.ShowCancelWait);
                    Assert.False(panel.ShowWaitButton);
                    Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                    Assert.True(panel.HasWatchProgress);
                    Assert.Equal(Loc.Instance[GitStrings.WatchWaiting], panel.WatchProgressText);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksRunning], panel.ChecksText);
                    Assert.Equal(string.Empty, panel.WatchMergeText);

                    // A second click while the wait runs is a no-op (the command is disabled).
                    panel.WaitForChecksCommand.Execute(null);

                    await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading");
                    await WaitUntilAsync(() => panel.HasWatchVerdict, "the verdict");

                    Assert.False(panel.IsWatching);
                    Assert.False(panel.ShowCancelWait);
                    Assert.True(panel.ShowWaitButton);
                    Assert.Equal(Loc.Instance[GitStrings.OutcomeReady], panel.WatchVerdictText);
                    Assert.False(panel.WatchVerdictIsProblem);
                    Assert.Equal(Loc.Instance.Format(GitStrings.WatchProgress, 2, 0, 0), panel.WatchProgressText);
                    Assert.Equal(Loc.Instance.Format(GitStrings.WatchMergeState, "CLEAN"), panel.WatchMergeText);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksPassed], panel.ChecksText);

                    await WaitUntilAsync(() => notifier.Count.Count >= 1, "the notification");
                    Assert.Equal(panel.WatchVerdictText, Assert.Single(notifier.Calls).Body);
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
    public async Task A_running_wait_marks_the_status_bar_as_running()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);

                    await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading");

                    // The pending check keeps the wait going, and the marker says so.
                    Assert.True(panel.IsWatching);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksRunning], panel.ChecksText);
                    Assert.Equal(Loc.Instance.Format(GitStrings.WatchProgress, 1, 0, 1), panel.WatchProgressText);
                    Assert.False(panel.HasWatchVerdict);
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
    public async Task A_failed_check_ends_with_the_cross_the_problem_verdict_and_the_failing_names()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();
        var script = new PrAppScript(
            PrAppStep.Of(PrKit.Merge(), PrKit.Check("build", PrCheckBucket.Fail), PrKit.Check("lint", PrCheckBucket.Pass)));
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform, notifier: notifier);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => panel.HasWatchVerdict, "the verdict");

                    Assert.True(panel.WatchVerdictIsProblem);
                    Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeChecksFailed, 1, "build"), panel.WatchVerdictText);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksFailed], panel.ChecksText);
                    Assert.Contains("build", panel.WatchVerdictText, StringComparison.Ordinal);

                    await WaitUntilAsync(() => notifier.Count.Count >= 1, "the notification");
                    Assert.Equal(panel.WatchVerdictText, Assert.Single(notifier.Calls).Body);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ cancelling

    [Fact]
    public async Task Cancelling_keeps_the_cancelled_verdict_clears_the_marker_and_notifies_nobody()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform, notifier: notifier);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => gate.Entered.Count >= 1, "the wait to be polling");

                    panel.CancelWaitCommand.Execute(null);

                    Assert.False(panel.IsWatching);
                    Assert.False(panel.ShowCancelWait);
                    Assert.True(panel.ShowWaitButton);
                    Assert.True(panel.WaitForChecksCommand.CanExecute(null));
                    Assert.True(panel.HasWatchVerdict);
                    Assert.Equal(Loc.Instance[GitStrings.WatchCancelled], panel.WatchVerdictText);
                    Assert.False(panel.WatchVerdictIsProblem);
                    Assert.Equal(string.Empty, panel.ChecksText);

                    // The reading that was in flight is not applied afterwards either.
                    gate.Release();
                    await QuietWindowAsync();

                    Assert.Equal(Loc.Instance[GitStrings.WatchCancelled], panel.WatchVerdictText);
                    Assert.Equal(0, notifier.Count.Count);
                    Assert.True(gate.Cancelled.Count >= 1);
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
    public async Task Cancelling_without_a_watch_does_nothing_and_the_cancel_button_is_disabled()
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
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");

                    Assert.False(panel.CancelWaitCommand.CanExecute(null));
                    panel.CancelWaitCommand.Execute(null);

                    Assert.False(panel.IsWatching);
                    Assert.Equal(string.Empty, panel.WatchVerdictText);
                    Assert.Equal(string.Empty, panel.ChecksText);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ the timeout

    [Fact]
    public async Task A_wait_that_times_out_needs_attention_and_is_notified()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();
        var script = new PrAppScript(PrAppStep.Of(PrKit.Merge(), PrKit.Check("build", PrCheckBucket.Pending)));
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(
            platform,
            () => new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(30), MaxWait = TimeSpan.FromMinutes(1) },
            notifier);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => panel.HasWatchVerdict, "the verdict");

                    Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeTimedOut, 1), panel.WatchVerdictText);
                    Assert.True(panel.WatchVerdictIsProblem);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksAttention], panel.ChecksText);

                    // The last reading is still on screen, so the user sees where the checks stood.
                    Assert.NotEqual(string.Empty, panel.WatchProgressText);
                    Assert.Equal(Loc.Instance.Format(GitStrings.WatchMergeState, "CLEAN"), panel.WatchMergeText);

                    await WaitUntilAsync(() => notifier.Count.Count >= 1, "the notification");
                    Assert.Equal(panel.WatchVerdictText, Assert.Single(notifier.Calls).Body);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ the lifetime of a watch

    [Fact]
    public async Task Closing_the_session_that_started_the_watch_stops_it()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform, notifier: notifier);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => gate.Entered.Count >= 1, "the wait to be polling");

                    panel.ReleaseSession("s1");

                    Assert.False(panel.IsWatching);
                    Assert.Equal(Loc.Instance[GitStrings.WatchCancelled], panel.WatchVerdictText);
                    Assert.Equal(string.Empty, panel.ChecksText);
                    await gate.Cancelled.WhenAtLeastAsync(1, "the in-flight poll to be cancelled");

                    gate.Release();
                    await QuietWindowAsync();

                    Assert.Equal(0, notifier.Count.Count);
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
    public async Task Releasing_another_session_leaves_the_watch_of_the_worktree_alone()
    {
        using var sandbox = GitTestSandbox.Create();
        var workA = sandbox.CloneTracked("a");
        var workB = sandbox.Clone("b");
        sandbox.CommitFile(workB, "other.txt", "b" + Environment.NewLine, "b initial");
        GitTestShell.RunChecked(workB, "push", "-q", "-u", "origin", "main");

        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", workA));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request of A");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading of A");

                    // Another session's tab is closed: it started no watch, so nothing of A's is stopped.
                    panel.Attach(Context("s2", workB));
                    Assert.False(panel.IsWatching);
                    panel.ReleaseSession("s2");
                    Assert.Equal(string.Empty, panel.ChecksText);

                    // Back on A: the watch is still running and its reading is still there.
                    panel.Attach(Context("s1", workA));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request of A again");
                    Assert.True(panel.IsWatching);
                    Assert.Equal(Loc.Instance[GitStrings.ChecksRunning], panel.ChecksText);
                    Assert.NotEqual(string.Empty, panel.WatchMergeText);

                    // And now A's own session closes: the watch stops.
                    panel.ReleaseSession("s1");
                    Assert.False(panel.IsWatching);
                    gate.Release();
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
    public async Task The_bounded_table_of_worktrees_never_forgets_one_whose_watch_runs()
    {
        using var sandbox = GitTestSandbox.Create();
        var watching = sandbox.CloneTracked("watching");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", watching));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading");

                    // More worktrees than the table remembers, all idle: one of them has to go.
                    for (var index = 0; index < GitPanelViewModel.MaxRememberedWorktrees; index++)
                    {
                        panel.Attach(Context("s" + index, Directory.CreateTempSubdirectory("forge-pr-evict-").FullName));
                    }

                    // The watching worktree was spared: its watch is still running when it is attached again.
                    panel.Attach(Context("s1", watching));
                    Assert.True(panel.IsWatching);

                    panel.ReleaseSession("s1");
                    gate.Release();
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
    public async Task Dispose_stops_the_watch_and_a_late_reading_never_reaches_the_panel()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var notifier = new PrAppNotifier();
        var script = PrAppScript.PendingThenBlock(gate);
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform, notifier: notifier);
        await session.Dispatch(
            async () =>
            {
                panel.Attach(Context("s1", work));
                await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                panel.WaitForChecksCommand.Execute(null);
                await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading");

                panel.Dispose();

                Assert.False(panel.IsWatching);
                Assert.False(panel.WaitForChecksCommand.CanExecute(null));
                Assert.False(panel.CancelWaitCommand.CanExecute(null));

                gate.Release();
                await QuietWindowAsync();

                Assert.Equal(string.Empty, panel.ChecksText);
                Assert.Equal(0, notifier.Count.Count);
                return 0;
            },
            CancellationToken.None);
    }

    // ------------------------------------------------------------ rendering

    [Fact]
    public async Task The_tool_view_offers_cancel_while_waiting_and_shows_the_verdict_when_it_ends()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var gate = new PrAppGate();
        var platform = new PrAppPlatform();
        var script = PrAppScript.PendingThenBlock(gate, PrKit.Passing());
        platform.OnChecks = script.ChecksAsync;
        platform.OnMerge = script.MergeAsync;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", work));
                    await WaitUntilAsync(() => panel.HasPullRequest, "the pull request");
                    panel.WaitForChecksCommand.Execute(null);
                    await WaitUntilAsync(() => panel.WatchMergeText.Length > 0, "the first reading");
                    return 0;
                },
                CancellationToken.None);

            var running = await session.Dispatch(
                () => Render(panel),
                CancellationToken.None);

            Assert.True(running.CancelButtonVisible);
            Assert.False(running.WaitButtonVisible);
            Assert.Equal(Loc.Instance[GitStrings.ChecksRunning], running.ChecksMarker);
            Assert.NotEqual(string.Empty, running.Progress);

            await session.Dispatch(
                async () =>
                {
                    gate.Release();
                    await WaitUntilAsync(() => panel.HasWatchVerdict, "the verdict");
                    return 0;
                },
                CancellationToken.None);

            var finished = await session.Dispatch(
                () => Render(panel),
                CancellationToken.None);

            Assert.Equal(Loc.Instance[GitStrings.OutcomeReady], finished.Verdict);
            Assert.True(finished.WaitButtonVisible);
            Assert.False(finished.CancelButtonVisible);
            Assert.Equal(Loc.Instance[GitStrings.ChecksPassed], finished.ChecksMarker);
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

    /// <summary>Shows the panel in a window and reads back the texts and button states of the watch.</summary>
    private static Rendered Render(GitPanelViewModel panel)
    {
        var view = new GitToolView { DataContext = panel };
        var window = new Window { Width = 480, Height = 900, Content = view };
        window.Show();
        Settle(window);
        try
        {
            var texts = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .ToList();
            return new Rendered(
                Verdict: TextOf(texts, "git-watch-verdict"),
                Progress: TextOf(texts, "git-watch-progress"),
                ChecksMarker: panel.ChecksText,
                WaitButtonVisible: Visible(window, "git-wait-checks"),
                CancelButtonVisible: Visible(window, "git-wait-cancel"));
        }
        finally
        {
            window.Close();
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

    private static string TextOf(IReadOnlyList<TextBlock> texts, string automationId) =>
        texts.FirstOrDefault(block => string.Equals(
            AutomationProperties.GetAutomationId(block),
            automationId,
            StringComparison.Ordinal))?.Text ?? string.Empty;

    private static bool Visible(Window window, string automationId) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(
                AutomationProperties.GetAutomationId(button),
                automationId,
                StringComparison.Ordinal))?.IsVisible ?? false;

    private sealed record Rendered(
        string Verdict,
        string Progress,
        string ChecksMarker,
        bool WaitButtonVisible,
        bool CancelButtonVisible);
}
