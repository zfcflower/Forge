using System.Threading;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>Concurrency, isolation and cooperative-cancellation behavior of
/// <see cref="ScriptCodeRunner"/> through its public API only.</summary>
public sealed class ScriptCodeRunnerConcurrencyTests
{
    private static readonly TimeSpan BarrierBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Parallel_tool_calls_share_a_bounded_slot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var host = new RecordingHostFacade
        {
            Handler = async (name, _, ct) =>
            {
                if (Interlocked.Increment(ref enteredCount) == 2)
                {
                    bothEntered.TrySetResult();
                }

                await release.Task.WaitAsync(ct);
                return new HostToolResult("v-" + name);
            },
        };
        var tools = new[] { "a", "b", "c", "d" }.Select(name => ScriptCodeSupport.Tool(name)).ToArray();
        var options = new CodeExecutionOptions { MaxConcurrentToolCalls = 2 };

        var run = ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(
                "var tasks = new[]{ Tools.Call(\"a\"), Tools.Call(\"b\"), Tools.Call(\"c\"), Tools.Call(\"d\") }; " +
                "return string.Join(\",\", await System.Threading.Tasks.Task.WhenAll(tasks));",
                tools, options),
            host, CancellationToken.None);
        try
        {
            await bothEntered.Task.WaitAsync(BarrierBudget);
            Assert.Equal(2, Volatile.Read(ref enteredCount));
            Assert.Equal(2, host.CallCount);
            Assert.Equal(2, host.InFlight);
            Assert.Equal(2, host.MaxObservedConcurrency);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await run;
        Assert.False(result.IsError, result.Content);
        Assert.Equal("v-a,v-b,v-c,v-d", result.Content);
        Assert.Equal(4, host.CallCount);
        Assert.Equal(2, host.MaxObservedConcurrency);
    }

    [Fact]
    public async Task Serial_slot_still_completes_parallel_calls()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var host = new RecordingHostFacade
        {
            Handler = async (name, _, ct) =>
            {
                if (Interlocked.Increment(ref enteredCount) == 1)
                {
                    firstEntered.TrySetResult();
                }

                await release.Task.WaitAsync(ct);
                return new HostToolResult("v-" + name);
            },
        };
        var tools = new[] { "a", "b", "c" }.Select(name => ScriptCodeSupport.Tool(name)).ToArray();
        var options = new CodeExecutionOptions { MaxConcurrentToolCalls = 1 };

        var run = ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(
                "var tasks = new[]{ Tools.Call(\"a\"), Tools.Call(\"b\"), Tools.Call(\"c\") }; " +
                "return string.Join(\",\", await System.Threading.Tasks.Task.WhenAll(tasks));",
                tools, options),
            host, CancellationToken.None);
        try
        {
            await firstEntered.Task.WaitAsync(BarrierBudget);
            Assert.Equal(1, Volatile.Read(ref enteredCount));
            Assert.Equal(1, host.CallCount);
            Assert.Equal(1, host.InFlight);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await run;
        Assert.False(result.IsError, result.Content);
        Assert.Equal("v-a,v-b,v-c", result.Content);
        Assert.Equal(3, host.CallCount);
        Assert.Equal(1, host.MaxObservedConcurrency);
    }

    [Fact]
    public async Task Host_call_receives_cancellation_and_run_stops_without_termination()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancel = 0;
        var host = new RecordingHostFacade
        {
            Handler = async (_, _, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return new HostToolResult("never");
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref observedCancel, 1);
                    throw;
                }
            },
        };
        var tools = new[] { ScriptCodeSupport.Tool("slow") };

        var run = ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request("return await Tools.Call(\"slow\");", tools, timeoutMilliseconds: 30_000),
            host, cts.Token);
        try
        {
            await entered.Task.WaitAsync(BarrierBudget);
            cts.Cancel();
        }
        finally
        {
            cts.Cancel();
        }

        var result = await run;
        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("cancelled and the script stopped", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref observedCancel));
        Assert.Equal(1, host.CallCount);
    }

    [Fact]
    public async Task Cancel_from_inside_a_tool_call_stops_without_termination()
    {
        // In-callback cancel: the script already started, so it unwinds cooperatively.
        var runner = new ScriptCodeRunner();
        using var cts = new CancellationTokenSource();
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(new HostToolResult("go"));
            },
        };
        var tools = new[] { ScriptCodeSupport.Tool("started") };

        var result = await ScriptCodeSupport.ExecuteAsync(
            runner,
            ScriptCodeSupport.Request(
                "var r = await Tools.Call(\"started\"); " +
                "await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(30), Cancellation); return r;",
                tools, timeoutMilliseconds: 30_000),
            host, cts.Token);

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("cancelled and the script stopped", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, host.CallCount);
    }

    [Fact]
    public async Task Cooperative_timeout_stops_without_termination_and_worker_is_reusable()
    {
        var runner = new ScriptCodeRunner();
        var request = ScriptCodeSupport.Request(
            "await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(30), Cancellation); return \"never\";",
            timeoutMilliseconds: 400);

        var stopped = await ScriptCodeSupport.ExecuteAsync(runner, request, new RecordingHostFacade(), CancellationToken.None);

        Assert.True(stopped.IsError);
        Assert.False(stopped.RequiresTermination);
        Assert.Contains("was stopped after", stopped.Content, StringComparison.Ordinal);
        Assert.Contains("0.4 s", stopped.Content, StringComparison.Ordinal);

        var reuse = await ScriptCodeSupport.ExecuteAsync(
            runner, ScriptCodeSupport.Request("return 1 + 1;"), new RecordingHostFacade(), CancellationToken.None);
        Assert.Equal("2", reuse.Content);
    }

    [Fact]
    public async Task Cooperative_cancel_after_a_tool_call_leaves_no_extra_callbacks()
    {
        var runner = new ScriptCodeRunner();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingHostFacade
        {
            Handler = (_, _, _) =>
            {
                entered.TrySetResult();
                return Task.FromResult(new HostToolResult("T"));
            },
        };
        var tools = new[] { ScriptCodeSupport.Tool("t") };

        var run = ScriptCodeSupport.ExecuteAsync(
            runner,
            ScriptCodeSupport.Request(
                "var r = await Tools.Call(\"t\"); " +
                "await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(30), Cancellation); return r;",
                tools, timeoutMilliseconds: 30_000),
            host, cts.Token);
        try
        {
            await entered.Task.WaitAsync(BarrierBudget);
            cts.Cancel();
        }
        finally
        {
            cts.Cancel();
        }

        var stopped = await run;
        Assert.True(stopped.IsError);
        Assert.False(stopped.RequiresTermination);
        Assert.Equal(1, host.CallCount);

        host.Handler = (_, _, _) => Task.FromResult(new HostToolResult("T2"));
        var reuse = await ScriptCodeSupport.ExecuteAsync(
            runner, ScriptCodeSupport.Request("return await Tools.Call(\"t\");", tools),
            host, CancellationToken.None);
        Assert.Equal("T2", reuse.Content);
        Assert.Equal(2, host.CallCount);
    }

    [Fact]
    public async Task Changed_tool_capabilities_do_not_leak_across_requests()
    {
        var runner = new ScriptCodeRunner();
        var host = new RecordingHostFacade
        {
            Handler = (name, _, _) => Task.FromResult(new HostToolResult("R-" + name)),
        };
        const string Code = "return await Tools.Call(\"alpha\");";

        var first = await ScriptCodeSupport.ExecuteAsync(
            runner, ScriptCodeSupport.Request(Code, new[] { ScriptCodeSupport.Tool("alpha") }),
            host, CancellationToken.None);
        Assert.Equal("R-alpha", first.Content);

        // Same source, different capabilities: the previous tool must now be unknown.
        var second = await ScriptCodeSupport.ExecuteAsync(
            runner, ScriptCodeSupport.Request(Code, new[] { ScriptCodeSupport.Tool("beta") }),
            host, CancellationToken.None);
        Assert.True(second.IsError);
        Assert.Contains("Unknown tool 'alpha'. Available tools: beta.", second.Content, StringComparison.Ordinal);

        var third = await ScriptCodeSupport.ExecuteAsync(
            runner, ScriptCodeSupport.Request("return await Tools.Call(\"beta\");", new[] { ScriptCodeSupport.Tool("beta") }),
            host, CancellationToken.None);
        Assert.Equal("R-beta", third.Content);
    }

    [Fact]
    public async Task Concurrent_runs_with_same_source_keep_logs_and_results_isolated()
    {
        var runner = new ScriptCodeRunner();
        const string Code = "var r = await Tools.Call(\"who\"); Log(\"saw:\" + r); return r;";
        var tools = new[] { ScriptCodeSupport.Tool("who") };
        var hostA = new RecordingHostFacade { Handler = (_, _, _) => Task.FromResult(new HostToolResult("A")) };
        var hostB = new RecordingHostFacade { Handler = (_, _, _) => Task.FromResult(new HostToolResult("B")) };

        var runA = ScriptCodeSupport.ExecuteAsync(runner, ScriptCodeSupport.Request(Code, tools, runId: "run-A"), hostA, CancellationToken.None);
        var runB = ScriptCodeSupport.ExecuteAsync(runner, ScriptCodeSupport.Request(Code, tools, runId: "run-B"), hostB, CancellationToken.None);
        var results = await Task.WhenAll(runA, runB).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("A\n[log]\nsaw:A", results[0].Content);
        Assert.Equal("B\n[log]\nsaw:B", results[1].Content);
        Assert.Equal(1, hostA.CallCount);
        Assert.Equal(1, hostB.CallCount);
    }

    [Fact]
    public async Task Precanceled_token_runs_no_script_and_makes_no_facade_calls()
    {
        var runner = new ScriptCodeRunner();
        var host = new RecordingHostFacade { Handler = (_, _, _) => Task.FromResult(new HostToolResult("x")) };
        var tools = new[] { ScriptCodeSupport.Tool("t") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ExecuteAsync(ScriptCodeSupport.Request("await Tools.Call(\"t\"); return 1;", tools), host, cts.Token));

        Assert.Equal(0, host.CallCount);
    }
}
