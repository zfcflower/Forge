using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class CiChainTests
{
    private const string Worktree = "/wt/s1";

    // ------------------------------------------------------------------ watcher

    [Fact]
    public async Task New_failure_starts_one_run_with_target_key_and_budget()
    {
        var h = new Harness();
        await h.Watcher.PollOnceAsync(CancellationToken.None);

        var request = Assert.Single(h.Engine.Started);
        Assert.Equal(BuiltInAutomations.CiChain, request.AutomationId);
        Assert.Equal(AutomationTriggerSource.Event, request.Source);
        Assert.Equal("s1", request.TargetSessionId);
        Assert.Equal("ci:s1#7", request.TargetKey);
        Assert.Equal(3, request.MaxTargetAttempts);
        Assert.StartsWith("ci:s1:abc123:", request.Occurrence, StringComparison.Ordinal);
        var detail = CiDetail.Parse(request.Detail);
        Assert.NotNull(detail);
        Assert.Equal(7, detail.PrNumber);
        Assert.Equal("feature", detail.HeadBranch);
        Assert.Equal("main", detail.BaseBranch);
        Assert.Equal("abc123", detail.HeadOid);
        Assert.Equal("build, test", detail.Signature);
    }

    [Fact]
    public async Task Same_watermark_does_not_start_again_but_a_new_head_does()
    {
        var h = new Harness();
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(h.Engine.Started);

        h.Pr.Pull = h.Pr.Pull! with { HeadRefOid = "def456" };
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Engine.Started.Count);
    }

    [Fact]
    public async Task Busy_start_keeps_the_watermark_so_the_next_poll_retries()
    {
        var h = new Harness();
        h.Engine.NextStatus = AutomationStartStatus.Busy;
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        h.Engine.NextStatus = AutomationStartStatus.Started;
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Engine.Started.Count);
    }

    [Fact]
    public async Task Green_resets_budget_and_watermark()
    {
        var h = new Harness();
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.NotNull(await h.State.GetStateAsync("ci.seen:s1", CancellationToken.None));

        h.Pr.Checks = [Check("build", PrCheckBucket.Pass), Check("test", PrCheckBucket.Skipping)];
        await h.Watcher.PollOnceAsync(CancellationToken.None);

        Assert.Null(await h.State.GetStateAsync("ci.seen:s1", CancellationToken.None));
        Assert.Equal(["ci:s1#7"], h.Engine.Resets);

        // The same failure after a green is new again.
        h.Pr.Checks = FailingChecks();
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Engine.Started.Count);
    }

    [Fact]
    public async Task Pending_checks_do_nothing()
    {
        var h = new Harness();
        h.Pr.Checks = [Check("build", PrCheckBucket.Pass), Check("test", PrCheckBucket.Pending)];
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        h.Pr.Checks = [Check("build", PrCheckBucket.Fail), Check("test", PrCheckBucket.Pending)];
        await h.Watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(h.Engine.Started);
        Assert.Empty(h.Engine.Resets);
    }

    [Fact]
    public async Task Disabled_does_nothing()
    {
        var h = new Harness();
        h.Enabled = false;
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(h.Engine.Started);
        Assert.Equal(0, h.Pr.ViewCalls);
    }

    [Fact]
    public async Task Closed_pull_request_and_missing_pull_request_are_skipped()
    {
        var h = new Harness();
        h.Pr.Pull = h.Pr.Pull! with { State = PrState.Closed };
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        h.Pr.Pull = null;
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public async Task Sub_agent_and_worktreeless_sessions_are_ignored()
    {
        var h = new Harness();
        h.Catalog.Sessions =
        [
            Session("child") with { ParentSessionId = "s1" },
            Session("bare") with { WorktreePath = null },
            Session("archived") with { Status = SessionStatus.Archived },
        ];
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(h.Engine.Started);
        Assert.Equal(0, h.Pr.ViewCalls);
    }

    [Fact]
    public async Task Budget_exhausted_notifies_once_per_target_and_resets_on_green()
    {
        var h = new Harness();
        h.Engine.NextStatus = AutomationStartStatus.BudgetExhausted;
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        h.Pr.Pull = h.Pr.Pull! with { HeadRefOid = "def456" };
        await h.Watcher.PollOnceAsync(CancellationToken.None);

        var call = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Warning, call.Level);
        Assert.Equal(AutomationStrings.CiStoppedTitle.Resolve(), call.Title);
        Assert.Equal(AutomationStrings.CiStoppedBudget("3"), call.Body);

        h.Pr.Checks = [Check("build", PrCheckBucket.Pass)];
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Null(await h.State.GetStateAsync("ci.budgetNotified:ci:s1#7", CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_session_poll_does_not_stop_the_others()
    {
        var h = new Harness();
        h.Catalog.Sessions = [Session("s0") with { WorktreePath = "/wt/boom" }, Session("s1")];
        h.Pr.ThrowFor = "/wt/boom";
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(h.Engine.Started);
    }

    [Fact]
    public async Task Run_loop_survives_a_failing_poll_and_stops_on_cancel()
    {
        var h = new Harness();
        h.Catalog.ThrowOnce = true;
        using var cts = new CancellationTokenSource();
        var task = h.Watcher.RunAsync(cts.Token);
        await h.Time.WaitForTimerAsync();
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Time.WaitForPollAsync(h.Catalog, 2);
        Assert.True(h.Catalog.Calls >= 2);

        await cts.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ runner

    [Fact]
    public async Task Runner_skips_when_the_target_session_is_gone()
    {
        var r = new RunnerHarness();
        r.Sessions.Session = null;
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Skipped, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
    }

    [Fact]
    public async Task Runner_skips_when_the_checks_are_gone()
    {
        var r = new RunnerHarness();
        r.Pr.Checks = [Check("build", PrCheckBucket.Pass)];
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Skipped, outcome.Status);
        Assert.Equal(AutomationStrings.CiChecksGone, outcome.Message);
        Assert.Empty(r.Launcher.Requests);
    }

    [Fact]
    public async Task Runner_fails_when_scout_errors()
    {
        var r = new RunnerHarness();
        r.Launcher.Result = new ChildAgentResult("c1", "scout broke", true, new TokenUsage(0, 0));
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal("scout broke", outcome.Message);
        Assert.Empty(r.Mailbox.Messages);
        Assert.Equal("Scout", Assert.Single(r.Launcher.Requests).Kind);
    }

    [Fact]
    public async Task Runner_maps_mailbox_outcomes()
    {
        var withdrawn = new RunnerHarness();
        withdrawn.Mailbox.Result = new MailboxResult(MailboxStatus.Withdrawn, "user took over");
        var o1 = await withdrawn.RunAsync();
        Assert.Equal(AutomationRunStatus.Cancelled, o1.Status);
        Assert.Equal("user took over", o1.Message);

        var cancelled = new RunnerHarness();
        cancelled.Mailbox.Result = new MailboxResult(MailboxStatus.Cancelled);
        Assert.Equal(AutomationRunStatus.Cancelled, (await cancelled.RunAsync()).Status);

        var failed = new RunnerHarness();
        failed.Mailbox.Result = new MailboxResult(MailboxStatus.TurnFailed, "model error");
        var o3 = await failed.RunAsync();
        Assert.Equal(AutomationRunStatus.Failed, o3.Status);
        Assert.Equal("model error", o3.Message);
        Assert.Empty(failed.Git.Pushes);
    }

    [Fact]
    public async Task Runner_does_not_push_when_head_is_unchanged()
    {
        var r = new RunnerHarness();
        r.Git.Heads = ["h1", "h1"];
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Empty(r.Git.Pushes);
        Assert.Contains(r.Notes.Calls, c => c.Level == NotificationLevel.Info && c.Body == AutomationStrings.CiNoFixBody("Session one").Resolve());
    }

    [Fact]
    public async Task Runner_does_not_push_when_auto_push_is_off_for_the_project()
    {
        var r = new RunnerHarness { Config = new AutomationConfig { CiAutoPushOffProjects = ["proj"] } };
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Empty(r.Git.Pushes);
        Assert.Contains(r.Notes.Calls, c => c.Body == AutomationStrings.CiNotPushedBody("Session one").Resolve());
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("", "main")]
    public async Task Runner_never_pushes_the_base_branch_or_an_empty_branch(string head, string baseBranch)
    {
        var r = new RunnerHarness { Detail = new CiDetail(7, head, baseBranch, "abc123", "build, test") };
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.CiStoppedBaseBranch(baseBranch), outcome.Message);
        Assert.Empty(r.Git.Pushes);
        Assert.Contains(r.Notes.Calls, c => c.Title == AutomationStrings.CiStoppedTitle.Resolve());
    }

    [Fact]
    public async Task Runner_fails_when_the_push_fails()
    {
        var r = new RunnerHarness();
        r.Git.PushResult = new CiPushResult(false, "rejected");
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.CiStoppedPushFailed("rejected"), outcome.Message);
        Assert.Null(await r.State.GetStateAsync("ci.lastFix:ci:s1#7", CancellationToken.None));
        Assert.Contains(r.Notes.Calls, c => c.Title == AutomationStrings.CiStoppedTitle.Resolve());
    }

    [Fact]
    public async Task Runner_pushes_the_head_branch_and_records_the_fixed_signature()
    {
        var r = new RunnerHarness();
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal([(Worktree, "feature")], r.Git.Pushes);
        Assert.Equal("h2|build, test", await r.State.GetStateAsync("ci.lastFix:ci:s1#7", CancellationToken.None));
        Assert.Contains(r.Notes.Calls, c => c.Level == NotificationLevel.Success && c.Body == AutomationStrings.CiPushedBody("feature").Resolve());

        var message = Assert.Single(r.Mailbox.Messages);
        Assert.Equal("s1", message.SessionId);
        Assert.Equal("run1", message.RunId);
        Assert.Contains("scout analysis", message.Text, StringComparison.Ordinal);
        Assert.Contains(r.Notes.Calls, c => c.Level == NotificationLevel.Warning && c.Title == AutomationStrings.CiFailedTitle.Resolve());
    }

    [Fact]
    public async Task Runner_reports_waiting_then_running_through_the_context()
    {
        var r = new RunnerHarness();
        r.Mailbox.ReportWaiting = true;
        await r.RunAsync();
        await Task.Yield();
        Assert.Contains(AutomationRunStatus.WaitingIdle, r.Context.Statuses);
        Assert.Contains(AutomationRunStatus.Running, r.Context.Statuses);
    }

    [Fact]
    public async Task Runner_stops_before_scout_when_the_failing_head_is_the_commit_it_pushed()
    {
        var r = new RunnerHarness();
        await r.State.SetStateAsync("ci.lastFix:ci:s1#7", "abc123|build, test", CancellationToken.None);
        var outcome = await r.RunAsync();
        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.CiStoppedSameFailure, outcome.Message);
        Assert.Empty(r.Launcher.Requests);
        Assert.Empty(r.Mailbox.Messages);
        Assert.Empty(r.Git.Pushes);
        Assert.Contains(r.Notes.Calls, c => c.Title == AutomationStrings.CiStoppedTitle.Resolve());

        // Other checks, or another commit, are not "the same failure after our fix".
        var otherChecks = new RunnerHarness();
        await otherChecks.State.SetStateAsync("ci.lastFix:ci:s1#7", "abc123|lint", CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Completed, (await otherChecks.RunAsync()).Status);
        Assert.Single(otherChecks.Git.Pushes);

        var otherCommit = new RunnerHarness();
        await otherCommit.State.SetStateAsync("ci.lastFix:ci:s1#7", "zzz999|build, test", CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Completed, (await otherCommit.RunAsync()).Status);
        Assert.Single(otherCommit.Git.Pushes);
    }

    [Fact]
    public async Task Runner_records_the_pushed_commit_with_the_checks_and_passes_the_guards()
    {
        var r = new RunnerHarness();
        await r.RunAsync();
        Assert.Equal("h2|build, test", await r.State.GetStateAsync("ci.lastFix:ci:s1#7", CancellationToken.None));
        Assert.Equal([("main", "abc123")], r.Git.PushGuards);
    }

    [Theory]
    [InlineData(CiPushRefusal.DetachedHead)]
    [InlineData(CiPushRefusal.BranchMismatch)]
    [InlineData(CiPushRefusal.NotDescended)]
    [InlineData(CiPushRefusal.ProtectedBranch)]
    [InlineData(CiPushRefusal.HeadUnreadable)]
    public async Task Runner_ends_with_a_localized_reason_and_notification_when_the_push_is_refused(CiPushRefusal refusal)
    {
        var r = new RunnerHarness();
        r.Git.PushResult = new CiPushResult(false, null, refusal);
        var outcome = await r.RunAsync();
        var expected = AutomationStrings.CiStoppedUnsafePush(AutomationStrings.CiRefusal(refusal, "feature"));
        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(expected, outcome.Message);
        Assert.Null(await r.State.GetStateAsync("ci.lastFix:ci:s1#7", CancellationToken.None));
        Assert.Contains(r.Notes.Calls, c => c.Title == AutomationStrings.CiStoppedTitle.Resolve() && c.Body == expected);
    }

    [Fact]
    public async Task Fail_fix_green_then_the_same_check_failing_again_runs_again()
    {
        var h = new Harness();
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        await h.State.SetStateAsync("ci.lastFix:ci:s1#7", "abc123|build, test", CancellationToken.None);

        h.Pr.Checks = [Check("build", PrCheckBucket.Pass), Check("test", PrCheckBucket.Pass)];
        await h.Watcher.PollOnceAsync(CancellationToken.None);
        Assert.Null(await h.State.GetStateAsync("ci.lastFix:ci:s1#7", CancellationToken.None));

        // Same state store, same check names, the pushed commit is no longer the failing head.
        var r = new RunnerHarness { State = h.State };
        Assert.Equal(AutomationRunStatus.Completed, (await r.RunAsync()).Status);
        Assert.Single(r.Launcher.Requests);
    }

    [Fact]
    public async Task Runner_truncates_logs_per_job_and_in_total()
    {
        var r = new RunnerHarness();
        var longLines = string.Join('\n', Enumerable.Range(0, 1000).Select(i => "line " + i));
        r.Pr.Logs = new Dictionary<long, string> { [11] = longLines, [12] = new string('x', 100_000) };
        r.Pr.Checks =
        [
            Check("build", PrCheckBucket.Fail, jobId: 11),
            Check("test", PrCheckBucket.Fail, jobId: 12),
            Check("lint", PrCheckBucket.Cancel, jobId: 13),
        ];
        await r.RunAsync();

        var task = Assert.Single(r.Launcher.Requests).Task;
        Assert.Contains("line 999", task, StringComparison.Ordinal);
        Assert.DoesNotContain("line 799\n", task, StringComparison.Ordinal);
        Assert.Contains("line 800", task, StringComparison.Ordinal);
        Assert.True(task.Length < CiChainRunner.MaxTotalChars + 2_000, "total log budget");
        Assert.Contains(AutomationStrings.CiLogUnavailable("no log"), task, StringComparison.Ordinal);
    }

    [Fact]
    public void Tail_keeps_the_last_lines_and_characters()
    {
        Assert.Equal("b\nc", CiChainRunner.Tail("a\nb\nc", 2, 100));
        Assert.Equal("b\nc\n", CiChainRunner.Tail("a\nb\nc\n", 2, 100));
        Assert.Equal("a\nb", CiChainRunner.Tail("a\nb", 5, 100));
        Assert.Equal("cde", CiChainRunner.Tail("abcde", 5, 3));
    }

    [Fact]
    public async Task Runner_propagates_cancellation()
    {
        var r = new RunnerHarness();
        r.Mailbox.Throw = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.RunAsync());

        var r2 = new RunnerHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r2.RunAsync(cts.Token));
        Assert.Empty(r2.Git.Pushes);
    }

    [Fact]
    public void Detail_round_trips()
    {
        var detail = new CiDetail(12, "feat/x", "main", "deadbeef", "a, b");
        Assert.Equal(detail, CiDetail.Parse(detail.Serialize()));
        Assert.Null(CiDetail.Parse("garbage"));
        Assert.Null(CiDetail.Parse(null));
    }

    // ------------------------------------------------------------------ helpers

    private static PrCheck Check(string name, PrCheckBucket bucket, long? jobId = null) =>
        new(name, "ci", bucket.ToString(), bucket, string.Empty, null, null, null, jobId is null ? null : 100, jobId);

    private static List<PrCheck> FailingChecks() =>
        [Check("test", PrCheckBucket.Fail, 2), Check("build", PrCheckBucket.Cancel, 1), Check("lint", PrCheckBucket.Pass)];

    private static SessionInfo Session(string id) =>
        new(id, "/ws", "Session one", DateTimeOffset.UnixEpoch)
        {
            ProjectId = "proj",
            WorktreePath = Worktree,
            Status = SessionStatus.Active,
        };

    private static PullRequestInfo Pull() =>
        new(7, "https://x/pr/7", PrState.Open, "t", "b", "feature", "main", "abc123", false, PrMergeStateStatus.Clean, PrReviewDecision.None);

    private sealed class Harness
    {
        public Harness()
        {
            Pr.Pull = Pull();
            Pr.Checks = FailingChecks();
            Catalog.Sessions = [Session("s1")];
            Watcher = new CiFailureWatcher(Engine, Catalog, Pr, State, Time, () => Enabled, Notes);
        }

        public bool Enabled { get; set; } = true;

        public CiFakeEngine Engine { get; } = new();

        public CiFakeCatalog Catalog { get; } = new();

        public CiFakePr Pr { get; } = new();

        public CiFakeState State { get; } = new();

        public CiFakeNotes Notes { get; } = new();

        public CiFakeTime Time { get; } = new();

        public CiFailureWatcher Watcher { get; }
    }

    private sealed class RunnerHarness
    {
        public RunnerHarness()
        {
            Pr.Pull = Pull();
            Pr.Checks = FailingChecks();
            Sessions.Session = Session("s1");
            Launcher.Result = new ChildAgentResult("c1", "scout analysis", false, new TokenUsage(0, 0));
            Git.Heads = ["h1", "h2"];
        }

        public CiDetail Detail { get; init; } = new(7, "feature", "main", "abc123", "build, test");

        public AutomationConfig Config { get; init; } = new();

        public CiFakePr Pr { get; } = new();

        public CiFakeGit Git { get; } = new();

        public CiFakeSessions Sessions { get; } = new();

        public CiFakeMailbox Mailbox { get; } = new();

        public CiFakeLauncher Launcher { get; } = new();

        public CiFakeNotes Notes { get; } = new();

        public CiFakeState State { get; init; } = new();

        public CiFakeContext Context => context ??= new CiFakeContext(
            new AutomationRun("run1", BuiltInAutomations.CiChain, "ci", AutomationTriggerSource.Event, "occ", AutomationRunStatus.Running, DateTimeOffset.UnixEpoch)
            {
                Detail = Detail.Serialize(),
                TargetSessionId = "s1",
                TargetKey = "ci:s1#7",
            });

        private CiFakeContext? context;

        public Task<AutomationRunOutcome> RunAsync(CancellationToken ct = default)
        {
            Mailbox.Context = Context;
            var runner = new CiChainRunner(Pr, Git, Sessions, Mailbox, Launcher, Notes, () => Config, State);
            return runner.RunAsync(Context, ct);
        }
    }
}
