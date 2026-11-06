using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The <c>pr_checks</c> and <c>pr_ci_log</c> tools: argument handling, honest rendering of snapshots and wait verdicts
/// (against a scripted platform and a virtual clock), and the job log end to end through <see cref="GitHubPlatform"/> and a
/// scripted stand-in for the gh executable.
/// </summary>
public sealed class PrCheckToolSmokeTests
{
    private const string WorkingDirectory = "/work";

    private static readonly PrCheck Failing = PrCheckSample.Check(
        "lint",
        PrCheckBucket.Fail,
        run: 11,
        job: 22,
        link: "https://github.com/acme/forge/actions/runs/11/job/22",
        state: "FAILURE",
        description: "boom");

    private static readonly PrCheck Running = PrCheckSample.Check(
        "build",
        PrCheckBucket.Pending,
        run: 11,
        job: 21,
        link: "https://github.com/acme/forge/actions/runs/11/job/21",
        state: "IN_PROGRESS");

    private static readonly PrCheck Passed = PrCheckSample.Check("test", PrCheckBucket.Pass, link: "https://github.com/acme/forge/actions/runs/11/job/23");

    private static readonly PrCheck Skipped = PrCheckSample.Check("docs", PrCheckBucket.Skipping);

    // ---- Definitions --------------------------------------------------------------------------------------------

    [Fact]
    public void Both_tools_are_read_only_have_the_documented_names_and_valid_schemas()
    {
        var tools = CreateTools(new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean)));

        Assert.Equal("pr_checks", PrCheckTools.ChecksName);
        Assert.Equal("pr_ci_log", PrCheckTools.CiLogName);
        Assert.Equal(PrCheckTools.ChecksName, tools.Checks.Definition.Name);
        Assert.Equal(PrCheckTools.CiLogName, tools.CiLog.Definition.Name);
        Assert.Equal(new[] { tools.Checks, tools.CiLog }, tools.All);
        Assert.All(tools.All, tool => Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect));
        Assert.All(tools.All, tool => Assert.Contains("pr_workflow", tool.Definition.Description, StringComparison.Ordinal));

        using var checks = JsonDocument.Parse(tools.Checks.Definition.InputSchemaJson);
        Assert.Equal(JsonValueKind.Object, checks.RootElement.GetProperty("properties").GetProperty("pr").ValueKind);
        Assert.Equal("boolean", checks.RootElement.GetProperty("properties").GetProperty("wait").GetProperty("type").GetString());
        Assert.False(checks.RootElement.TryGetProperty("required", out _));

        using var log = JsonDocument.Parse(tools.CiLog.Definition.InputSchemaJson);
        Assert.Equal("job_id", Assert.Single(log.RootElement.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("integer", log.RootElement.GetProperty("properties").GetProperty("run_id").GetProperty("type").GetString());
    }

    // ---- pr_checks: snapshot ------------------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_lists_every_check_with_bucket_and_link_failures_first()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Blocked, review: PrReviewDecision.ReviewRequired),
            Passed,
            Running,
            Failing,
            Skipped));
        var tools = CreateTools(rig);

        var result = await InvokeAsync(tools.Checks, """{"pr":7}""");

        Assert.False(result.IsError);
        var text = result.Content;
        Assert.StartsWith("Pull request #7", text, StringComparison.Ordinal);
        Assert.Contains("[FAIL] lint (CI) state=FAILURE - boom run_id=11 job_id=22 https://github.com/acme/forge/actions/runs/11/job/22", text, StringComparison.Ordinal);
        Assert.Contains("[pending] build (CI) state=IN_PROGRESS run_id=11 job_id=21 https://github.com/acme/forge/actions/runs/11/job/21", text, StringComparison.Ordinal);
        Assert.Contains("[pass] test (CI)", text, StringComparison.Ordinal);
        Assert.Contains("[skipped] docs (CI)", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("[FAIL]", StringComparison.Ordinal) < text.IndexOf("[pending]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[pending]", StringComparison.Ordinal) < text.IndexOf("[skipped]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[skipped]", StringComparison.Ordinal) < text.IndexOf("[pass]", StringComparison.Ordinal));
        Assert.Contains("Checks: 4 reported - 1 passed, 1 failed or cancelled, 1 not settled, 1 skipped/neutral.", text, StringComparison.Ordinal);
        Assert.Contains("Verdict: CHECKS FAILED. 1 check(s) failed or were cancelled; 1 more are still running.", text, StringComparison.Ordinal);
        Assert.Contains("pr_ci_log", text, StringComparison.Ordinal);
        Assert.Contains("Merge state: BLOCKED", text, StringComparison.Ordinal);
        Assert.Contains("Review decision: review required", text, StringComparison.Ordinal);
        Assert.Contains("Draft: no.", text, StringComparison.Ordinal);
        Assert.Equal(1, rig.Platform.ChecksCalls);
        Assert.Equal(1, rig.Platform.MergeCalls);
        Assert.Equal(0, rig.Platform.ViewCalls);
        Assert.Empty(rig.Time.Delays);
    }

    [Fact]
    public async Task An_empty_check_list_is_not_determined_yet_and_never_no_ci()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.False(result.IsError);
        Assert.Contains("NOT DETERMINED YET", result.Content, StringComparison.Ordinal);
        Assert.Contains("Do not conclude 'no CI' from one empty read", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("NO CI", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Snapshot_of_settled_passing_checks_with_a_clean_merge_state_is_ready_and_counts_skipped_as_passed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed, Skipped));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("Verdict: READY. All 2 check(s) passed (1 of them skipped/neutral) and the merge state is CLEAN.", result.Content, StringComparison.Ordinal);
        Assert.Contains("Forge never merges", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_with_running_checks_is_not_finished_and_names_them()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed, Running));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("Verdict: NOT FINISHED. 1 of 2 check(s) have not settled (build)", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_with_passing_checks_but_an_uncomputed_merge_state_is_not_determined_yet()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Unknown, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("NOT DETERMINED YET. All checks have finished and passed, but GitHub has not computed the merge state", result.Content, StringComparison.Ordinal);
        Assert.Contains("Merge state: UNKNOWN - GitHub has not computed it yet.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_reports_a_check_with_an_unclassifiable_bucket_as_not_counted_as_passed()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed, PrCheckSample.Check("odd", PrCheckBucket.Unknown)));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("[unknown, not counted as passed] odd", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_shows_a_cancelled_check_distinctly_and_a_draft_flag()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Blocked, draft: true),
            PrCheckSample.Check("e2e", PrCheckBucket.Cancel, run: 5, job: 6)));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("[CANCELLED] e2e (CI) run_id=5 job_id=6", result.Content, StringComparison.Ordinal);
        Assert.Contains("A cancelled check never counts as passed", result.Content, StringComparison.Ordinal);
        Assert.Contains("Draft: yes.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_shows_an_unrecognised_merge_state_exactly_as_observed()
    {
        var merge = PrCheckSample.Merge(PrMergeStateStatus.Unknown) with { MergeStateStatusText = "QUEUED_FOR_MERGE" };
        var rig = new PrCheckPollerRig(PrCheckSample.Step(merge, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.Contains("Merge state: QUEUED_FOR_MERGE - a value this version does not recognise", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_pr_the_pull_request_of_the_current_branch_is_resolved_first_and_its_address_shown()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed));
        var tools = CreateTools(rig);

        var result = await InvokeAsync(tools.Checks, "{}");

        Assert.False(result.IsError);
        Assert.StartsWith("Pull request #7 https://github.com/acme/forge/pull/7", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, rig.Platform.ViewCalls);
        Assert.Null(rig.Platform.LastSelector);
        Assert.Equal(WorkingDirectory, rig.Platform.LastDirectory);
    }

    [Fact]
    public async Task An_explicit_pr_number_is_used_as_given_without_looking_up_the_branch()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed));

        await InvokeAsync(CreateTools(rig).Checks, """{"pr":42}""");

        Assert.Equal(42, rig.Platform.LastNumber);
        Assert.Equal(0, rig.Platform.ViewCalls);
    }

    [Fact]
    public async Task A_branch_without_a_pull_request_is_an_error_that_points_at_pr_create()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));
        rig.Platform.ViewResult = PrResult<PullRequestInfo>.Fail(
            GitRemoteFailureKind.Other,
            "no pull requests found for branch \"feature\"",
            reason: PrFailureReason.PullRequestNotFound);

        var result = await InvokeAsync(CreateTools(rig).Checks, "{}");

        Assert.True(result.IsError);
        Assert.Contains("no pull requests found", result.Content, StringComparison.Ordinal);
        Assert.Contains("pr_create", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task A_platform_failure_is_a_named_tool_error_with_the_next_step()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.Network, "error connecting to api.github.com"));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Network: error connecting to api.github.com", result.Content, StringComparison.Ordinal);
        Assert.Contains("Next:", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_and_missing_cli_failures_tell_the_agent_to_involve_the_user()
    {
        var auth = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.AuthenticationFailed, "bad credentials"));
        var missing = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.Other, "gh not found", PrFailureReason.PlatformNotInstalled));

        var authResult = await InvokeAsync(CreateTools(auth).Checks, """{"pr":7}""");
        var missingResult = await InvokeAsync(CreateTools(missing).Checks, """{"pr":7}""");

        Assert.True(authResult.IsError);
        Assert.Contains("gh auth login", authResult.Content, StringComparison.Ordinal);
        Assert.True(missingResult.IsError);
        Assert.Contains("Install the GitHub CLI", missingResult.Content, StringComparison.Ordinal);
    }

    // ---- pr_checks: wait ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Wait_polls_until_the_checks_settle_and_reports_the_ready_verdict_with_the_time_spent()
    {
        var rig = new PrCheckPollerRig(
            PrCheckSample.Step(PrMergeStateStatus.Clean, Running),
            PrCheckSample.Step(PrMergeStateStatus.Clean, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.False(result.IsError);
        Assert.Contains("Waited 30s over 2 poll(s).", result.Content, StringComparison.Ordinal);
        Assert.Contains("Verdict: READY. All 1 check(s) passed and the merge state is CLEAN.", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Wait_reads_the_options_on_every_call_so_a_settings_change_applies_to_the_next_wait()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Running));
        var options = new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(10), MaxWait = TimeSpan.FromSeconds(30) };
        var tools = new PrCheckTools(rig.Platform, rig.Poller, () => options);

        var first = await InvokeAsync(tools.Checks, """{"pr":7,"wait":true}""");
        options = new PrChecksPollerOptions { Interval = TimeSpan.FromSeconds(20), MaxWait = TimeSpan.FromSeconds(60) };
        var second = await InvokeAsync(tools.Checks, """{"pr":7,"wait":true}""");

        Assert.Contains("TIMED OUT after 30s", first.Content, StringComparison.Ordinal);
        Assert.Contains("TIMED OUT after 1m0s", second.Content, StringComparison.Ordinal);
        Assert.Equal(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)],
            rig.Time.Delays);
    }

    [Fact]
    public async Task Wait_that_finds_a_failure_names_the_failed_job_for_pr_ci_log()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Unstable, Running, Failing));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.False(result.IsError);
        Assert.Contains("Waited 0s over 1 poll(s).", result.Content, StringComparison.Ordinal);
        Assert.Contains("Verdict: CHECKS FAILED.", result.Content, StringComparison.Ordinal);
        Assert.Contains("run_id=11 job_id=22", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_times_out_with_a_verdict_that_is_neither_a_pass_nor_a_failure()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Running));
        var tools = new PrCheckTools(rig.Platform, rig.Poller, () => new PrChecksPollerOptions { MaxWait = TimeSpan.FromMinutes(2) });

        var result = await InvokeAsync(tools.Checks, """{"pr":7,"wait":true}""");

        Assert.False(result.IsError);
        Assert.Contains("Verdict: TIMED OUT after 2m0s. NOT FINISHED. 1 of 1 check(s) have not settled (build)", result.Content, StringComparison.Ordinal);
        Assert.Contains("Ask the user whether to keep waiting", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("CHECKS FAILED", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_concludes_no_ci_only_after_the_confirmations_and_says_how_many_polls_it_took()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.False(result.IsError);
        Assert.Contains("Waited 1m0s over 3 poll(s).", result.Content, StringComparison.Ordinal);
        Assert.Contains("Verdict: NO CI. No checks were reported by 3 poll(s) in a row", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Wait_passes_the_hooks_caveat_on_exactly_once()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.HasHooks, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.Contains("READY WITH A CAVEAT", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.Content, "pre-receive hooks run when it is merged"));
    }

    [Theory]
    [InlineData(PrMergeStateStatus.Behind, "Verdict: BEHIND.", "pr_sync")]
    [InlineData(PrMergeStateStatus.Dirty, "Verdict: CONFLICTS (DIRTY).", "pr_sync")]
    [InlineData(PrMergeStateStatus.Blocked, "Verdict: BLOCKED.", "report it to the user")]
    [InlineData(PrMergeStateStatus.Unstable, "Verdict: UNSTABLE.", "nothing was changed")]
    [InlineData(PrMergeStateStatus.Draft, "Verdict: DRAFT.", "the user's decision")]
    public async Task Wait_hands_back_the_merge_state_verdicts_without_acting_on_them(
        PrMergeStateStatus status,
        string verdict,
        string advice)
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(status, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.False(result.IsError);
        Assert.Contains(verdict, result.Content, StringComparison.Ordinal);
        Assert.Contains(advice, result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wait_blocked_verdict_includes_the_review_decision()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(
            PrCheckSample.Merge(PrMergeStateStatus.Blocked, review: PrReviewDecision.ChangesRequested),
            Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.Contains("(review decision: changes requested)", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PrState.Merged, "has been merged")]
    [InlineData(PrState.Closed, "has been closed")]
    public async Task Wait_on_a_pull_request_that_is_no_longer_open_says_so(PrState state, string expected)
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrCheckSample.Merge(PrMergeStateStatus.Unknown, state: state), Running));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.Contains("Verdict: NOT OPEN.", result.Content, StringComparison.Ordinal);
        Assert.Contains(expected, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_that_gives_up_after_repeated_platform_failures_is_a_tool_error_naming_the_cause()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.FailedRead(GitRemoteFailureKind.Network, "error connecting to api.github.com"));

        var result = await InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""");

        Assert.True(result.IsError);
        Assert.StartsWith("Network: Gave up waiting for pull request #7 after 3 poll(s) over 1m0s", result.Content, StringComparison.Ordinal);
        Assert.Contains("error connecting to api.github.com", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_is_cancelled_with_the_turn_and_the_cancellation_is_not_turned_into_a_tool_result()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Running));
        rig.Platform.ThrowWhenCancelled = false;
        rig.Platform.OnChecks = index =>
        {
            if (index == 1)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeAsync(CreateTools(rig).Checks, """{"pr":7,"wait":true}""", cancellation.Token));

        Assert.Equal(2, rig.Platform.ChecksCalls);
    }

    [Fact]
    public async Task Invalid_poller_options_become_a_tool_error_not_an_exception()
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed));
        var tools = new PrCheckTools(rig.Platform, rig.Poller, () => new PrChecksPollerOptions { Interval = TimeSpan.Zero });

        var result = await InvokeAsync(tools.Checks, """{"pr":7,"wait":true}""");

        Assert.True(result.IsError);
        Assert.StartsWith("pr_checks failed:", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, rig.Platform.ChecksCalls);
    }

    // ---- Argument validation (both tools) -----------------------------------------------------------------------

    [Theory]
    [InlineData("{\"pr\":0}")]
    [InlineData("{\"pr\":-3}")]
    [InlineData("{\"pr\":\"7\"}")]
    [InlineData("{\"pr\":7.5}")]
    [InlineData("{\"pr\":99999999999}")]
    [InlineData("{\"wait\":\"yes\"}")]
    [InlineData("{\"nope\":1}")]
    [InlineData("[]")]
    [InlineData("{not json")]
    public async Task Checks_refuses_malformed_arguments_before_reading_anything(string json)
    {
        var rig = new PrCheckPollerRig(PrCheckSample.Step(PrMergeStateStatus.Clean, Passed));

        var result = await InvokeAsync(CreateTools(rig).Checks, json);

        Assert.True(result.IsError);
        Assert.Equal(0, rig.Platform.ChecksCalls);
        Assert.Equal(0, rig.Platform.ViewCalls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"job_id\":0}")]
    [InlineData("{\"job_id\":-1}")]
    [InlineData("{\"job_id\":\"22\"}")]
    [InlineData("{\"job_id\":22,\"run_id\":0}")]
    [InlineData("{\"job_id\":22,\"run_id\":\"11\"}")]
    [InlineData("{\"job_id\":22,\"extra\":true}")]
    [InlineData("{broken")]
    public async Task Ci_log_refuses_malformed_arguments_without_calling_gh(string json)
    {
        using var fake = new SmokeFakeGh(stdout: "log");
        var tools = CreateGhTools(fake);

        var result = await InvokeAsync(tools.CiLog, json, workingDirectory: fake.WorkingDirectory);

        Assert.True(result.IsError);
        Assert.Empty(fake.Calls);
    }

    // ---- pr_ci_log ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ci_log_returns_the_failed_step_log_whole_with_a_header()
    {
        const string log = "build\tRun tests\t2026-05-01T10:00:00Z Failed AssertionError: expected 3 but was 4\nbuild\tRun tests\t##[error]Process completed with exit code 1.\n";
        using var fake = new SmokeFakeGh(stdout: log);

        var result = await InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":22,"run_id":11}""", workingDirectory: fake.WorkingDirectory);

        Assert.False(result.IsError);
        Assert.Equal("Failed-step log of job 22 (run 11):\n" + log.ReplaceLineEndings("\r\n").ReplaceLineEndings("\n"), result.Content.ReplaceLineEndings("\n"));
        var call = Assert.Single(fake.Calls);
        Assert.Equal("run view 11 --job 22 --log-failed", call);
    }

    [Fact]
    public async Task Ci_log_without_a_run_id_asks_gh_for_the_job_alone()
    {
        using var fake = new SmokeFakeGh(stdout: "boom\n");

        var result = await InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":22}""", workingDirectory: fake.WorkingDirectory);

        Assert.False(result.IsError);
        Assert.StartsWith("Failed-step log of job 22:\n", result.Content, StringComparison.Ordinal);
        Assert.Equal("run view --job 22 --log-failed", Assert.Single(fake.Calls));
    }

    [Fact]
    public async Task Ci_log_accepts_job_ids_beyond_the_32_bit_range()
    {
        using var fake = new SmokeFakeGh(stdout: "boom\n");

        var result = await InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":71234567890,"run_id":25012345678}""", workingDirectory: fake.WorkingDirectory);

        Assert.False(result.IsError);
        Assert.Equal("run view 25012345678 --job 71234567890 --log-failed", Assert.Single(fake.Calls));
    }

    [Fact]
    public async Task Ci_log_of_a_very_long_log_keeps_the_end_and_says_it_was_cut()
    {
        using var fake = new SmokeFakeGh(stdout: new string('a', 20_000) + "THE-END");
        var tools = new PrCheckTools(
            new GitHubPlatform(fake.Cli) { MaxLogCharacters = 200 },
            new PrChecksPoller(new PrCheckFakePlatform([PrCheckSample.Step(PrMergeStateStatus.Clean)]), TimeProvider.System),
            () => new PrChecksPollerOptions());

        var result = await InvokeAsync(tools.CiLog, """{"job_id":22}""", workingDirectory: fake.WorkingDirectory);

        Assert.False(result.IsError);
        Assert.Contains("only its end is shown", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("THE-END", result.Content.TrimEnd(), StringComparison.Ordinal);
        Assert.True(result.Content.Length < 1000);
    }

    [Fact]
    public async Task Ci_log_with_no_failed_step_output_says_so_instead_of_returning_an_empty_result()
    {
        using var fake = new SmokeFakeGh(stdout: "\n");

        var result = await InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":22}""", workingDirectory: fake.WorkingDirectory);

        Assert.False(result.IsError);
        Assert.Contains("(no failed-step output)", result.Content, StringComparison.Ordinal);
        Assert.Contains("may still be running", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ci_log_reports_a_gh_failure_as_a_named_tool_error()
    {
        using var fake = new SmokeFakeGh(exitCode: 1, stderr: "error connecting to api.github.com\n");

        var result = await InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":22}""", workingDirectory: fake.WorkingDirectory);

        Assert.True(result.IsError);
        Assert.StartsWith("Network:", result.Content, StringComparison.Ordinal);
        Assert.Contains("error connecting to api.github.com", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ci_log_without_the_gh_executable_tells_the_agent_to_install_and_log_in()
    {
        using var temp = new TempDirectory();
        var tools = new PrCheckTools(
            new GitHubPlatform(new GhCli(temp.Combine("no-such-gh-executable"))),
            new PrChecksPoller(new PrCheckFakePlatform([PrCheckSample.Step(PrMergeStateStatus.Clean)]), TimeProvider.System),
            () => new PrChecksPollerOptions());

        var result = await InvokeAsync(tools.CiLog, """{"job_id":22}""", workingDirectory: temp.Path);

        Assert.True(result.IsError);
        Assert.Contains("Install the GitHub CLI", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ci_log_cancellation_propagates_instead_of_becoming_a_tool_error()
    {
        using var fake = new SmokeFakeGh(sleep: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeAsync(CreateGhTools(fake).CiLog, """{"job_id":22}""", cancellation.Token, fake.WorkingDirectory));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static PrCheckTools CreateTools(PrCheckPollerRig rig) =>
        new(rig.Platform, rig.Poller, static () => new PrChecksPollerOptions());

    private static PrCheckTools CreateGhTools(SmokeFakeGh fake) =>
        new(
            new GitHubPlatform(fake.Cli),
            new PrChecksPoller(new PrCheckFakePlatform([PrCheckSample.Step(PrMergeStateStatus.Clean)]), TimeProvider.System),
            static () => new PrChecksPollerOptions());

    private static Task<ToolResult> InvokeAsync(
        ITool tool,
        string json,
        CancellationToken ct = default,
        string workingDirectory = WorkingDirectory) =>
        tool.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("n"), tool.Definition.Name, json),
            new ToolContext("session", workingDirectory),
            ct).AsTask();

    private static Task<ToolResult> InvokeAsync(ITool tool, string json, string workingDirectory) =>
        InvokeAsync(tool, json, CancellationToken.None, workingDirectory);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
