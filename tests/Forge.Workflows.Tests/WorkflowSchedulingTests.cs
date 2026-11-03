using System.Collections.Concurrent;
using Forge.Plugin.Sdk;
using Forge.Workflows.Tests.Support;
using Xunit;

namespace Forge.Workflows.Tests;

public sealed class WorkflowSchedulingTests
{
    [Fact]
    public async Task Implicit_after_chains_each_step_to_the_previous_one()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"t"},
              {"id":"b","tool":"t"},
              {"id":"c","tool":"t"}
            ]}
            """;
        var host = new RecordingHost().Respond("t", "ok", delay: TimeSpan.FromMilliseconds(5));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Empty(WorkflowKit.Step(result, "a").DependsOn);
        Assert.Equal(new[] { "a" }, WorkflowKit.Step(result, "b").DependsOn);
        Assert.Equal(new[] { "b" }, WorkflowKit.Step(result, "c").DependsOn);
        Assert.Equal(1, host.MaxInFlight);
        Assert.Equal(3, host.Calls.Count);
    }

    [Fact]
    public async Task Empty_after_list_makes_a_parallel_root()
    {
        const string json = """
            {"steps":[
              {"id":"left","tool":"left","after":[]},
              {"id":"right","tool":"right","after":[]},
              {"id":"join","tool":"join","after":["left","right"]}
            ]}
            """;
        var rendezvous = new Rendezvous(2, WorkflowKit.Guard);
        var host = new RecordingHost()
            .On("left", async (_, ct) =>
            {
                await rendezvous.ArriveAsync(ct);
                return new HostToolResult("left");
            })
            .On("right", async (_, ct) =>
            {
                await rendezvous.ArriveAsync(ct);
                return new HostToolResult("right");
            })
            .Respond("join", "joined");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Empty(WorkflowKit.Step(result, "left").DependsOn);
        Assert.Empty(WorkflowKit.Step(result, "right").DependsOn);
        Assert.Equal(new[] { "left", "right" }, WorkflowKit.Step(result, "join").DependsOn);
        Assert.Equal(2, host.MaxInFlight);
        Assert.Equal("join", host.CalledTools[^1]);
    }

    [Fact]
    public async Task Fan_out_never_exceeds_max_concurrency_and_reaches_it()
    {
        const int limit = 3;
        var rendezvous = new Rendezvous(limit, WorkflowKit.Guard);
        var host = new RecordingHost().On("fan", async (_, ct) =>
        {
            await rendezvous.ArriveAsync(ct);
            return new HostToolResult("ok");
        });

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(FanOutDocument(10, "fan"), host, WorkflowKit.Options(maxConcurrency: limit)));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(10, result.Steps.Count(static step => step.Status == StepStatus.Succeeded));
        Assert.Equal(limit, host.MaxInFlight);
    }

    [Fact]
    public async Task Fan_out_with_slow_tools_stays_under_the_limit()
    {
        const int limit = 4;
        var host = new RecordingHost().Respond("slow", "ok", delay: TimeSpan.FromMilliseconds(5));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(FanOutDocument(12, "slow"), host, WorkflowKit.Options(maxConcurrency: limit)));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.InRange(host.MaxInFlight, 1, limit);
        Assert.Equal(12, host.CallCount("slow"));
    }

    [Fact]
    public async Task Diamond_join_starts_only_after_both_branches_complete()
    {
        const string json = """
            {"steps":[
              {"id":"a","tool":"a","after":[]},
              {"id":"b","tool":"b","after":["a"]},
              {"id":"c","tool":"c","after":["a"]},
              {"id":"d","tool":"d","after":["b","c"]}
            ]}
            """;
        var events = new ConcurrentQueue<string>();
        var host = new RecordingHost()
            .On("a", (_, _) => Finish(events, "a", TimeSpan.Zero))
            .On("b", (_, _) => Finish(events, "b", TimeSpan.FromMilliseconds(30)))
            .On("c", (_, _) => Finish(events, "c", TimeSpan.FromMilliseconds(5)))
            .On("d", (_, _) => Finish(events, "d", TimeSpan.Zero));

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "b", "c" }, WorkflowKit.Step(result, "d").DependsOn);
        var log = events.ToArray();
        Assert.Equal(8, log.Length);
        Assert.Equal("start:a", log[0]);
        Assert.True(Array.IndexOf(log, "end:b") < Array.IndexOf(log, "start:d"));
        Assert.True(Array.IndexOf(log, "end:c") < Array.IndexOf(log, "start:d"));
    }

    [Fact]
    public async Task Single_slot_run_starts_the_lowest_index_ready_step_first()
    {
        const string json = """
            {"steps":[
              {"id":"x","tool":"x","after":["z"]},
              {"id":"y","tool":"y","after":[]},
              {"id":"z","tool":"z","after":[]}
            ]}
            """;
        var host = new RecordingHost().Respond("x", "x").Respond("y", "y").Respond("z", "z");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, WorkflowKit.Options(maxConcurrency: 1)));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "y", "z", "x" }, host.CalledTools);
    }

    [Fact]
    public async Task Single_slot_schedule_is_identical_across_repeated_runs()
    {
        const string json = """
            {"steps":[
              {"id":"s0","tool":"s0","after":[]},
              {"id":"s1","tool":"s1","after":[]},
              {"id":"s2","tool":"s2","after":["s0"]},
              {"id":"s3","tool":"s3","after":[]},
              {"id":"s4","tool":"s4","after":["s1","s3"]}
            ]}
            """;
        var definition = WorkflowJson.Parse(json);
        var orders = new List<IReadOnlyList<string>>();

        for (var run = 0; run < 3; run++)
        {
            var host = new RecordingHost();
            foreach (var tool in new[] { "s0", "s1", "s2", "s3", "s4" })
            {
                host.Respond(tool, "ok");
            }

            var result = await WorkflowKit.Within(WorkflowKit.RunAsync(definition, host, WorkflowKit.Options(maxConcurrency: 1)));

            Assert.Equal(WorkflowRunStatus.Completed, result.Status);
            orders.Add(host.CalledTools);
        }

        var expected = new[] { "s0", "s1", "s2", "s3", "s4" };
        Assert.All(orders, order => Assert.Equal(expected, order));
    }

    [Fact]
    public async Task Results_are_reported_in_definition_order_regardless_of_completion_order()
    {
        const string json = """
            {"steps":[
              {"id":"slow","tool":"slow","after":[]},
              {"id":"fast","tool":"fast","after":[]},
              {"id":"last","tool":"fast","after":[]}
            ]}
            """;
        var host = new RecordingHost()
            .Respond("slow", "s", delay: TimeSpan.FromMilliseconds(40))
            .Respond("fast", "f");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        Assert.Equal(new[] { "slow", "fast", "last" }, result.Steps.Select(static step => step.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, result.Steps.Select(static step => step.Index).ToArray());
        Assert.Equal("s", WorkflowKit.Step(result, "slow").Content);
    }

    [Fact]
    public async Task Empty_workflow_completes_without_calling_the_host()
    {
        var host = new RecordingHost();

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync("""{"steps":[]}""", host));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Empty(result.Steps);
        Assert.Empty(result.NeverRanStepIds);
        Assert.Null(result.Error);
        Assert.Empty(host.Calls);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Successful_step_records_tool_name_and_output()
    {
        const string json = """{"steps":[{"id":"a","tool":"read_file"}]}""";
        var host = new RecordingHost().Respond("read_file", "hello");

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host));

        var step = WorkflowKit.Step(result, "a");
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal("read_file", step.Tool);
        Assert.Equal("hello", step.Content);
        Assert.False(step.IsError);
        Assert.Null(step.Error);
    }

    [Fact]
    public async Task Duration_and_timestamps_come_from_the_injected_clock()
    {
        const string json = """{"steps":[{"id":"a","tool":"slow"}]}""";
        var clock = new ManualClock();
        var host = new RecordingHost().On("slow", (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(new HostToolResult("done"));
        });

        var result = await WorkflowKit.Within(WorkflowKit.RunAsync(json, host, WorkflowKit.Options(clock: clock)));

        var step = WorkflowKit.Step(result, "a");
        Assert.Equal(TimeSpan.FromSeconds(3), step.Duration);
        Assert.Equal(step.StartedAt + TimeSpan.FromSeconds(3), step.CompletedAt);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);
        Assert.Equal(result.StartedAt + TimeSpan.FromSeconds(3), result.CompletedAt);
    }

    private static string FanOutDocument(int count, string tool)
    {
        var steps = Enumerable.Range(0, count).Select(i => $$"""{"id":"s{{i}}","tool":"{{tool}}","after":[]}""");
        return $$"""{"steps":[{{string.Join(",", steps)}}]}""";
    }

    private static async Task<HostToolResult> Finish(ConcurrentQueue<string> events, string name, TimeSpan delay)
    {
        events.Enqueue($"start:{name}");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        events.Enqueue($"end:{name}");
        return new HostToolResult(name);
    }
}
