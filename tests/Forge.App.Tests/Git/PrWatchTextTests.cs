using Forge.App.Git;
using Forge.App.Localization;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="PrWatchText"/>, the one place the wait's wording lives: the live progress line, the merge state, one
/// sentence per <see cref="PrWaitOutcome"/> (which is also the body of the system notification), the problem flag that
/// colours the verdict, and the short status-bar marker for a running and a finished wait. The expected strings are built
/// from <see cref="Loc"/> and the table's own keys, so the assertions hold in either language; a separate test checks
/// that every outcome has a text at all, so a new enum member cannot ship without words.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PrWatchTextTests
{
    // ------------------------------------------------------------ the progress line

    [Fact]
    public void A_reading_without_checks_says_no_checks_reported_yet()
    {
        var observation = PrKit.Observation(PrMergeStateStatus.Clean);

        Assert.True(observation.Checks.NoneReported);
        Assert.Equal(Loc.Instance[GitStrings.WatchNoChecksYet], PrWatchText.Progress(observation));
    }

    [Fact]
    public void The_progress_line_counts_passed_failed_and_pending_checks()
    {
        var observation = PrKit.Observation(
            PrMergeStateStatus.Clean,
            PrKit.Check("build", PrCheckBucket.Pass),
            PrKit.Check("lint", PrCheckBucket.Pass),
            PrKit.Check("e2e", PrCheckBucket.Fail),
            PrKit.Check("docs", PrCheckBucket.Pending),
            PrKit.Check("perf", PrCheckBucket.Pending));

        Assert.Equal(
            Loc.Instance.Format(GitStrings.WatchProgress, 2, 1, 2),
            PrWatchText.Progress(observation));
    }

    [Fact]
    public void Skipped_checks_count_as_passed_and_cancelled_ones_do_not()
    {
        var observation = PrKit.Observation(
            PrMergeStateStatus.Clean,
            PrKit.Check("build", PrCheckBucket.Skipping),
            PrKit.Check("lint", PrCheckBucket.Pass),
            PrKit.Check("e2e", PrCheckBucket.Cancel));

        // Two passed (one of them skipped), one failed (the cancelled check), none pending.
        Assert.Equal(
            Loc.Instance.Format(GitStrings.WatchProgress, 2, 1, 0),
            PrWatchText.Progress(observation));
    }

    [Fact]
    public void An_unclassifiable_check_is_pending_not_passed()
    {
        var observation = PrKit.Observation(
            PrMergeStateStatus.Unknown,
            PrKit.Check("build", PrCheckBucket.Unknown));

        Assert.Equal(Loc.Instance.Format(GitStrings.WatchProgress, 0, 0, 1), PrWatchText.Progress(observation));
    }

    // ------------------------------------------------------------ the merge state line

    [Theory]
    [InlineData(PrMergeStateStatus.Clean)]
    [InlineData(PrMergeStateStatus.HasHooks)]
    [InlineData(PrMergeStateStatus.Behind)]
    [InlineData(PrMergeStateStatus.Dirty)]
    [InlineData(PrMergeStateStatus.Blocked)]
    [InlineData(PrMergeStateStatus.Unstable)]
    [InlineData(PrMergeStateStatus.Draft)]
    public void The_merge_state_line_shows_the_platforms_own_word(PrMergeStateStatus status)
    {
        var observation = PrKit.Observation(status);

        var expected = status switch
        {
            PrMergeStateStatus.Clean => "CLEAN",
            PrMergeStateStatus.HasHooks => "HAS_HOOKS",
            PrMergeStateStatus.Behind => "BEHIND",
            PrMergeStateStatus.Dirty => "DIRTY",
            PrMergeStateStatus.Blocked => "BLOCKED",
            PrMergeStateStatus.Unstable => "UNSTABLE",
            _ => "DRAFT",
        };

        Assert.Equal(Loc.Instance.Format(GitStrings.WatchMergeState, expected), PrWatchText.MergeState(observation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_merge_state_text_reads_as_UNKNOWN(string raw)
    {
        var observation = new PrChecksObservation(
            new PrChecksSnapshot([]),
            PrKit.Merge() with { MergeStateStatusText = raw },
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            Loc.Instance.Format(GitStrings.WatchMergeState, "UNKNOWN"),
            PrWatchText.MergeState(observation));
    }

    [Fact]
    public void The_merge_state_text_is_trimmed_but_not_translated()
    {
        var observation = new PrChecksObservation(
            new PrChecksSnapshot([]),
            PrKit.Merge() with { MergeStateStatusText = "  A_STATE_THIS_BUILD_NEVER_HEARD_OF  " },
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            Loc.Instance.Format(GitStrings.WatchMergeState, "A_STATE_THIS_BUILD_NEVER_HEARD_OF"),
            PrWatchText.MergeState(observation));
    }

    // ------------------------------------------------------------ one sentence per outcome

    public static TheoryData<PrWaitOutcome, string> OutcomeKeys() => new()
    {
        { PrWaitOutcome.Ready, GitStrings.OutcomeReady },
        { PrWaitOutcome.ReadyWithHooks, GitStrings.OutcomeReadyWithHooks },
        { PrWaitOutcome.Behind, GitStrings.OutcomeBehind },
        { PrWaitOutcome.Dirty, GitStrings.OutcomeDirty },
        { PrWaitOutcome.Blocked, GitStrings.OutcomeBlocked },
        { PrWaitOutcome.Unstable, GitStrings.OutcomeUnstable },
        { PrWaitOutcome.Draft, GitStrings.OutcomeDraft },
        { PrWaitOutcome.NoCi, GitStrings.OutcomeNoCi },
        { PrWaitOutcome.NotOpen, GitStrings.OutcomeNotOpen },
    };

    [Theory]
    [MemberData(nameof(OutcomeKeys))]
    public void Each_verdict_has_its_own_sentence(PrWaitOutcome outcome, string key) =>
        Assert.Equal(Loc.Instance[key], PrWatchText.Outcome(PrKit.Result(outcome)));

    [Fact]
    public void Every_outcome_the_poller_can_report_has_a_sentence()
    {
        foreach (var outcome in Enum.GetValues<PrWaitOutcome>())
        {
            var text = PrWatchText.Outcome(PrKit.Result(outcome));

            Assert.False(string.IsNullOrWhiteSpace(text), "no text for " + outcome);
        }
    }

    [Fact]
    public void A_failed_verdict_lists_the_failing_checks()
    {
        var observation = PrKit.Observation(
            PrMergeStateStatus.Clean,
            PrKit.Check("build", PrCheckBucket.Fail),
            PrKit.Check("lint", PrCheckBucket.Fail));

        Assert.Equal(
            Loc.Instance.Format(GitStrings.OutcomeChecksFailed, 2, "build, lint"),
            PrWatchText.Outcome(PrKit.Result(PrWaitOutcome.ChecksFailed, observation)));
    }

    [Fact]
    public void A_failed_verdict_stops_listing_names_after_five_and_says_how_many_more()
    {
        var checks = Enumerable.Range(1, 7).Select(index => PrKit.Check("check-" + index, PrCheckBucket.Fail)).ToArray();
        var observation = PrKit.Observation(PrMergeStateStatus.Clean, checks);

        Assert.Equal(
            Loc.Instance.Format(GitStrings.OutcomeChecksFailed, 7, "check-1, check-2, check-3, check-4, check-5, … (+2)"),
            PrWatchText.Outcome(PrKit.Result(PrWaitOutcome.ChecksFailed, observation)));
    }

    [Fact]
    public void A_failed_verdict_without_an_observation_says_zero_and_no_names()
    {
        Assert.Equal(
            Loc.Instance.Format(GitStrings.OutcomeChecksFailed, 0, string.Empty),
            PrWatchText.Outcome(PrKit.Result(PrWaitOutcome.ChecksFailed)));
    }

    [Fact]
    public void A_platform_failure_says_why_and_a_missing_gh_says_install_it()
    {
        var failed = PrKit.Result(
            PrWaitOutcome.Failed,
            failure: new GitRemoteResult(GitRemoteFailureKind.Network, "  the network is down  ", "stderr"));

        Assert.Equal(
            Loc.Instance.Format(GitStrings.OutcomeFailed, "the network is down"),
            PrWatchText.Outcome(failed));

        var notInstalled = PrKit.Result(PrWaitOutcome.Failed, reason: PrFailureReason.PlatformNotInstalled);
        Assert.Equal(Loc.Instance[GitStrings.PullRequestGhNotReady], PrWatchText.Outcome(notInstalled));
    }

    [Fact]
    public void A_failed_verdict_without_a_reason_still_says_something()
    {
        var text = PrWatchText.Outcome(PrKit.Result(PrWaitOutcome.Failed));

        Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeFailed, PrWaitOutcome.Failed.ToString()), text);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 1)]
    [InlineData(60, 1)]
    [InlineData(90, 2)]
    [InlineData(150, 3)]
    [InlineData(1800, 30)]
    public void A_timeout_says_how_many_minutes_the_wait_lasted_rounded_up(int seconds, int minutes)
    {
        var result = PrKit.Result(PrWaitOutcome.TimedOut, elapsed: TimeSpan.FromSeconds(seconds));

        Assert.Equal(Loc.Instance.Format(GitStrings.OutcomeTimedOut, minutes), PrWatchText.Outcome(result));
    }

    // ------------------------------------------------------------ the problem flag

    [Theory]
    [InlineData(PrWaitOutcome.Ready, false)]
    [InlineData(PrWaitOutcome.ReadyWithHooks, false)]
    [InlineData(PrWaitOutcome.NoCi, false)]
    [InlineData(PrWaitOutcome.NotOpen, false)]
    [InlineData(PrWaitOutcome.ChecksFailed, true)]
    [InlineData(PrWaitOutcome.Behind, true)]
    [InlineData(PrWaitOutcome.Dirty, true)]
    [InlineData(PrWaitOutcome.Blocked, true)]
    [InlineData(PrWaitOutcome.Unstable, true)]
    [InlineData(PrWaitOutcome.Draft, true)]
    [InlineData(PrWaitOutcome.TimedOut, true)]
    [InlineData(PrWaitOutcome.Failed, true)]
    public void Only_the_outcomes_that_need_nothing_from_the_user_are_not_a_problem(
        PrWaitOutcome outcome,
        bool problem) =>
        Assert.Equal(problem, PrWatchText.IsProblem(outcome));

    // ------------------------------------------------------------ the status bar marker

    [Fact]
    public void Before_the_first_reading_the_marker_says_the_checks_are_running()
    {
        Assert.Equal(Loc.Instance[GitStrings.ChecksRunning], PrWatchText.Running(null));
        Assert.Equal(
            Loc.Instance[GitStrings.ChecksRunning],
            PrWatchText.Running(PrKit.Observation(PrMergeStateStatus.Clean)));
    }

    [Fact]
    public void A_failing_check_turns_the_running_marker_into_a_cross_at_once()
    {
        var observation = PrKit.Observation(
            PrMergeStateStatus.Clean,
            PrKit.Check("build", PrCheckBucket.Fail),
            PrKit.Check("lint", PrCheckBucket.Pending));

        Assert.Equal(Loc.Instance[GitStrings.ChecksFailed], PrWatchText.Running(observation));
    }

    [Theory]
    [InlineData(PrWaitOutcome.Ready, GitStrings.ChecksPassed)]
    [InlineData(PrWaitOutcome.ReadyWithHooks, GitStrings.ChecksPassed)]
    [InlineData(PrWaitOutcome.ChecksFailed, GitStrings.ChecksFailed)]
    [InlineData(PrWaitOutcome.NoCi, GitStrings.ChecksNone)]
    [InlineData(PrWaitOutcome.Behind, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.Dirty, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.Blocked, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.Unstable, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.Draft, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.TimedOut, GitStrings.ChecksAttention)]
    [InlineData(PrWaitOutcome.Failed, GitStrings.ChecksAttention)]
    public void The_finished_marker_says_how_the_checks_stood(PrWaitOutcome outcome, string key) =>
        Assert.Equal(Loc.Instance[key], PrWatchText.Finished(outcome));

    [Fact]
    public void A_merged_or_closed_pull_request_leaves_no_marker()
    {
        Assert.Equal(string.Empty, PrWatchText.Finished(PrWaitOutcome.NotOpen));
    }

    // ------------------------------------------------------------ the arguments

    [Fact]
    public void The_arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() => PrWatchText.Progress(null!));
        Assert.Throws<ArgumentNullException>(() => PrWatchText.MergeState(null!));
        Assert.Throws<ArgumentNullException>(() => PrWatchText.Outcome(null!));
    }
}
