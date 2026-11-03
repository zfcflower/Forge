using Forge.Plugin.Sdk;
using Forge.Workflows.Tests.Support;
using Xunit;

namespace Forge.Workflows.Tests;

public sealed class WorkflowFailureTests
{
    [Fact]
    public async Task Failed_step_skips_its_transitive_dependents_and_independent_branches_still_run()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"fail","after":[]},
              {"id":"b","tool":"next"},
              {"id":"c","tool":"next"},
              {"id":"d","tool":"independent","after":[]}
            ]}
            """;
        var host = new RecordingHost()
            .Respond("fail", "boom", isError: true)
            .Respond("next", "never")
            .Respond("independent", "fine");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        var failed = WorkflowKit.Step(result, "a");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.True(failed.IsError);
        Assert.Equal("boom", failed.Content);
        Assert.Equal("step 'a': tool 'fail' reported an error: boom", failed.Error);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "b").Status);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "c").Status);
        Assert.Equal(StepStatus.Succeeded, WorkflowKit.Step(result, "d").Status);
        Assert.Equal(new[] { "b", "c" }, result.NeverRanStepIds);
        Assert.Equal(0, host.CallCount("next"));
        Assert.Equal(1, host.CallCount("independent"));
        Assert.Equal(failed.Error, result.Error);
    }

    [Fact]
    public async Task Tool_exception_is_recorded_as_a_failed_step_with_type_and_message()
    {
        const string json = """{"steps":[{"id":"a","tool":"boom"},{"id":"b","tool":"next"}]}""";
        var host = new RecordingHost()
            .On("boom", (_, _) => throw new InvalidOperationException("kaboom"))
            .Respond("next", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        var failed = WorkflowKit.Step(result, "a");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Equal("step 'a': InvalidOperationException: kaboom", failed.Error);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "b").Status);
        Assert.Equal(0, host.CallCount("next"));
    }

    [Fact]
    public async Task Failure_in_a_diamond_skips_the_join_but_not_the_unrelated_branch()
    {
        const string json = """
            {"steps":[
              {"id":"ok","tool":"ok","after":[]},
              {"id":"bad","tool":"bad","after":[]},
              {"id":"mid","tool":"ok","after":["ok"]},
              {"id":"join","tool":"next","after":["bad","mid"]}
            ]}
            """;
        var host = new RecordingHost()
            .Respond("ok", "fine")
            .Respond("bad", "nope", isError: true)
            .Respond("next", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Succeeded, WorkflowKit.Step(result, "mid").Status);
        Assert.Equal(StepStatus.Failed, WorkflowKit.Step(result, "bad").Status);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "join").Status);
        Assert.Equal(0, host.CallCount("next"));
    }

    [Fact]
    public async Task Failure_does_not_cancel_sibling_steps_already_in_flight()
    {
        const string json = """
            {"steps":[
              {"id":"fails","tool":"fail","after":[]},
              {"id":"sibling","tool":"slow","after":[]}
            ]}
            """;
        var host = new RecordingHost()
            .Respond("fail", "boom", isError: true)
            .Respond("slow", "finished", delay: TimeSpan.FromMilliseconds(50));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, WorkflowKit.Step(result, "fails").Status);
        var sibling = WorkflowKit.Step(result, "sibling");
        Assert.Equal(StepStatus.Succeeded, sibling.Status);
        Assert.Equal("finished", sibling.Content);
    }

    [Fact]
    public async Task Caller_cancellation_stops_scheduling_and_leaves_unstarted_steps_not_run()
    {
        const string json = """
            {"steps":[
              {"id":"block","tool":"block","after":[]},
              {"id":"p","tool":"p","after":[]},
              {"id":"q","tool":"q","after":[]}
            ]}
            """;
        using var cts = new CancellationTokenSource();
        var started = NewSignal();
        var host = new RecordingHost().On("block", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HostToolResult("never");
        });

        var run = WorkflowKit.RunAsync(json, host, WorkflowKit.Options(maxConcurrency: 1), cts.Token);
        await WorkflowKit.Within(started.Task);
        cts.Cancel();
        var result = await WorkflowKit.Within(run);

        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.Equal(StepStatus.Cancelled, WorkflowKit.Step(result, "block").Status);
        Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "p").Status);
        Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "q").Status);
        Assert.Equal(new[] { "block" }, host.CalledTools);
        Assert.Equal(new[] { "block", "p", "q" }, result.NeverRanStepIds);
        Assert.Contains("cancelled", result.Error);
    }

    [Fact]
    public async Task Token_cancelled_before_the_run_starts_calls_no_tool()
    {
        const string json = """{"steps":[{"id":"a","tool":"t","after":[]},{"id":"b","tool":"t","after":[]}]}""";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var host = new RecordingHost().Respond("t", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, ct: cts.Token));

        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.Empty(host.Calls);
        Assert.All(result.Steps, static step => Assert.Equal(StepStatus.NotRun, step.Status));
        Assert.Equal(new[] { "a", "b" }, result.NeverRanStepIds);
    }

    [Fact]
    public async Task Cancelled_in_flight_step_finishes_its_cleanup_before_the_run_returns()
    {
        const string json = """{"steps":[{"id":"work","tool":"work"}]}""";
        using var cts = new CancellationTokenSource();
        var started = NewSignal();
        var cleanedUp = false;
        var host = new RecordingHost().On("work", async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
                cleanedUp = true;
                throw;
            }

            return new HostToolResult("never");
        });

        var run = WorkflowKit.RunAsync(json, host, ct: cts.Token);
        await WorkflowKit.Within(started.Task);
        cts.Cancel();
        var result = await WorkflowKit.Within(run);

        Assert.True(cleanedUp);
        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.Equal(StepStatus.Cancelled, WorkflowKit.Step(result, "work").Status);
    }

    [Fact]
    public async Task Tool_that_ignores_cancellation_is_abandoned_after_the_drain_timeout()
    {
        const string json = """{"steps":[{"id":"stuck","tool":"stuck"},{"id":"next","tool":"next"}]}""";
        using var cts = new CancellationTokenSource();
        var started = NewSignal();
        var release = NewSignal();
        var host = new RecordingHost().On("stuck", async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new HostToolResult("late");
        });
        var options = new RunOptions { MaxConcurrency = 1, CancellationDrainTimeout = TimeSpan.FromMilliseconds(50) };

        try
        {
            var run = WorkflowKit.RunAsync(json, host, options, cts.Token);
            await WorkflowKit.Within(started.Task);
            cts.Cancel();
            var result = await WorkflowKit.Within(run);

            Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
            var stuck = WorkflowKit.Step(result, "stuck");
            Assert.Equal(StepStatus.Cancelled, stuck.Status);
            Assert.Contains("drain timeout", stuck.Error);
            Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "next").Status);
            Assert.Equal(new[] { "stuck", "next" }, result.NeverRanStepIds);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Caller_cancellation_fired_during_a_start_burst_prevents_starting_the_remaining_ready_steps()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t","after":[]},
              {"id":"b","tool":"t","after":[]}
            ]}
            """;
        using var cts = new CancellationTokenSource();
        var host = new RecordingHost().On("t", (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(new HostToolResult("ok"));
        });

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, WorkflowKit.Options(maxConcurrency: 2), cts.Token));

        Assert.Equal(WorkflowRunStatus.Cancelled, result.Status);
        Assert.Equal(1, host.CallCount("t"));
        Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "b").Status);
    }

    [Fact]
    public async Task Tool_throwing_task_canceled_without_caller_cancellation_fails_the_step_and_skips_dependents()
    {
        const string json = """{"steps":[{"id":"a","tool":"http"},{"id":"b","tool":"next"}]}""";
        var host = new RecordingHost()
            .On("http", (_, _) => throw new TaskCanceledException("http client timeout"))
            .Respond("next", "never");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, WorkflowKit.Step(result, "a").Status);
        Assert.Equal(StepStatus.Skipped, WorkflowKit.Step(result, "b").Status);
        Assert.Equal(0, host.CallCount("next"));
    }

    [Fact]
    public async Task Timeout_cancels_the_in_flight_step_and_reports_timed_out()
    {
        const string json = """
            {"steps":[
              {"id":"slow","tool":"slow","after":[]},
              {"id":"later","tool":"later","after":[]}
            ]}
            """;
        var clock = new ManualClock();
        var started = NewSignal();
        var host = new RecordingHost().On("slow", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HostToolResult("never");
        });

        var run = WorkflowKit.RunAsync(json, host, WorkflowKit.Options(1, clock, TimeSpan.FromSeconds(1)));
        await WorkflowKit.Within(started.Task);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await WorkflowKit.Within(run);

        Assert.Equal(WorkflowRunStatus.TimedOut, result.Status);
        Assert.Equal(StepStatus.Cancelled, WorkflowKit.Step(result, "slow").Status);
        Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "later").Status);
        Assert.Equal(new[] { "slow" }, host.CalledTools);
        Assert.Equal(new[] { "slow", "later" }, result.NeverRanStepIds);
        Assert.Contains("did not finish within 1s", result.Error);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
    }

    [Fact]
    public async Task Timeout_keeps_the_results_of_steps_that_finished_before_it()
    {
        const string json = """
            {"steps":[
              {"id":"quick","tool":"quick","after":[]},
              {"id":"slow","tool":"slow"},
              {"id":"after-slow","tool":"next"}
            ]}
            """;
        var clock = new ManualClock();
        var started = NewSignal();
        var host = new RecordingHost()
            .Respond("quick", "done")
            .On("slow", async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new HostToolResult("never");
            });

        var run = WorkflowKit.RunAsync(json, host, WorkflowKit.Options(1, clock, TimeSpan.FromSeconds(2)));
        await WorkflowKit.Within(started.Task);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await WorkflowKit.Within(run);

        Assert.Equal(WorkflowRunStatus.TimedOut, result.Status);
        Assert.Equal("done", WorkflowKit.Step(result, "quick").Content);
        Assert.Equal(StepStatus.Succeeded, WorkflowKit.Step(result, "quick").Status);
        Assert.Equal(StepStatus.Cancelled, WorkflowKit.Step(result, "slow").Status);
        Assert.Equal(StepStatus.NotRun, WorkflowKit.Step(result, "after-slow").Status);
    }

    [Fact]
    public async Task Run_finishing_before_the_timeout_reports_completed_and_clock_never_fires()
    {
        const string json = """{"steps":[{"id":"a","tool":"t"},{"id":"b","tool":"t"}]}""";
        var clock = new ManualClock();
        var host = new RecordingHost().Respond("t", "ok");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, WorkflowKit.Options(1, clock, TimeSpan.FromSeconds(5))));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.Equal(2, host.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Max_concurrency_below_one_is_rejected_before_any_call(int maxConcurrency)
    {
        var host = new RecordingHost().Respond("t", "ok");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WorkflowKit.RunAsync("""{"steps":[{"id":"a","tool":"t"}]}""", host, new RunOptions { MaxConcurrency = maxConcurrency }));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Non_positive_timeout_is_rejected()
    {
        var host = new RecordingHost();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WorkflowKit.RunAsync("""{"steps":[]}""", host, new RunOptions { Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public async Task Negative_drain_timeout_is_rejected()
    {
        var host = new RecordingHost();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WorkflowKit.RunAsync("""{"steps":[]}""", host, new RunOptions { CancellationDrainTimeout = TimeSpan.FromMilliseconds(-1) }));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
