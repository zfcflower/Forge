using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <see cref="PrChecksPoller.Verdict"/> — the single-read verdict the app's checks panel shows, which must agree with the
/// wait mode's rules except in one place: an empty check list is never "no CI" without the consecutive readings only a wait
/// counts, so a snapshot of a fresh head stays undetermined instead of claiming the repository has no CI.
/// </summary>
public sealed class PrChecksVerdictTests
{
    private static readonly PrCheck Passed = PrCheckSample.Check("test", PrCheckBucket.Pass);
    private static readonly PrCheck Failed = PrCheckSample.Check("lint", PrCheckBucket.Fail);
    private static readonly PrCheck Cancelled = PrCheckSample.Check("package", PrCheckBucket.Cancel);
    private static readonly PrCheck Running = PrCheckSample.Check("build", PrCheckBucket.Pending);

    private static PrChecksObservation Observe(PrMergeState merge, params PrCheck[] checks) =>
        new(new PrChecksSnapshot(checks), merge, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_running_check_leaves_the_verdict_open()
    {
        Assert.Null(PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Passed, Running)));
    }

    [Fact]
    public void A_failed_check_decides_at_once_even_while_others_run()
    {
        Assert.Equal(PrWaitOutcome.ChecksFailed, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Failed, Running)));
    }

    [Fact]
    public void A_cancelled_check_counts_as_failed()
    {
        Assert.Equal(PrWaitOutcome.ChecksFailed, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Cancelled)));
    }

    [Fact]
    public void An_empty_snapshot_is_never_no_ci()
    {
        // The wait mode concludes "no CI" only after several consecutive empty reads; a snapshot has no such streak.
        Assert.Null(PrChecksPoller.Verdict(Observe(PrCheckSample.Merge())));
    }

    [Fact]
    public void Every_check_passed_and_clean_is_ready()
    {
        Assert.Equal(PrWaitOutcome.Ready, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Passed)));
    }

    [Fact]
    public void Skipped_and_neutral_checks_count_as_passed()
    {
        var skipped = PrCheckSample.Check("docs", PrCheckBucket.Skipping);

        Assert.Equal(PrWaitOutcome.Ready, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Passed, skipped)));
    }

    [Theory]
    [InlineData(PrMergeStateStatus.Behind, PrWaitOutcome.Behind)]
    [InlineData(PrMergeStateStatus.Dirty, PrWaitOutcome.Dirty)]
    [InlineData(PrMergeStateStatus.Blocked, PrWaitOutcome.Blocked)]
    [InlineData(PrMergeStateStatus.Unstable, PrWaitOutcome.Unstable)]
    [InlineData(PrMergeStateStatus.Draft, PrWaitOutcome.Draft)]
    public void The_merge_state_is_reported_once_the_checks_settled(PrMergeStateStatus status, PrWaitOutcome expected)
    {
        Assert.Equal(expected, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(status), Passed)));
    }

    [Fact]
    public void A_conflicting_branch_is_dirty_even_when_the_status_string_says_something_else()
    {
        var merge = PrCheckSample.Merge(PrMergeStateStatus.Unknown, mergeable: PrMergeable.Conflicting);

        Assert.Equal(PrWaitOutcome.Dirty, PrChecksPoller.Verdict(Observe(merge, Passed)));
    }

    [Fact]
    public void Hooks_are_a_ready_with_a_caveat()
    {
        Assert.Equal(PrWaitOutcome.ReadyWithHooks, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(PrMergeStateStatus.HasHooks), Passed)));
    }

    [Theory]
    [InlineData(PrState.Merged)]
    [InlineData(PrState.Closed)]
    public void A_pull_request_that_is_no_longer_open_is_not_open(PrState state)
    {
        Assert.Equal(PrWaitOutcome.NotOpen, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(state: state), Running)));
    }

    [Fact]
    public void An_unknown_merge_state_with_settled_checks_is_still_undetermined()
    {
        Assert.Null(PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(PrMergeStateStatus.Unknown), Passed)));
    }

    [Fact]
    public void An_unclassifiable_check_is_never_treated_as_passed()
    {
        var unknown = PrCheckSample.Check("external", PrCheckBucket.Unknown);

        Assert.Null(PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), Passed, unknown)));
        Assert.Equal(PrWaitOutcome.ChecksFailed, PrChecksPoller.Verdict(Observe(PrCheckSample.Merge(), unknown, Failed)));
    }

    [Fact]
    public void A_null_observation_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => PrChecksPoller.Verdict(null!));
    }

    [Fact]
    public void The_panel_order_puts_failures_first_and_passes_last()
    {
        var order = new[] { PrCheckBucket.Pass, PrCheckBucket.Fail, PrCheckBucket.Skipping, PrCheckBucket.Pending, PrCheckBucket.Cancel }
            .OrderBy(PrCheckText.BucketRank)
            .ToArray();

        Assert.Equal(
            [PrCheckBucket.Fail, PrCheckBucket.Cancel, PrCheckBucket.Pending, PrCheckBucket.Skipping, PrCheckBucket.Pass],
            order);
    }
}
