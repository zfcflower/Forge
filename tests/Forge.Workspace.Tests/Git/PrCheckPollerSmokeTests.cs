using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// State machine of <see cref="PrChecksPoller"/> against a scripted <see cref="IPrPlatform"/> and a virtual clock: every
/// delay goes through the injected <see cref="TimeProvider"/>, so a 30-minute wait takes microseconds and the arithmetic
/// of polls, delays and elapsed time can be asserted exactly.
/// </summary>
public sealed class PrCheckPollerSmokeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Waits with the default options and asserts the settled outcome together with the number of polls.</summary>
    private static async Task<PrWaitResult> SettlesAsync(PrCheckPollerRig rig, PrWaitOutcome outcome, int polls)
    {
        var result = await rig.WaitAsync();
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(polls, result.Polls);
        return result;
    }

    // ---- Ready / settled verdicts -------------------------------------------------------------------------------

    [Fact]
    public async Task Pending_checks_keep_waiting_until_everything_passed_and_the_merge_state_is_clean()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending), PrCheckSample.Check("lint", PrCheckBucket.Pass)),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass), PrCheckSample.Check("lint", PrCheckBucket.Pass)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 2);
        Assert.Equal(Interval, result.Elapsed);
        Assert.Equal([Interval], rig.Time.Delays);
        Assert.Null(result.Note);
        Assert.Empty(result.FailedChecks);
        Assert.Equal(2, result.Observation!.PassedCount);
    }

    [Fact]
    public async Task Skipped_and_neutral_checks_count_as_passed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrMergeStateStatus.Clean,
            PrCheckSample.Check("build", PrCheckBucket.Pass),
            PrCheckSample.Check("optional", PrCheckBucket.Skipping)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 1);
        Assert.Equal(2, result.Observation!.PassedCount);
        Assert.Equal(1, result.Observation.SkippedCount);
    }

    [Fact]
    public async Task Has_hooks_is_ready_with_the_hooks_caveat()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.HasHooks, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.ReadyWithHooks, result.Outcome);
        Assert.Equal(PrChecksPoller.HooksCaveat, result.Note);
        Assert.Contains("pre-receive hooks", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_merge_state_is_never_a_verdict_even_when_every_check_passed()
    {
        var passed = PrCheckSample.Check("build", PrCheckBucket.Pass);
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Unknown, passed),
            PrCheckSample.Step(PrMergeStateStatus.Unknown, passed),
            PrCheckSample.Step(PrMergeStateStatus.Clean, passed));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 3);
    }

    [Theory]
    [InlineData(PrCheckBucket.Pending)]
    [InlineData(PrCheckBucket.Unknown)]
    public async Task A_check_that_has_not_settled_blocks_ready_until_the_wait_times_out(PrCheckBucket unsettled)
    {
        // Unknown means "this build cannot classify the check": it must never be read as a pass.
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrMergeStateStatus.Clean,
            PrCheckSample.Check("build", PrCheckBucket.Pass),
            PrCheckSample.Check("deploy", unsettled)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { MaxWait = TimeSpan.FromMinutes(2) });

        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal("deploy", Assert.Single(result.Observation!.PendingChecks).Name);
    }

    // ---- Failures -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_check_ends_the_wait_at_once_even_while_other_checks_still_run()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrMergeStateStatus.Blocked,
            PrCheckSample.Check("build", PrCheckBucket.Pending),
            PrCheckSample.Check("lint", PrCheckBucket.Fail, run: 11, job: 22, link: "https://github.com/acme/forge/actions/runs/11/job/22")));

        var result = await SettlesAsync(rig, PrWaitOutcome.ChecksFailed, 1);
        Assert.Empty(rig.Time.Delays);
        var failed = Assert.Single(result.FailedChecks);
        Assert.Equal("lint", failed.Name);
        Assert.Equal(11, failed.RunId);
        Assert.Equal(22, failed.JobId);
        Assert.Equal("build", Assert.Single(result.Observation!.PendingChecks).Name);
    }

    [Fact]
    public async Task A_cancelled_check_is_reported_as_failed_and_never_as_passed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrMergeStateStatus.Clean,
            PrCheckSample.Check("build", PrCheckBucket.Pass),
            PrCheckSample.Check("e2e", PrCheckBucket.Cancel)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.ChecksFailed, result.Outcome);
        Assert.Equal("e2e", Assert.Single(result.FailedChecks).Name);
    }

    [Fact]
    public async Task A_failed_check_wins_over_every_merge_state_including_a_merge_state_not_yet_computed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Unknown, PrCheckSample.Check("lint", PrCheckBucket.Fail)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.ChecksFailed, result.Outcome);
    }

    // ---- Merge states once the checks have settled --------------------------------------------------------------

    [Theory]
    [InlineData(PrMergeStateStatus.Behind, PrWaitOutcome.Behind)]
    [InlineData(PrMergeStateStatus.Dirty, PrWaitOutcome.Dirty)]
    [InlineData(PrMergeStateStatus.Blocked, PrWaitOutcome.Blocked)]
    [InlineData(PrMergeStateStatus.Unstable, PrWaitOutcome.Unstable)]
    [InlineData(PrMergeStateStatus.Draft, PrWaitOutcome.Draft)]
    public async Task Settled_checks_return_the_merge_state_to_the_caller_without_fixing_anything(
        PrMergeStateStatus status,
        PrWaitOutcome expected)
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(status, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, result.Polls);
        Assert.Equal(1, rig.Platform.ChecksCalls);
        Assert.Equal(status, result.Observation!.MergeState.MergeStateStatus);
    }

    [Theory]
    [InlineData(PrMergeStateStatus.Behind)]
    [InlineData(PrMergeStateStatus.Dirty)]
    [InlineData(PrMergeStateStatus.Blocked)]
    [InlineData(PrMergeStateStatus.Unstable)]
    public async Task Merge_state_verdicts_wait_for_the_running_checks_to_settle_first(PrMergeStateStatus status)
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(status, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.Step(status, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(2, result.Polls);
        Assert.Equal(PrCheckBucket.Pass, Assert.Single(result.Observation!.Checks.Checks).Bucket);
    }

    [Theory]
    [InlineData(PrMergeStateStatus.Clean)]
    [InlineData(PrMergeStateStatus.Blocked)]
    [InlineData(PrMergeStateStatus.Unknown)]
    public async Task The_draft_flag_alone_makes_a_settled_pull_request_a_draft(PrMergeStateStatus status)
    {
        // GitHub reports BLOCKED (or, while computing, UNKNOWN) for drafts; the flag is the reliable signal.
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(status, draft: true),
            PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.Draft, result.Outcome);
    }

    [Fact]
    public async Task Conflicting_mergeable_is_dirty_even_when_the_merge_state_is_not_computed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Unknown, mergeable: PrMergeable.Conflicting),
            PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.Dirty, result.Outcome);
    }

    [Fact]
    public async Task A_review_decision_does_not_change_the_verdict_but_stays_in_the_observation()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Clean, review: PrReviewDecision.ChangesRequested),
            PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.Ready, result.Outcome);
        Assert.Equal(PrReviewDecision.ChangesRequested, result.Observation!.MergeState.ReviewDecision);
    }

    [Theory]
    [InlineData(PrState.Merged)]
    [InlineData(PrState.Closed)]
    public async Task A_merged_or_closed_pull_request_ends_the_wait_whatever_the_checks_say(PrState state)
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Unknown, state: state),
            PrCheckSample.Check("build", PrCheckBucket.Pending),
            PrCheckSample.Check("lint", PrCheckBucket.Fail)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.NotOpen, result.Outcome);
        Assert.Equal(state, result.Observation!.MergeState.State);
    }

    // ---- "No checks" confirmation -------------------------------------------------------------------------------

    [Fact]
    public async Task No_ci_is_only_concluded_after_three_consecutive_empty_reads_by_default()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await SettlesAsync(rig, PrWaitOutcome.NoCi, 3);
        Assert.Equal([Interval, Interval], rig.Time.Delays);
        Assert.True(result.Observation!.Checks.NoneReported);
    }

    [Fact]
    public async Task The_number_of_confirmations_is_configurable()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { NoChecksConfirmations = 5 });

        Assert.Equal(PrWaitOutcome.NoCi, result.Outcome);
        Assert.Equal(5, result.Polls);
    }

    [Fact]
    public async Task A_single_confirmation_concludes_no_ci_on_the_first_empty_read()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { NoChecksConfirmations = 1 });

        Assert.Equal(PrWaitOutcome.NoCi, result.Outcome);
        Assert.Equal(1, result.Polls);
    }

    [Fact]
    public async Task Checks_that_register_late_cancel_the_no_ci_conclusion()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 4);
    }

    [Fact]
    public async Task The_empty_streak_restarts_after_a_check_appears_and_vanishes()
    {
        // empty, empty, a check shows up, empty, empty, empty: the first two reads do not count towards the final three.
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await SettlesAsync(rig, PrWaitOutcome.NoCi, 6);
    }

    [Fact]
    public async Task A_failed_read_breaks_the_run_of_empty_confirmations()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.Step(PrMergeStateStatus.Clean),
            PrCheckSample.FailedRead(),
            PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await SettlesAsync(rig, PrWaitOutcome.NoCi, 6);
    }

    [Theory]
    [InlineData(PrMergeStateStatus.Dirty, PrWaitOutcome.Dirty)]
    [InlineData(PrMergeStateStatus.Behind, PrWaitOutcome.Behind)]
    [InlineData(PrMergeStateStatus.Blocked, PrWaitOutcome.Blocked)]
    public async Task Without_checks_a_real_problem_is_reported_instead_of_no_ci(PrMergeStateStatus status, PrWaitOutcome expected)
    {
        // Workflows often do not run for a conflicting pull request, so the merge state is the more useful answer.
        var rig = new PrCheckPollerRig(PrCheckSample.Step(status));

        var result = await rig.WaitAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(3, result.Polls);
    }

    [Fact]
    public async Task No_checks_with_a_merge_state_still_unknown_keeps_waiting_after_the_confirmations()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Unknown),
            PrCheckSample.Step(PrMergeStateStatus.Unknown),
            PrCheckSample.Step(PrMergeStateStatus.Unknown),
            PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await SettlesAsync(rig, PrWaitOutcome.NoCi, 4);
    }

    [Fact]
    public async Task No_ci_with_has_hooks_still_carries_the_hooks_caveat()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.HasHooks));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.NoCi, result.Outcome);
        Assert.Equal(PrChecksPoller.HooksCaveat, result.Note);
    }

    // ---- Platform failures --------------------------------------------------------------------------------------

    [Fact]
    public async Task Transient_failures_are_retried_on_the_next_tick()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.FailedRead(GitRemoteFailureKind.Network),
            PrCheckSample.FailedRead(GitRemoteFailureKind.Network),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 3);
        Assert.Equal([Interval, Interval], rig.Time.Delays);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Three_consecutive_failures_end_the_wait_with_the_last_failure_attached()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.AuthenticationFailed, "token rejected"));

        var result = await SettlesAsync(rig, PrWaitOutcome.Failed, 3);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Elapsed);
        Assert.Null(result.Observation);
        Assert.Equal(GitRemoteFailureKind.AuthenticationFailed, result.Failure!.Failure);
        Assert.Contains("token rejected", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_success_resets_the_consecutive_failure_count()
    {
        var pending = PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending));
        var rig = new PrCheckPollerRig(
            PrCheckSample.FailedRead(),
            PrCheckSample.FailedRead(),
            pending,
            PrCheckSample.FailedRead(),
            PrCheckSample.FailedRead(),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 6);
    }

    [Fact]
    public async Task Failing_after_a_successful_read_keeps_the_last_observation()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.FailedRead(GitRemoteFailureKind.Network));

        var result = await SettlesAsync(rig, PrWaitOutcome.Failed, 4);
        Assert.Equal("build", Assert.Single(result.Observation!.Checks.Checks).Name);
    }

    [Fact]
    public async Task A_missing_platform_cli_is_not_retried()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.Other, reason: PrFailureReason.PlatformNotInstalled));

        var result = await SettlesAsync(rig, PrWaitOutcome.Failed, 1);
        Assert.Equal(PrFailureReason.PlatformNotInstalled, result.FailureReason);
        Assert.Empty(rig.Time.Delays);
    }

    [Fact]
    public async Task A_pull_request_that_is_not_found_yet_is_retried_because_a_fresh_one_can_lag_behind()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.FailedRead(GitRemoteFailureKind.Other, reason: PrFailureReason.PullRequestNotFound),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await SettlesAsync(rig, PrWaitOutcome.Ready, 2);
    }

    [Fact]
    public async Task A_failing_merge_state_read_counts_as_a_failed_poll_even_when_the_checks_were_read()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.MergeReadFails(GitRemoteFailureKind.Network, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.Failed, result.Outcome);
        Assert.Equal(GitRemoteFailureKind.Network, result.Failure!.Failure);
        Assert.Equal(3, result.Polls);
        Assert.Null(result.Observation);
    }

    // ---- Maximum wait -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_wait_times_out_with_a_last_poll_at_the_deadline_and_the_last_observation()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { MaxWait = TimeSpan.FromMinutes(5) });

        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(11, result.Polls);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Elapsed);
        Assert.Equal(10, rig.Time.Delays.Count);
        Assert.All(rig.Time.Delays, delay => Assert.Equal(Interval, delay));
        Assert.Equal("build", Assert.Single(result.Observation!.PendingChecks).Name);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task The_default_maximum_wait_is_thirty_minutes()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));

        var result = await rig.WaitAsync();

        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Elapsed);
        Assert.Equal(61, result.Polls);
    }

    [Fact]
    public async Task The_last_delay_is_shortened_to_the_remaining_time()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { MaxWait = TimeSpan.FromSeconds(70) });

        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(4, result.Polls);
        Assert.Equal([Interval, Interval, TimeSpan.FromSeconds(10)], rig.Time.Delays);
        Assert.Equal(TimeSpan.FromSeconds(70), result.Elapsed);
    }

    [Fact]
    public async Task A_result_found_by_the_poll_at_the_deadline_beats_the_timeout()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { MaxWait = Interval });

        Assert.Equal(PrWaitOutcome.Ready, result.Outcome);
        Assert.Equal(2, result.Polls);
    }

    [Fact]
    public async Task A_merge_state_that_never_gets_computed_times_out_instead_of_passing()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Unknown, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { MaxWait = TimeSpan.FromMinutes(1) });

        Assert.Equal(PrWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal(PrMergeStateStatus.Unknown, result.Observation!.MergeState.MergeStateStatus);
    }

    [Fact]
    public async Task Timing_options_are_honoured_through_the_time_provider()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));

        var result = await rig.WaitAsync(new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(7), MaxWait = TimeSpan.FromSeconds(21) });

        Assert.Equal(4, result.Polls);
        Assert.Equal(3, rig.Time.Delays.Count);
        Assert.All(rig.Time.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(7), delay));
    }

    // ---- Cancellation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_cancelled_before_the_start_throws_without_touching_the_platform()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.WaitAsync(ct: cancellation.Token));

        Assert.Equal(0, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_between_polls_throws_instead_of_returning_a_result()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));
        rig.Platform.ThrowWhenCancelled = false;
        rig.Platform.OnChecks = index =>
        {
            if (index == 1)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.WaitAsync(ct: cancellation.Token));

        Assert.Equal(2, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Cancellation_thrown_by_the_platform_is_not_mistaken_for_a_failed_read()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)));
        rig.Platform.OnChecks = index =>
        {
            if (index == 0)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.WaitAsync(ct: cancellation.Token));

        Assert.Equal(1, rig.Platform.ChecksCalls);
    }

    // ---- Progress and observation -------------------------------------------------------------------------------

    [Fact]
    public async Task Progress_is_reported_once_per_successful_reading_and_not_for_failed_reads()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.FailedRead(),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pending)),
            PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));
        var progress = new PrCheckCollectingProgress();

        var result = await rig.WaitAsync(progress: progress);

        Assert.Equal(PrWaitOutcome.Ready, result.Outcome);
        Assert.Equal(3, result.Polls);
        Assert.Equal(2, progress.Items.Count);
        Assert.Equal(PrCheckBucket.Pending, progress.Items[0].Checks.Checks[0].Bucket);
        Assert.Equal(PrCheckBucket.Pass, progress.Items[1].Checks.Checks[0].Bucket);
        Assert.Same(progress.Items[1], result.Observation);
    }

    [Fact]
    public async Task Observe_reads_checks_and_merge_state_once_and_stamps_the_time()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Behind, review: PrReviewDecision.Approved),
            PrCheckSample.Check("build", PrCheckBucket.Pass)));

        var read = await rig.Poller.ObserveAsync("/work", 7);

        Assert.True(read.Success);
        Assert.Equal(1, rig.Platform.ChecksCalls);
        Assert.Equal(1, rig.Platform.MergeCalls);
        Assert.Equal((7, "/work"), (rig.Platform.LastNumber, rig.Platform.LastDirectory));
        Assert.Equal(PrMergeStateStatus.Behind, read.Value!.MergeState.MergeStateStatus);
        Assert.Equal(PrReviewDecision.Approved, read.Value.MergeState.ReviewDecision);
        Assert.Equal(rig.Time.GetUtcNow(), read.Value.ObservedAt);
    }

    [Fact]
    public async Task Observe_reports_the_failure_of_the_checks_read_with_its_reason()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.Other, "gone", PrFailureReason.PullRequestNotFound));

        var read = await rig.Poller.ObserveAsync("/work", 7);

        Assert.False(read.Success);
        Assert.Equal(PrFailureReason.PullRequestNotFound, read.Reason);
        Assert.Equal("gone", read.Status.Message);
    }

    // ---- Argument validation ------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_options_and_numbers_are_rejected_before_any_platform_call()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, PrCheckSample.Check("build", PrCheckBucket.Pass)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.WaitAsync(new PrChecksPollerOptions { Interval = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.WaitAsync(new PrChecksPollerOptions { MaxWait = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.WaitAsync(new PrChecksPollerOptions { NoChecksConfirmations = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Poller.WaitAsync("/work", 0, new PrChecksPollerOptions(), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Poller.ObserveAsync("/work", -1));

        Assert.Equal(0, rig.Platform.ChecksCalls);
    }
}

/// <summary>A poller wired to a scripted platform and a virtual clock.</summary>
internal sealed class PrCheckPollerRig
{
    public PrCheckPollerRig(params PrCheckStep[] script)
    {
        Platform = new PrCheckFakePlatform(script);
        Time = new PrCheckManualTime();
        Poller = new PrChecksPoller(Platform, Time);
    }

    public PrCheckFakePlatform Platform { get; }

    public PrCheckManualTime Time { get; }

    public PrChecksPoller Poller { get; }

    /// <summary>Runs one wait; the real-time cap only turns a hang (a delay that bypassed the fake clock) into a failure.</summary>
    public Task<PrWaitResult> WaitAsync(
        PrChecksPollerOptions? options = null,
        IProgress<PrChecksObservation>? progress = null,
        CancellationToken ct = default) =>
        Poller.WaitAsync("/work", 7, options ?? new PrChecksPollerOptions(), progress, ct).WaitAsync(TimeSpan.FromSeconds(20));
}

/// <summary>Collects progress synchronously (unlike <see cref="Progress{T}"/>, which posts to the thread pool).</summary>
internal sealed class PrCheckCollectingProgress : IProgress<PrChecksObservation>
{
    public List<PrChecksObservation> Items { get; } = [];

    public void Report(PrChecksObservation value) => Items.Add(value);
}

/// <summary>
/// A <see cref="TimeProvider"/> whose delays elapse instantly: creating a timer advances the virtual clock by its due time
/// and fires it. Every requested delay is recorded so tests can assert the intervals the code asked for.
/// </summary>
internal sealed class PrCheckManualTime : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private long ticks;

    public List<TimeSpan> Delays { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => Origin.AddTicks(ticks);

    public override long GetTimestamp() => ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Delays.Add(dueTime);
            ticks += dueTime.Ticks;
            callback(state);
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>One scripted poll: what the checks read and the merge-state read return.</summary>
internal sealed record PrCheckStep(PrResult<PrChecksSnapshot> Checks, PrResult<PrMergeState> Merge);

internal static class PrCheckSample
{
    public static PrCheck Check(
        string name,
        PrCheckBucket bucket,
        long? run = null,
        long? job = null,
        string? link = null,
        string workflow = "CI",
        string state = "",
        string description = "") =>
        new(name, workflow, state, bucket, description, link, null, null, run, job);

    public static PrMergeState Merge(
        PrMergeStateStatus status = PrMergeStateStatus.Clean,
        bool draft = false,
        PrState state = PrState.Open,
        PrReviewDecision review = PrReviewDecision.None,
        PrMergeable mergeable = PrMergeable.Mergeable) =>
        new(
            state,
            draft,
            status,
            review,
            mergeable,
            status switch
            {
                PrMergeStateStatus.Clean => "CLEAN",
                PrMergeStateStatus.HasHooks => "HAS_HOOKS",
                PrMergeStateStatus.Behind => "BEHIND",
                PrMergeStateStatus.Dirty => "DIRTY",
                PrMergeStateStatus.Blocked => "BLOCKED",
                PrMergeStateStatus.Unstable => "UNSTABLE",
                PrMergeStateStatus.Draft => "DRAFT",
                _ => "UNKNOWN",
            },
            review switch
            {
                PrReviewDecision.Approved => "APPROVED",
                PrReviewDecision.ChangesRequested => "CHANGES_REQUESTED",
                PrReviewDecision.ReviewRequired => "REVIEW_REQUIRED",
                _ => string.Empty,
            },
            mergeable switch
            {
                PrMergeable.Mergeable => "MERGEABLE",
                PrMergeable.Conflicting => "CONFLICTING",
                _ => "UNKNOWN",
            });

    public static PrCheckStep Step(PrMergeStateStatus status, params PrCheck[] checks) => Step(Merge(status), checks);

    public static PrCheckStep Step(PrMergeState merge, params PrCheck[] checks) =>
        new(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(checks)), PrResult<PrMergeState>.Ok(merge));

    /// <summary>Both reads of the poll fail.</summary>
    public static PrCheckStep FailedRead(
        GitRemoteFailureKind kind = GitRemoteFailureKind.Network,
        string message = "gh failed",
        PrFailureReason reason = PrFailureReason.None) =>
        new(
            PrResult<PrChecksSnapshot>.Fail(kind, message, "stderr", reason),
            PrResult<PrMergeState>.Fail(kind, message, "stderr", reason));

    /// <summary>The checks read succeeds and the merge-state read fails.</summary>
    public static PrCheckStep MergeReadFails(GitRemoteFailureKind kind, params PrCheck[] checks) =>
        new(
            PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(checks)),
            PrResult<PrMergeState>.Fail(kind, "merge state unavailable", "stderr"));
}

/// <summary>
/// An <see cref="IPrPlatform"/> that answers the n-th poll with the n-th scripted step (the last step repeats). Only the
/// calls the check tools make are implemented.
/// </summary>
internal sealed class PrCheckFakePlatform(IReadOnlyList<PrCheckStep> script) : ThrowingPrPlatform
{
    private int checksCalls;
    private int mergeCalls;
    private int viewCalls;

    public override string Name => "Fake";

    /// <summary>Invoked with the zero-based poll index at the start of every checks read; tests cancel from here.</summary>
    public Action<int>? OnChecks { get; set; }

    /// <summary>Whether a read observes a cancelled token the way a real platform does (by throwing).</summary>
    public bool ThrowWhenCancelled { get; set; } = true;

    public PrResult<PullRequestInfo> ViewResult { get; set; } = PrResult<PullRequestInfo>.Ok(
        new PullRequestInfo(
            7,
            "https://github.com/acme/forge/pull/7",
            PrState.Open,
            "Title",
            "Body",
            "feature",
            "main",
            "abc",
            false,
            PrMergeStateStatus.Clean,
            PrReviewDecision.None));

    public int ChecksCalls => Volatile.Read(ref checksCalls);

    public int MergeCalls => Volatile.Read(ref mergeCalls);

    public int ViewCalls => Volatile.Read(ref viewCalls);

    public string? LastSelector { get; private set; }

    public int LastNumber { get; private set; }

    public string? LastDirectory { get; private set; }

    public override Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref viewCalls);
        LastSelector = selector;
        return Task.FromResult(ViewResult);
    }

    public override Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref checksCalls) - 1;
        LastNumber = number;
        LastDirectory = workingDirectory;
        OnChecks?.Invoke(index);
        if (ThrowWhenCancelled)
        {
            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult(script[Math.Min(index, script.Count - 1)].Checks);
    }

    public override Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref mergeCalls) - 1;
        return Task.FromResult(script[Math.Min(index, script.Count - 1)].Merge);
    }
}
