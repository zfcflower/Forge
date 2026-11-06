using Forge.App.Git;
using Forge.App.Localization;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="PrWatcher"/>: one wait on a pull request's checks, run on a pool thread over the shared
/// <see cref="PrChecksPoller"/>, with every reading and the final verdict handed to the UI thread through the
/// <c>post</c> seam and one localized notification raised when the wait ends.
/// <para>Covers the passing verdicts (CLEAN and HAS_HOOKS), every failure state the poller can report (failed checks,
/// BEHIND, DIRTY, BLOCKED, UNSTABLE, draft), the timeout at the configured maximum, cancellation (nothing is delivered
/// afterwards, a reading that was already posted included), the start/cancel/dispose state machine, the option timing
/// the wait was given, a poller that throws, and the notification path (its text, a notifier that throws or cancels).
/// Every wait is a counter or a gate trip on a fake platform with a virtual clock - nothing sleeps and nothing calls gh.
/// The post seam is a queue the test drains, which is also how the "the UI thread was the only one that touched the view
/// state" assertion is made: the callbacks record the thread that ran them.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class PrWatcherTests
{
    // ------------------------------------------------------------ the verdicts a wait can reach

    [Fact]
    public async Task A_pull_request_that_is_ready_ends_the_wait_ready_and_notifies_once()
    {
        var rig = new WatchRig(PrKit.Passing(PrMergeStateStatus.Clean));

        var watcher = rig.Create(number: 42);
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.Ready, result.Outcome);
        Assert.Equal(1, result.Polls);
        Assert.Null(result.Failure);
        Assert.Null(result.Note);

        // The one reading of the wait, delivered through the post seam.
        var observation = Assert.Single(rig.Observations);
        Assert.Equal(2, observation.PassedCount);
        Assert.Empty(observation.FailingChecks);

        Assert.False(watcher.IsRunning);
        Assert.False(watcher.IsCancelled);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);

        // One notification naming the pull request and saying how the wait ended.
        Assert.Equal(1, rig.Notifier.Count.Count);
        var call = Assert.Single(rig.Notifier.Calls);
        Assert.Equal(Loc.Instance.Format(GitStrings.NotifyTitle, 42), call.Title);
        Assert.Equal(PrWatchText.Outcome(result), call.Body);
        Assert.Equal(Loc.Instance[GitStrings.OutcomeReady], call.Body);
    }

    [Fact]
    public async Task An_HAS_HOOKS_merge_state_ends_ready_with_hooks_and_carries_the_caveat()
    {
        var rig = new WatchRig(PrKit.Passing(PrMergeStateStatus.HasHooks));

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.ReadyWithHooks, result.Outcome);
        Assert.Equal(PrChecksPoller.HooksCaveat, result.Note);

        // The notification says the pull request is fine, with the hooks wording and not the plain "ready" one.
        Assert.Equal(Loc.Instance[GitStrings.OutcomeReadyWithHooks], Assert.Single(rig.Notifier.Calls).Body);
        Assert.NotEqual(Loc.Instance[GitStrings.OutcomeReady], Assert.Single(rig.Notifier.Calls).Body);
    }

    public static TheoryData<PrMergeStateStatus, bool, PrWaitOutcome> SettledFailureStates() => new()
    {
        { PrMergeStateStatus.Behind, false, PrWaitOutcome.Behind },
        { PrMergeStateStatus.Dirty, false, PrWaitOutcome.Dirty },
        { PrMergeStateStatus.Blocked, false, PrWaitOutcome.Blocked },
        { PrMergeStateStatus.Unstable, false, PrWaitOutcome.Unstable },
        { PrMergeStateStatus.Draft, false, PrWaitOutcome.Draft },

        // The draft flag alone (with a CLEAN state) is a draft too.
        { PrMergeStateStatus.Clean, true, PrWaitOutcome.Draft },
    };

    [Theory]
    [MemberData(nameof(SettledFailureStates))]
    public async Task A_settled_pull_request_that_needs_work_ends_with_its_own_verdict(
        PrMergeStateStatus status,
        bool draft,
        PrWaitOutcome expected)
    {
        var rig = new WatchRig(PrAppStep.Of(PrKit.Merge(status, draft: draft), PrKit.Check("build", PrCheckBucket.Pass)));

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(expected, result.Outcome);
        Assert.True(PrWatchText.IsProblem(result.Outcome));
        Assert.Single(rig.Observations);
        Assert.Equal(
            Loc.Instance[expected switch
            {
                PrWaitOutcome.Behind => GitStrings.OutcomeBehind,
                PrWaitOutcome.Dirty => GitStrings.OutcomeDirty,
                PrWaitOutcome.Blocked => GitStrings.OutcomeBlocked,
                PrWaitOutcome.Unstable => GitStrings.OutcomeUnstable,
                _ => GitStrings.OutcomeDraft,
            }],
            Assert.Single(rig.Notifier.Calls).Body);
    }

    [Fact]
    public async Task A_failing_check_ends_the_wait_at_the_first_reading_and_still_reports_it()
    {
        var rig = new WatchRig(
            PrAppStep.Of(
                PrKit.Merge(),
                PrKit.Check("build", PrCheckBucket.Fail),
                PrKit.Check("lint", PrCheckBucket.Pending)));

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.ChecksFailed, result.Outcome);
        Assert.Equal(1, result.Polls);
        Assert.Equal("build", Assert.Single(result.FailedChecks).Name);

        // The reading that decided it was delivered first, with the pending check still on it.
        var observation = Assert.Single(rig.Observations);
        Assert.Single(observation.FailingChecks);
        Assert.Single(observation.PendingChecks);

        Assert.Contains("build", Assert.Single(rig.Notifier.Calls).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_checks_keep_the_wait_running_until_they_settle()
    {
        var rig = new WatchRig(
            PrKit.Pending(),
            PrAppStep.Of(PrKit.Merge(), PrKit.Check("build", PrCheckBucket.Pending)),
            PrAppStep.Of(
                PrKit.Merge(),
                PrKit.Check("build", PrCheckBucket.Pass),
                PrKit.Check("lint", PrCheckBucket.Skipping)));

        var watcher = rig.Create(new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(7), MaxWait = TimeSpan.FromMinutes(5) });
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.Ready, result.Outcome);
        Assert.Equal(3, result.Polls);

        // A reading per poll, in order: two pending ones, then the settled third.
        Assert.Equal(3, rig.Observations.Count);
        Assert.Single(rig.Observations[0].PendingChecks);
        Assert.Single(rig.Observations[1].PendingChecks);
        Assert.Empty(rig.Observations[2].PendingChecks);

        // The timing is the one the options named, not a default: one delay of 7 s between the polls.
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, rig.Time.Delays.ToArray());
    }

    [Fact]
    public async Task A_wait_that_never_settles_times_out_on_the_configured_maximum()
    {
        var rig = new WatchRig(PrAppStep.Of(PrKit.Merge(), PrKit.Check("build", PrCheckBucket.Pending)));

        var watcher = rig.Create(new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(30), MaxWait = TimeSpan.FromMinutes(2) });
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Elapsed);

        // Polls at 0, 30 s, 60 s, 90 s and one last poll at the deadline.
        Assert.Equal(5, result.Polls);
        Assert.Equal(5, rig.Observations.Count);

        // The reading before the timeout is kept, so the panel still shows where the checks stood.
        Assert.NotNull(result.Observation);
        Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeTimedOut, 2), Assert.Single(rig.Notifier.Calls).Body);
    }

    [Fact]
    public async Task A_pull_request_that_closed_while_waiting_ends_with_NotOpen()
    {
        var rig = new WatchRig(
            PrAppStep.Of(PrKit.Merge(PrMergeStateStatus.Unknown, PrState.Merged), PrKit.Check("build", PrCheckBucket.Pending)));

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.Equal(PrWaitOutcome.NotOpen, Assert.Single(rig.Results).Outcome);
        Assert.False(PrWatchText.IsProblem(PrWaitOutcome.NotOpen));
    }

    // ------------------------------------------------------------ cancellation, start, dispose

    [Fact]
    public async Task Cancelling_stops_the_wait_and_delivers_neither_a_reading_nor_a_verdict()
    {
        var gate = new PrAppGate();
        var script = PrAppScript.PendingThenBlock(gate);
        var rig = new WatchRig(script);

        var watcher = rig.Create();
        watcher.Start();
        await gate.Entered.WhenAtLeastAsync(1, "the second poll to reach the platform");
        Assert.True(watcher.IsRunning);

        watcher.Cancel();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.True(watcher.IsCancelled);
        Assert.False(watcher.IsRunning);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);

        // Nothing reached the view state and no notification was raised.
        Assert.Empty(rig.Results);
        Assert.Empty(rig.Observations);
        Assert.Equal(0, rig.Notifier.Count.Count);

        // The platform call that was in flight was the one that observed the cancellation.
        Assert.True(gate.Cancelled.Count >= 1);
    }

    [Fact]
    public async Task A_reading_that_was_already_posted_is_dropped_when_the_wait_is_cancelled()
    {
        var gate = new PrAppGate();
        var script = PrAppScript.PendingThenBlock(gate);
        var rig = new WatchRig(script);

        var watcher = rig.Create();
        watcher.Start();

        // The first poll's reading is already on the queue (the second poll is blocked at the gate).
        await gate.Entered.WhenAtLeastAsync(1, "the second poll to reach the platform");
        Assert.True(rig.Post.Pending >= 1);

        watcher.Cancel();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        // The queued reading was skipped by the guard the seam wraps it in.
        Assert.Empty(rig.Observations);
        Assert.Empty(rig.Results);
    }

    [Fact]
    public async Task Start_before_anything_completes_immediately_and_starting_twice_waits_once()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create();
        Assert.True(watcher.Completion.IsCompleted);

        watcher.Start();
        watcher.Start();

        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        // Two Start calls, one wait: the checks were read once (one poll) and one verdict was delivered.
        Assert.Equal(1, rig.Platform.Checks.Count);
        Assert.Single(rig.Results);
        Assert.Equal(1, rig.Notifier.Count.Count);
    }

    [Fact]
    public async Task Start_after_a_cancel_does_nothing()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create();
        watcher.Cancel();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.True(watcher.IsCancelled);
        Assert.Equal(0, rig.Platform.TotalCalls);
        Assert.Empty(rig.Results);
        Assert.Equal(0, rig.Notifier.Count.Count);
    }

    [Fact]
    public void Dispose_before_a_start_delivers_nothing_and_leaves_the_completion_done()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create();
        watcher.Dispose();
        watcher.Start();
        watcher.Dispose();

        rig.Drain();
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
        Assert.True(watcher.IsCancelled);
        Assert.False(watcher.IsRunning);
        Assert.Equal(0, rig.Platform.TotalCalls);
        Assert.Empty(rig.Results);
    }

    [Fact]
    public async Task Cancelling_twice_and_disposing_after_the_wait_ended_is_harmless()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        watcher.Cancel();
        watcher.Cancel();
        watcher.Dispose();
        rig.Drain();

        // The verdict that was already delivered stays delivered; nothing new arrives.
        Assert.Single(rig.Results);
        Assert.Equal(1, rig.Notifier.Count.Count);
    }

    // ------------------------------------------------------------ the UI-thread seam

    [Fact]
    public async Task Every_reading_and_the_verdict_are_delivered_on_the_thread_the_post_seam_uses()
    {
        var rig = new WatchRig(PrKit.Pending(), PrKit.Passing());

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);

        // Nothing ran yet: the watcher posted, it did not call the view state itself.
        Assert.Empty(rig.Observations);
        Assert.Empty(rig.Results);

        var drainThread = Environment.CurrentManagedThreadId;
        rig.Drain();

        Assert.Equal(2, rig.Observations.Count);
        Assert.Single(rig.Results);
        Assert.All(rig.ProgressThreads, thread => Assert.Equal(drainThread, thread));
        Assert.All(rig.CompletedThreads, thread => Assert.Equal(drainThread, thread));
    }

    [Fact]
    public async Task The_pool_thread_never_calls_a_callback_before_the_seam_runs_it()
    {
        var gate = new PrAppGate();
        var script = PrAppScript.PendingThenBlock(gate);
        var rig = new WatchRig(script);

        var watcher = rig.Create();
        watcher.Start();
        await gate.Entered.WhenAtLeastAsync(1, "the wait to be running");

        // A reading was posted and has been sitting on the queue while the wait runs; no callback has run.
        Assert.Equal(0, rig.Callbacks);
        Assert.True(watcher.IsRunning);

        watcher.Cancel();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        Assert.Equal(0, rig.Callbacks);
    }

    // ------------------------------------------------------------ a poller that cannot answer

    [Fact]
    public async Task A_poller_that_throws_becomes_a_failed_verdict_instead_of_a_faulted_task()
    {
        using var trace = new PrAppTraceCapture();
        var rig = new WatchRig(PrKit.Passing());

        // The poller rejects a zero interval, which is the shape of a bad option reaching it.
        var watcher = rig.Create(new PrChecksPollerOptions { Interval = TimeSpan.Zero });
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        var result = Assert.Single(rig.Results);
        Assert.Equal(PrWaitOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Contains("Interval", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.Polls);

        // Shown like any other failure, and the notification still says the wait is over.
        Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeFailed, result.Failure.Message), Assert.Single(rig.Notifier.Calls).Body);
        Assert.Contains(trace.Lines, static line => line.StartsWith("[watch] the wait failed:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ notifications

    [Fact]
    public async Task A_notifier_that_throws_does_not_stop_the_verdict_or_fault_the_completion()
    {
        using var trace = new PrAppTraceCapture();
        var rig = new WatchRig(PrKit.Passing());
        rig.Notifier.Behavior = static (_, _, _) => throw new InvalidOperationException("notify-boom");

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.Equal(PrWaitOutcome.Ready, Assert.Single(rig.Results).Outcome);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
        Assert.Contains(trace.Lines, static line => line.StartsWith("[watch] the notification failed: notify-boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_notifier_that_reports_cancellation_is_swallowed_too()
    {
        var rig = new WatchRig(PrKit.Passing());
        rig.Notifier.Behavior = static (_, _, _) => throw new OperationCanceledException();

        var watcher = rig.Create();
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.Equal(PrWaitOutcome.Ready, Assert.Single(rig.Results).Outcome);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Without_a_notifier_the_wait_still_delivers_its_verdict()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create(withNotifier: false);
        watcher.Start();
        await watcher.Completion.WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.Single(rig.Results);
        Assert.Equal(0, rig.Notifier.Count.Count);
    }

    [Fact]
    public async Task Cancelling_while_the_notification_runs_ends_it_quietly_and_drops_the_verdict()
    {
        var rig = new WatchRig(PrKit.Passing());

        // A notification that stays in flight until the wait's token says otherwise.
        rig.Notifier.Behavior = static (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        var watcher = rig.Create();
        watcher.Start();
        await rig.Notifier.Count.WhenAtLeastAsync(1, "the notification to run");
        Assert.True(rig.Notifier.LastToken.CanBeCanceled);
        Assert.False(watcher.Completion.IsCompleted);

        watcher.Cancel();
        await watcher.Completion.WaitAsync(PrKit.Budget);

        // The in-flight notification was cancelled with the wait (no exception escapes) ...
        Assert.True(rig.Notifier.LastToken.IsCancellationRequested);
        Assert.True(watcher.Completion.IsCompletedSuccessfully);

        // ... and the verdict that was posted just before it is dropped, because the user asked to stop.
        rig.Drain();
        Assert.Empty(rig.Results);
    }

    // ------------------------------------------------------------ two waits at once

    [Fact]
    public async Task Two_waits_on_different_worktrees_keep_their_own_readings_and_verdicts()
    {
        var rig = new WatchRig(PrKit.Passing());
        rig.Platform.UseScripts(static directory =>
            new PrAppScript(
                string.Equals(directory, "/work-a", StringComparison.Ordinal)
                    ? PrKit.Passing(PrMergeStateStatus.Clean)
                    : PrKit.Passing(PrMergeStateStatus.Behind)));

        var first = rig.Create(directory: "/work-a", number: 11);
        var second = rig.Create(directory: "/work-b", number: 12);
        first.Start();
        second.Start();
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(PrKit.Budget);
        rig.Drain();

        Assert.Equal(2, rig.Results.Count);
        Assert.Contains(rig.Results, static result => result.Outcome == PrWaitOutcome.Ready);
        Assert.Contains(rig.Results, static result => result.Outcome == PrWaitOutcome.Behind);

        // Each wait read its own worktree and named its own pull request in the notification.
        Assert.Contains("checks:/work-a", rig.Platform.Calls);
        Assert.Contains("checks:/work-b", rig.Platform.Calls);
        Assert.Equal(2, rig.Notifier.Count.Count);
        Assert.Contains(rig.Notifier.Calls, call => call.Title == Loc.Instance.Format(GitStrings.NotifyTitle, 11));
        Assert.Contains(rig.Notifier.Calls, call => call.Title == Loc.Instance.Format(GitStrings.NotifyTitle, 12));
    }

    // ------------------------------------------------------------ the arguments

    [Fact]
    public void The_arguments_are_validated()
    {
        var platform = new PrAppPlatform();
        var poller = new PrChecksPoller(platform, new PrAppManualTime());
        var options = new PrChecksPollerOptions();
        Action<Action> post = static _ => { };

        Assert.Throws<ArgumentNullException>(() => new PrWatcher(null!, "/work", 1, options, post, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentException>(() => new PrWatcher(poller, " ", 1, options, post, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentNullException>(() => new PrWatcher(poller, "/work", 1, null!, post, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrWatcher(poller, "/work", 0, options, post, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrWatcher(poller, "/work", -3, options, post, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentNullException>(() => new PrWatcher(poller, "/work", 1, options, null!, static _ => { }, static _ => { }));
        Assert.Throws<ArgumentNullException>(() => new PrWatcher(poller, "/work", 1, options, post, null!, static _ => { }));
        Assert.Throws<ArgumentNullException>(() => new PrWatcher(poller, "/work", 1, options, post, static _ => { }, null!));
    }

    [Fact]
    public void The_worktree_and_the_pull_request_are_readable_before_and_after_the_wait()
    {
        var rig = new WatchRig(PrKit.Passing());

        var watcher = rig.Create(directory: "/work-x", number: 99);

        Assert.Equal("/work-x", watcher.WorkingDirectory);
        Assert.Equal(99, watcher.Number);
        Assert.False(watcher.IsRunning);
        Assert.False(watcher.IsCancelled);
    }

    // ------------------------------------------------------------ the harness

    /// <summary>
    /// One watcher over a fake platform with a virtual clock, plus everything it delivered: the readings, the verdict,
    /// the thread each callback ran on, and the fake notifier.
    /// </summary>
    private sealed class WatchRig
    {
        private int callbacks;

        public WatchRig(params PrAppStep[] steps)
            : this(new PrAppScript(steps))
        {
        }

        public WatchRig(PrAppScript script)
            : this(script.ChecksAsync, script.MergeAsync)
        {
        }

        public WatchRig(Func<PrAppCall, Task<PrResult<PrChecksSnapshot>>> checks, Func<PrAppCall, Task<PrResult<PrMergeState>>> merge)
        {
            Platform.OnChecks = checks;
            Platform.OnMerge = merge;
        }

        public PrAppPlatform Platform { get; } = new();

        public PrAppManualTime Time { get; } = new();

        public PrAppPostQueue Post { get; } = new();

        public PrAppNotifier Notifier { get; } = new();

        public List<PrChecksObservation> Observations { get; } = [];

        public List<PrWaitResult> Results { get; } = [];

        public List<int> ProgressThreads { get; } = [];

        public List<int> CompletedThreads { get; } = [];

        /// <summary>How many of the two callbacks ran (on any thread).</summary>
        public int Callbacks => Volatile.Read(ref callbacks);

        public PrWatcher Create(
            PrChecksPollerOptions? options = null,
            bool withNotifier = true,
            int number = 7,
            string directory = "/work") =>
            new(
                new PrChecksPoller(Platform, Time),
                directory,
                number,
                options ?? new PrChecksPollerOptions(),
                Post.Post,
                observation =>
                {
                    Interlocked.Increment(ref callbacks);
                    ProgressThreads.Add(Environment.CurrentManagedThreadId);
                    Observations.Add(observation);
                },
                result =>
                {
                    Interlocked.Increment(ref callbacks);
                    CompletedThreads.Add(Environment.CurrentManagedThreadId);
                    Results.Add(result);
                },
                withNotifier ? Notifier : null);

        /// <summary>Runs everything the watcher posted; call it on the thread that owns the view state.</summary>
        public int Drain() => Post.Drain();
    }
}
