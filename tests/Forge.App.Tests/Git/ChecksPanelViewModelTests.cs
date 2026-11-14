using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the pull request checks panel (<see cref="ChecksPanelViewModel"/>) without Avalonia: every poll runs against a
/// scripted <see cref="IPrPlatform"/> and a clock whose delays elapse instantly, the panel gets the inline <c>post</c> the
/// spec names for tests, so a whole read finishes inside <c>Start()</c>. Covered here: the failure-first row order and the
/// verdict mapping (outcome, verdict text, ready/failed/warning/waiting), the bucket counts and the empty reading that is
/// never concluded as "no CI", a failed read that keeps the last reading next to the problem, the poll stopping at a
/// verdict and resuming on refresh, dispose cancelling a read in flight, mark-ready (draft only, success re-reads, failure
/// does not), the hand-off of the failing checks with one job log each, a row's log (read once, reopened from the cache,
/// kept across the next poll, the no-job case, the platform's capture limit), the links, and close/dispose. Nothing sleeps:
/// a read a test wants to observe mid-flight is held by a completion source.
/// </summary>
public sealed class ChecksPanelViewModelTests
{
    private const int Number = 12;
    private const string WorkDirectory = "work";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ the first reading

    [Fact]
    public void A_reading_lists_the_checks_failure_first_and_maps_the_verdict()
    {
        // The input order and the names both contradict the expected order on purpose: only the bucket rank can produce it.
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("z-pass", PrCheckBucket.Pass),
            ChecksSample.Check("b-pending", PrCheckBucket.Pending),
            ChecksSample.Check("a-skip", PrCheckBucket.Skipping),
            ChecksSample.Check("c-cancel", PrCheckBucket.Cancel),
            ChecksSample.Check("d-fail", PrCheckBucket.Fail)));

        using var panel = NewPanel(platform);
        panel.Start();

        Assert.Equal(["d-fail", "c-cancel", "b-pending", "a-skip", "z-pass"], panel.Checks.Select(row => row.Name));
        Assert.Equal(PrWaitOutcome.ChecksFailed, panel.Outcome);
        Assert.Equal(Loc.Instance[ChecksStrings.VerdictFailed], panel.VerdictText);
        Assert.True(panel.IsFailed);
        Assert.False(panel.IsReady);
        Assert.False(panel.IsWarning);
        Assert.False(panel.IsWaiting);
        Assert.True(panel.HasChecks);
        Assert.Equal("#12", panel.NumberText);
        Assert.Equal(Number, platform.LastNumber);
        Assert.Equal(WorkDirectory, platform.LastDirectory);
    }

    [Fact]
    public void The_summary_counts_passed_failed_running_and_skipped()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Pass),
            ChecksSample.Check("test", PrCheckBucket.Pass),
            ChecksSample.Check("lint", PrCheckBucket.Fail),
            ChecksSample.Check("deploy", PrCheckBucket.Pending),
            ChecksSample.Check("optional", PrCheckBucket.Skipping)));

        using var panel = NewPanel(platform);
        panel.Start();

        var expected = string.Join(
            Loc.Instance[ChecksStrings.CountSeparator],
            Loc.Instance.Format(ChecksStrings.CountPassed, 2),
            Loc.Instance.Format(ChecksStrings.CountFailed, 1),
            Loc.Instance.Format(ChecksStrings.CountPending, 1),
            Loc.Instance.Format(ChecksStrings.CountSkipped, 1));
        Assert.Equal(expected, panel.SummaryText);
        Assert.True(panel.HasSummary);
    }

    [Fact]
    public void The_summary_hides_the_buckets_that_have_no_check()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("lint", PrCheckBucket.Fail),
            ChecksSample.Check("build", PrCheckBucket.Fail)));

        using var panel = NewPanel(platform);
        panel.Start();

        Assert.Equal(Loc.Instance.Format(ChecksStrings.CountFailed, 2), panel.SummaryText);
        Assert.DoesNotContain("0", panel.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_reading_is_shown_as_empty_and_keeps_polling_instead_of_no_ci()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.EmptyRead()) { HoldFrom = 1 };

        using var panel = NewPanel(platform);
        panel.Start();
        await platform.ReadStartedAsync(1);

        Assert.Empty(panel.Checks);
        Assert.False(panel.HasChecks);
        Assert.True(panel.IsEmpty);
        Assert.Null(panel.Outcome);
        Assert.True(panel.IsWaiting);
        Assert.Equal(Loc.Instance[ChecksStrings.VerdictWaiting], panel.VerdictText);
        Assert.NotEqual(Loc.Instance[ChecksStrings.VerdictNoCi], panel.VerdictText);
        Assert.Equal(string.Empty, panel.SummaryText);
        Assert.True(panel.IsReading);
        Assert.Equal(2, platform.ChecksCalls);

        panel.Dispose();
        await panel.Watch.WaitAsync(Budget);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_rows_and_verdict_and_shows_the_platform_message()
    {
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Fail)),
            ChecksSample.FailedRead(GitRemoteFailureKind.Network, "gh: could not resolve api.github.com"))
        {
            HoldFrom = 2,
        };

        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);
        Assert.Equal(PrWaitOutcome.ChecksFailed, panel.Outcome);

        panel.RefreshCommand.Execute(null);
        await platform.ReadStartedAsync(2);

        Assert.Equal("build", Assert.Single(panel.Checks).Name);
        Assert.Equal(PrWaitOutcome.ChecksFailed, panel.Outcome);
        Assert.Equal(Loc.Instance[ChecksStrings.VerdictFailed], panel.VerdictText);
        Assert.True(panel.HasProblem);
        Assert.Contains("could not resolve api.github.com", panel.ProblemText, StringComparison.Ordinal);
        Assert.False(panel.IsEmpty);

        panel.Dispose();
        await panel.Watch.WaitAsync(Budget);
    }

    // ------------------------------------------------------------ polling

    [Fact]
    public async Task A_running_check_keeps_the_poll_going_until_the_next_reading_settles()
    {
        var clock = new ChecksPanelClock();
        var interval = TimeSpan.FromSeconds(5);
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pending)),
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform, clock, interval);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal(2, platform.ChecksCalls);
        Assert.Equal(2, platform.MergeCalls);
        Assert.True(panel.Watch.IsCompleted);
        Assert.False(panel.IsReading);
        Assert.Equal([interval], clock.Delays);
        Assert.Equal(PrWaitOutcome.Ready, panel.Outcome);
        Assert.Equal(Loc.Instance[ChecksStrings.VerdictReady], panel.VerdictText);
        Assert.True(panel.IsReady);
        Assert.Equal(PrCheckBucket.Pass, Assert.Single(panel.Checks).Check.Bucket);
    }

    [Fact]
    public async Task A_reading_that_already_settles_is_read_once()
    {
        var clock = new ChecksPanelClock();
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform, clock);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal(1, platform.ChecksCalls);
        Assert.Empty(clock.Delays);
        Assert.False(panel.IsReading);
        Assert.Equal(PrWaitOutcome.Ready, panel.Outcome);
    }

    [Fact]
    public async Task Dispose_cancels_the_poll_and_the_platform_is_not_read_again()
    {
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pending)),
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass)))
        {
            HoldFrom = 1,
        };

        using var panel = NewPanel(platform);
        panel.Start();
        await platform.ReadStartedAsync(1);
        Assert.True(panel.IsReading);

        panel.Dispose();
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal(2, platform.ChecksCalls);
        Assert.False(panel.IsReading);
        Assert.Null(panel.Outcome);
    }

    [Fact]
    public async Task Refresh_starts_a_new_read_after_a_settled_one()
    {
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass)),
            ChecksSample.Poll(
                PrMergeStateStatus.Clean,
                ChecksSample.Check("build", PrCheckBucket.Pass),
                ChecksSample.Check("lint", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);
        Assert.Equal(1, platform.ChecksCalls);
        Assert.True(panel.RefreshCommand.CanExecute(null));

        panel.RefreshCommand.Execute(null);
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal(2, platform.ChecksCalls);
        Assert.Equal(2, panel.Checks.Count);
    }

    // ------------------------------------------------------------ mark ready

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MarkReady_is_enabled_only_for_a_draft(bool draft, bool enabled)
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            ChecksSample.Merge(PrMergeStateStatus.Clean, draft: draft),
            ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform);
        panel.Start();

        Assert.Equal(draft, panel.IsDraft);
        Assert.Equal(enabled, panel.MarkReadyCommand.CanExecute(null));
    }

    [Fact]
    public async Task MarkReady_marks_the_draft_ready_and_re_reads()
    {
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(
                ChecksSample.Merge(PrMergeStateStatus.Draft, draft: true),
                ChecksSample.Check("build", PrCheckBucket.Pass)),
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);
        Assert.True(panel.IsDraft);

        panel.MarkReadyCommand.Execute(null);
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal([Number], platform.ReadyRequests);
        Assert.Equal(Loc.Instance[ChecksStrings.MarkReadyDone], panel.StatusText);
        Assert.True(panel.HasStatus);
        Assert.Equal(2, platform.ChecksCalls);
        Assert.Equal(PrWaitOutcome.Ready, panel.Outcome);
        Assert.False(panel.IsDraft);
    }

    [Fact]
    public async Task MarkReady_failure_shows_the_platform_message_and_does_not_re_read()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            ChecksSample.Merge(PrMergeStateStatus.Draft, draft: true),
            ChecksSample.Check("build", PrCheckBucket.Pass)));
        platform.ReadyResult = PrResult<PrReference>.Fail(GitRemoteFailureKind.Other, "pull request is locked");

        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);

        panel.MarkReadyCommand.Execute(null);

        Assert.Equal(Loc.Instance.Format(ChecksStrings.MarkReadyFailed, "pull request is locked"), panel.StatusText);
        Assert.Contains("pull request is locked", panel.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, platform.ReadyCalls);
        Assert.Equal(1, platform.ChecksCalls);
    }

    // ------------------------------------------------------------ handing the failures to the agent

    [Fact]
    public void Fix_is_disabled_without_a_failed_check()
    {
        var handed = 0;
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform, handToAgent: _ =>
        {
            handed++;
            return Task.FromResult(ChecksHandOff.Ran);
        });
        panel.Start();

        Assert.False(panel.CanFix);
        Assert.False(panel.FixCommand.CanExecute(null));
        panel.FixCommand.Execute(null);
        Assert.Equal(0, handed);
    }

    [Fact]
    public void Fix_is_disabled_without_a_session_to_hand_it_to()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22)));

        using var panel = NewPanel(platform);
        panel.Start();

        Assert.False(panel.CanFix);
        Assert.False(panel.FixCommand.CanExecute(null));
        Assert.Equal(0, platform.LogCalls);
    }

    [Fact]
    public void Fix_reads_one_log_per_failing_check_and_hands_the_digest_to_the_agent()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22),
            ChecksSample.Check("lint", PrCheckBucket.Fail, run: 11, job: 33),
            ChecksSample.Check("deploy", PrCheckBucket.Cancel, run: 11, job: 44)))
            .Log(11, 22, "npm ERR! the build broke")
            .Log(11, 33, "lint: unexpected token")
            .Log(11, 44, "deploy: cancelled after 3 s");
        string? handed = null;

        using var panel = NewPanel(platform, handToAgent: message =>
        {
            handed = message;
            return Task.FromResult(ChecksHandOff.Ran);
        });
        panel.Start();
        Assert.True(panel.CanFix);

        panel.FixCommand.Execute(null);

        Assert.Equal(new (long? Run, long Job)[] { (11, 22), (11, 33), (11, 44) }, platform.LogRequests);
        Assert.NotNull(handed);
        Assert.Contains("Pull request #12", handed, StringComparison.Ordinal);
        Assert.Contains("== Failed job logs ==", handed, StringComparison.Ordinal);
        Assert.Contains("npm ERR! the build broke", handed, StringComparison.Ordinal);
        Assert.Contains("lint: unexpected token", handed, StringComparison.Ordinal);
        Assert.Contains("deploy: cancelled after 3 s", handed, StringComparison.Ordinal);
        Assert.Equal(Loc.Instance[ChecksStrings.FixSent], panel.StatusText);
    }

    // The hand-off travels as an int: an internal enum cannot appear in a public test signature (CS0051), the same
    // casting ComposerSuggestionTests does for its token kind.
    [Theory]
    [InlineData((int)ChecksHandOff.Ran, ChecksStrings.FixSent)]
    [InlineData((int)ChecksHandOff.Queued, ChecksStrings.FixQueued)]
    [InlineData((int)ChecksHandOff.NoSession, ChecksStrings.FixNoSession)]
    public void Fix_reports_what_the_hand_off_did(int handOff, string statusKey)
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22)))
            .Log(11, 22, "exit code 1");

        using var panel = NewPanel(platform, handToAgent: _ => Task.FromResult((ChecksHandOff)handOff));
        panel.Start();

        panel.FixCommand.Execute(null);

        Assert.Equal(Loc.Instance[statusKey], panel.StatusText);
    }

    // ------------------------------------------------------------ one row's log

    [Fact]
    public void ToggleLog_reads_the_log_once_and_reopens_it_from_the_cache()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22)))
            .Log(11, 22, "line 1\nexit code 1");

        using var panel = NewPanel(platform);
        panel.Start();
        var row = Assert.Single(panel.Checks);
        Assert.False(row.IsLogOpen);

        row.ToggleLogCommand.Execute(null);

        Assert.True(row.IsLogOpen);
        Assert.True(row.HasLogTail);
        Assert.Equal("line 1\nexit code 1", row.LogTail);
        Assert.False(row.HasLogProblem);
        Assert.False(row.IsLogLoading);
        Assert.Equal(1, platform.LogCalls);

        row.ToggleLogCommand.Execute(null);

        Assert.False(row.IsLogOpen);
        Assert.True(row.HasLogTail);

        row.ToggleLogCommand.Execute(null);

        Assert.True(row.IsLogOpen);
        Assert.Equal("line 1\nexit code 1", row.LogTail);
        Assert.Equal(1, platform.LogCalls);
    }

    [Fact]
    public void A_failing_check_without_a_job_shows_that_it_has_no_log()
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("external-status", PrCheckBucket.Cancel)));

        using var panel = NewPanel(platform);
        panel.Start();
        var row = Assert.Single(panel.Checks);
        Assert.True(row.IsFailing);
        Assert.False(row.HasLog);

        row.ToggleLogCommand.Execute(null);

        Assert.True(row.IsLogOpen);
        Assert.True(row.HasLogProblem);
        Assert.Equal(Loc.Instance[ChecksStrings.LogNone], row.LogProblem);
        Assert.False(row.HasLogTail);
        Assert.Equal(0, platform.LogCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_log_is_marked_cut_when_the_platform_cut_it(bool truncated)
    {
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22)))
            .Log(11, 22, "the last two lines", truncated);

        using var panel = NewPanel(platform);
        panel.Start();

        Assert.Single(panel.Checks).ToggleLogCommand.Execute(null);

        Assert.Equal(truncated, Assert.Single(panel.Checks).IsLogCut);
    }

    [Fact]
    public async Task A_loaded_log_stays_open_and_is_not_read_again_by_the_next_poll()
    {
        var check = ChecksSample.Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, check),
            ChecksSample.Poll(PrMergeStateStatus.Clean, check, ChecksSample.Check("lint", PrCheckBucket.Pass)))
            .Log(11, 22, "exit code 1");

        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);
        var row = Assert.Single(panel.Checks);
        row.ToggleLogCommand.Execute(null);
        Assert.Equal("exit code 1", row.LogTail);

        panel.RefreshCommand.Execute(null);
        await panel.Watch.WaitAsync(Budget);

        Assert.Equal(2, platform.ChecksCalls);
        var after = panel.Checks[0];
        Assert.Same(row, after);
        Assert.Equal("build", after.Name);
        Assert.True(after.IsLogOpen);
        Assert.Equal("exit code 1", after.LogTail);
        Assert.Equal(1, platform.LogCalls);
    }

    // ------------------------------------------------------------ links, close and dispose

    [Fact]
    public void The_row_and_the_pull_request_open_their_links()
    {
        const string CheckLink = "https://example.test/acme/forge/actions/runs/11/job/22";
        var opened = new List<string>();
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Pass, link: CheckLink)));

        using var panel = NewPanel(platform, openLink: opened.Add);
        panel.Start();
        var row = Assert.Single(panel.Checks);
        Assert.True(row.HasLink);

        row.OpenLinkCommand.Execute(null);
        panel.OpenPullRequestCommand.Execute(null);

        Assert.Equal([CheckLink, "https://example.test/acme/forge/pull/12"], opened);
    }

    [Fact]
    public void OpenPullRequest_does_nothing_without_a_url()
    {
        var opened = new List<string>();
        var platform = new ChecksPanelPlatform(ChecksSample.Poll(
            PrMergeStateStatus.Clean,
            ChecksSample.Check("build", PrCheckBucket.Pass)));

        using var panel = NewPanel(platform, openLink: opened.Add, url: null);
        panel.Start();

        Assert.False(panel.HasPullRequestUrl);
        Assert.False(panel.OpenPullRequestCommand.CanExecute(null));

        panel.OpenPullRequestCommand.Execute(null);

        Assert.Empty(opened);
    }

    [Fact]
    public async Task Close_invokes_the_injected_close_and_dispose_is_idempotent()
    {
        var closed = 0;
        var platform = new ChecksPanelPlatform(
            ChecksSample.Poll(PrMergeStateStatus.Clean, ChecksSample.Check("build", PrCheckBucket.Pass, run: 11, job: 22)));

        var panel = NewPanel(platform, close: () => closed++);
        panel.Start();
        await panel.Watch.WaitAsync(Budget);

        panel.CloseCommand.Execute(null);
        Assert.Equal(1, closed);

        panel.Dispose();
        panel.Dispose();

        Assert.False(panel.RefreshCommand.CanExecute(null));
        Assert.False(panel.MarkReadyCommand.CanExecute(null));
        Assert.False(panel.FixCommand.CanExecute(null));
        panel.RefreshCommand.Execute(null);
        Assert.Equal(1, platform.ChecksCalls);
    }

    // ------------------------------------------------------------ harness

    /// <summary>The panel over a scripted platform, an instantly-firing clock and the inline <c>post</c> the spec names.</summary>
    private static ChecksPanelViewModel NewPanel(
        ChecksPanelPlatform platform,
        ChecksPanelClock? clock = null,
        TimeSpan? interval = null,
        string? url = "https://example.test/acme/forge/pull/12",
        Action<string>? openLink = null,
        Func<string, Task<ChecksHandOff>>? handToAgent = null,
        Action? close = null) =>
        new(
            WorkDirectory,
            Number,
            url,
            platform,
            () => new PrChecksPollerOptions { Interval = interval ?? TimeSpan.FromSeconds(1) },
            () => 40,
            action => action(),
            openLink,
            handToAgent,
            close,
            clock ?? new ChecksPanelClock());
}

/// <summary>One scripted poll of the checks panel: what the checks read and the merge-state read answer with.</summary>
internal sealed record ChecksPoll(PrResult<PrChecksSnapshot> Checks, PrResult<PrMergeState> Merge);

/// <summary>
/// A <see cref="TimeProvider"/> whose delays elapse immediately: creating a timer advances the virtual clock by its due
/// time and fires it, so the panel's poll runs as far as the scripted platform lets it inside <c>Start()</c>. Every
/// requested delay is recorded, so a test can assert which interval the poll asked for.
/// </summary>
internal sealed class ChecksPanelClock : TimeProvider
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

/// <summary>Builders for the checks panel's scripted readings.</summary>
internal static class ChecksSample
{
    /// <summary>One check of the "CI" workflow, as GitHub reports it; the run and job ids are what its log is read with.</summary>
    public static PrCheck Check(string name, PrCheckBucket bucket, long? run = null, long? job = null, string? link = null) =>
        new(name, "CI", string.Empty, bucket, string.Empty, link, null, null, run, job);

    public static PrMergeState Merge(PrMergeStateStatus status = PrMergeStateStatus.Clean, bool draft = false) =>
        new(
            PrState.Open,
            draft,
            status,
            PrReviewDecision.None,
            PrMergeable.Mergeable,
            status switch
            {
                PrMergeStateStatus.Clean => "CLEAN",
                PrMergeStateStatus.Draft => "DRAFT",
                _ => "UNKNOWN",
            },
            string.Empty,
            "MERGEABLE");

    public static ChecksPoll Poll(PrMergeStateStatus status, params PrCheck[] checks) => Poll(Merge(status), checks);

    public static ChecksPoll Poll(PrMergeState merge, params PrCheck[] checks) =>
        new(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(checks)), PrResult<PrMergeState>.Ok(merge));

    /// <summary>A successful empty read: the pull request has no checks (yet).</summary>
    public static ChecksPoll EmptyRead(PrMergeStateStatus status = PrMergeStateStatus.Clean) =>
        new(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot([])), PrResult<PrMergeState>.Ok(Merge(status)));

    /// <summary>Both reads of the poll fail.</summary>
    public static ChecksPoll FailedRead(
        GitRemoteFailureKind kind = GitRemoteFailureKind.Network,
        string message = "gh failed",
        PrFailureReason reason = PrFailureReason.None) =>
        new(
            PrResult<PrChecksSnapshot>.Fail(kind, message, "stderr", reason),
            PrResult<PrMergeState>.Fail(kind, message, "stderr", reason));
}

/// <summary>
/// A scripted <see cref="IPrPlatform"/> for the checks panel: the n-th checks read answers with the n-th scripted poll (the
/// last one repeats) and the merge-state read answers with the same poll's merge state; a failed job log is answered from
/// what the test pinned. A read at or past <see cref="HoldFrom"/> answers only by throwing the read's cancellation, so a
/// test can look at a poll in flight without sleeping.
/// </summary>
internal sealed class ChecksPanelPlatform(params ChecksPoll[] polls) : IPrPlatform
{
    private readonly Lock gate = new();
    private readonly List<TaskCompletionSource> reads = [];
    private readonly List<(long? Run, long Job)> logRequests = [];
    private readonly List<int> readyRequests = [];
    private readonly Dictionary<(long Run, long Job), PrResult<PrText>> logs = [];
    private int checksCalls;
    private int mergeCalls;

    public string Name => "Fake";

    /// <summary>The first read index that only ends by cancellation instead of answering at once.</summary>
    public int HoldFrom { get; init; } = int.MaxValue;

    /// <summary>What <c>gh pr ready</c> answers; a success for the number it was asked about unless a test pins a failure.</summary>
    public PrResult<PrReference>? ReadyResult { get; set; }

    public int ChecksCalls => Volatile.Read(ref checksCalls);

    public int MergeCalls => Volatile.Read(ref mergeCalls);

    public int LogCalls
    {
        get
        {
            lock (gate)
            {
                return logRequests.Count;
            }
        }
    }

    public IReadOnlyList<int> ReadyRequests
    {
        get
        {
            lock (gate)
            {
                return readyRequests.ToArray();
            }
        }
    }

    public int ReadyCalls => ReadyRequests.Count;

    /// <summary>Every failed-job-log read, in order, as the run and job ids the panel asked for.</summary>
    public IReadOnlyList<(long? Run, long Job)> LogRequests
    {
        get
        {
            lock (gate)
            {
                return logRequests.ToArray();
            }
        }
    }

    public int LastNumber { get; private set; }

    public string LastDirectory { get; private set; } = string.Empty;

    /// <summary>Pins the answer of one job log; the "number" the panel passed as the pull request is not part of it.</summary>
    public ChecksPanelPlatform Log(long run, long job, string text, bool truncated = false)
    {
        logs[(run, job)] = PrResult<PrText>.Ok(new PrText(text, truncated));
        return this;
    }

    /// <summary>Completes when the read with <paramref name="index"/> started; the bound only turns a hang into a failure.</summary>
    public Task ReadStartedAsync(int index)
    {
        Task started;
        lock (gate)
        {
            started = SignalFor(index).Task;
        }

        return started.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public async Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref checksCalls) - 1;
        LastNumber = number;
        LastDirectory = workingDirectory;
        Signal(index);
        if (index >= HoldFrom)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }

        return polls[Math.Min(index, polls.Length - 1)].Checks;
    }

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref mergeCalls) - 1;
        return Task.FromResult(polls[Math.Min(index, polls.Length - 1)].Merge);
    }

    public Task<PrResult<PrReference>> MarkPullRequestReadyAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        lock (gate)
        {
            readyRequests.Add(number);
        }

        return Task.FromResult(ReadyResult ?? PrResult<PrReference>.Ok(new PrReference(number, string.Empty)));
    }

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default)
    {
        PrResult<PrText> answer;
        lock (gate)
        {
            logRequests.Add((runId, jobId));
            answer = logs.TryGetValue((runId ?? 0, jobId), out var pinned)
                ? pinned
                : PrResult<PrText>.Fail(GitRemoteFailureKind.Other, $"no log pinned for job {jobId}");
        }

        return Task.FromResult(answer);
    }

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>Announces that the read with <paramref name="index"/> started.</summary>
    private void Signal(int index)
    {
        lock (gate)
        {
            SignalFor(index).TrySetResult();
        }
    }

    /// <summary>The completion source of one read index, created on first use. Callers hold <see cref="gate"/>.</summary>
    private TaskCompletionSource SignalFor(int index)
    {
        while (reads.Count <= index)
        {
            reads.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return reads[index];
    }
}
