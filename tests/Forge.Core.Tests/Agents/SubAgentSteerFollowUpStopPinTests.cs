using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins steer / follow-up / stop semantics of live sub-agent runs.</summary>
public sealed class SubAgentSteerFollowUpStopPinTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private static readonly AgentDefinition OpenScout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), null);

    private static AgentOptions Options(int maxParallel = 4, int maxDepth = 3, int maxTurns = 10) =>
        new() { MaxParallelSubAgents = maxParallel, MaxSubAgentDepth = maxDepth, MaxTurns = maxTurns };

    private static string Spawn(string kind, string task) =>
        $$"""{"kind":"{{kind}}","task":"{{task}}"}""";

    private static int ToolRounds(ChatRequest request) =>
        request.Messages.Count(m => m.Role == MessageRole.Tool);

    private sealed class ListSink : IAgentEventSink
    {
        public readonly ConcurrentQueue<AgentEvent> Events = new();
        public void Emit(AgentEvent value) => Events.Enqueue(value);
    }

    private sealed class GateApprovalHandler(TaskCompletionSource entered, TaskCompletionSource release) : IApprovalHandler
    {
        public int Calls;
        public ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            entered.TrySetResult();
            return new ValueTask<ApprovalDecision>(release.Task.WaitAsync(ct).ContinueWith(
                _ => ApprovalDecision.Allow, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default));
        }
    }

    private static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(15);
        }
    }

    /// <summary>Opens a root session under the temp workspace and starts its <c>go</c> turn in the background.</summary>
    private static (Task<List<AgentEvent>> Run, AgentInstance Root) StartRun(AgentHost host, string sessionPrefix)
    {
        var root = host.CreateRoot(sessionPrefix + "-" + Guid.NewGuid().ToString("n"), Workspace);
        return (Task.Run(() => AgentTestHost.CollectAsync(host.RunAsync(root, "go"))), root);
    }

    /// <summary>Waits for <paramref name="entered"/>, then until the scheduler has registered
    /// <paramref name="childCount"/> live children, and returns their ids.</summary>
    private static async Task<IReadOnlyList<string>> AwaitChildrenAsync(AgentHost host, Task entered, int childCount = 1)
    {
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        await SpinUntilAsync(() => host.SubAgents.Instances.Count == childCount, TimeSpan.FromSeconds(10));
        return [.. host.SubAgents.Instances.Select(instance => instance.Id)];
    }

    /// <summary>Parent spawns one Scout child on <c>job</c>; that child blocks in its first turn until
    /// <paramref name="release"/> completes, then answers an instruction containing
    /// <paramref name="marker"/> with <paramref name="reply"/>.</summary>
    private static FakeProvider HoldingChildProvider(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        string marker,
        string reply) =>
        new(async (request, index, ct) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "job")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Turn.Say("part1");
            }

            if (last.Contains(marker, StringComparison.Ordinal))
            {
                return Turn.Say(reply);
            }

            return request.Messages.Count == 1 && last == "go"
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "job")))
                : Turn.Say("parent done");
        });

    // ---- Steer ----

    [Fact]
    public async Task Steer_at_batch_boundary_skips_remaining_calls_with_error_results()
    {
        var w1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var w1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var w2Invoked = 0;
        ITool Writer(string name, ToolSideEffect effect, Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> handler) =>
            new FakeTool(name, effect, handler);
        var tools = new List<ITool>
        {
            Writer("w1", ToolSideEffect.Write, async (_, _, ct) =>
            {
                w1Entered.TrySetResult();
                await w1Release.Task.WaitAsync(ct);
                return new ToolResult("w1 ok");
            }),
            Writer("w2", ToolSideEffect.Write, (_, _, _) =>
            {
                Interlocked.Increment(ref w2Invoked);
                return new ValueTask<ToolResult>(new ToolResult("w2 ok"));
            }),
        };
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "job")
            {
                return Turn.Call(("c1", "w1", "{}"), ("c2", "w2", "{}"));
            }

            if (last.Contains("redirect", StringComparison.Ordinal))
            {
                return Turn.Say("steered done");
            }

            return index == 0
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "job")))
                : Turn.Say("parent done");
        });
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var host = AgentTestHost.Create(provider, tools, options: Options(), agents: [OpenScout], policy: policy);
        var (runTask, root) = StartRun(host, "session-steer");
        var childId = (await AwaitChildrenAsync(host, w1Entered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(childId, "redirect now"));
        w1Release.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var steered = Assert.Single(events.OfType<AgentSubAgentSteered>());
        Assert.Equal(childId, steered.AgentId);
        Assert.Equal("redirect now", steered.Instruction);
        Assert.Equal(1, steered.DroppedToolCalls);
        Assert.Equal(0, Volatile.Read(ref w2Invoked));

        var child = host.SubAgents.Instances.Single();
        var toolMessage = child.History.First(m => m.Role == MessageRole.Tool);
        var results = toolMessage.Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.False(results[0].IsError);
        Assert.True(results[1].IsError);
        Assert.Contains("skipped: the agent was redirected", results[1].Content, StringComparison.Ordinal);
        var parentResult = root.History.SelectMany(m => m.Content.OfType<ToolResultBlock>()).First(r => r.Content.Contains(childId, StringComparison.Ordinal));
        Assert.False(parentResult.IsError);
        Assert.Contains("steered done", parentResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_steers_before_one_boundary_produce_one_message_one_event_in_order()
    {
        var w1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var w1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new List<ITool>
        {
            new FakeTool("w1", ToolSideEffect.Write, async (_, _, ct) =>
            {
                w1Entered.TrySetResult();
                await w1Release.Task.WaitAsync(ct);
                return new ToolResult("w1 ok");
            }),
            new FakeTool("w2", ToolSideEffect.Write, (_, _, _) => new ValueTask<ToolResult>(new ToolResult("w2 ok"))),
        };
        string? secondTurnText = null;
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "job")
            {
                return Turn.Call(("c1", "w1", "{}"), ("c2", "w2", "{}"));
            }

            if (last.Contains("first", StringComparison.Ordinal))
            {
                secondTurnText = last;
                return Turn.Say("steered done");
            }

            return index == 0
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "job")))
                : Turn.Say("parent done");
        });
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var host = AgentTestHost.Create(provider, tools, options: Options(), agents: [OpenScout], policy: policy);
        var (runTask, _) = StartRun(host, "session-steer2");
        var childId = (await AwaitChildrenAsync(host, w1Entered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(childId, "first instruction"));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(childId, "second instruction"));
        w1Release.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var steered = Assert.Single(events.OfType<AgentSubAgentSteered>());
        Assert.Equal("first instruction\n\nsecond instruction", steered.Instruction);
        Assert.Equal(1, steered.DroppedToolCalls);
        Assert.NotNull(secondTurnText);
        Assert.True(
            secondTurnText.IndexOf("first instruction", StringComparison.Ordinal)
            < secondTurnText.IndexOf("second instruction", StringComparison.Ordinal),
            "Steering order must be preserved.");
    }

    [Fact]
    public async Task Steer_after_the_last_call_is_delivered_with_zero_dropped()
    {
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = HoldingChildProvider(turnEntered, turnRelease, "late steer", "steered done");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (runTask, _) = StartRun(host, "session-steer3");
        var childId = (await AwaitChildrenAsync(host, turnEntered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(childId, "late steer"));
        turnRelease.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var steered = Assert.Single(events.OfType<AgentSubAgentSteered>());
        Assert.Equal(0, steered.DroppedToolCalls);
        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Completed, finished.Status);
    }

    [Fact]
    public async Task Steer_during_read_only_parallel_batch_lets_in_flight_calls_finish()
    {
        var r1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r2Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r1Count = 0;
        var r2Count = 0;
        var tools = new List<ITool>
        {
            new FakeTool("r1", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                Interlocked.Increment(ref r1Count);
                r1Entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new ToolResult("r1 ok");
            }),
            new FakeTool("r2", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                Interlocked.Increment(ref r2Count);
                r2Entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new ToolResult("r2 ok");
            }),
        };
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "job")
            {
                return Turn.Call(("c1", "r1", "{}"), ("c2", "r2", "{}"));
            }

            if (last.Contains("redirect", StringComparison.Ordinal))
            {
                return Turn.Say("steered done");
            }

            return index == 0
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "job")))
                : Turn.Say("parent done");
        });
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var host = AgentTestHost.Create(provider, tools, options: Options(), agents: [OpenScout], policy: policy);
        var (runTask, _) = StartRun(host, "session-steer4");
        var childId = (await AwaitChildrenAsync(host, Task.WhenAll(r1Entered.Task, r2Entered.Task))).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(childId, "redirect after batch"));
        release.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(1, Volatile.Read(ref r1Count));
        Assert.Equal(1, Volatile.Read(ref r2Count));
        var steered = Assert.Single(events.OfType<AgentSubAgentSteered>());
        Assert.Equal(0, steered.DroppedToolCalls);
        var child = host.SubAgents.Instances.Single();
        var toolMessage = child.History.First(m => m.Role == MessageRole.Tool);
        Assert.All(toolMessage.Content.OfType<ToolResultBlock>(), r => Assert.False(r.IsError));
    }

    // ---- FollowUp ----

    [Fact]
    public async Task FollowUp_continues_instead_of_finishing_and_reports_pending()
    {
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = HoldingChildProvider(turnEntered, turnRelease, "more work", "part2");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (runTask, _) = StartRun(host, "session-follow");
        var childId = (await AwaitChildrenAsync(host, turnEntered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.FollowUp(childId, "more work"));
        turnRelease.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var queued = Assert.Single(events.OfType<AgentSubAgentFollowUpQueued>());
        Assert.Equal(1, queued.Pending);
        Assert.Equal("more work", queued.Instruction);
        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Completed, finished.Status);
        var child = host.SubAgents.Instances.Single();
        var texts = child.History.Select(m => Turn.Text(m)).ToArray();
        Assert.Contains("part1", texts);
        Assert.Contains("part2", texts);
    }

    [Fact]
    public async Task Multiple_followUps_are_delivered_in_order_with_pending_counts()
    {
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = HoldingChildProvider(turnEntered, turnRelease, "extra", "final");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (runTask, _) = StartRun(host, "session-follow2");
        var childId = (await AwaitChildrenAsync(host, turnEntered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.FollowUp(childId, "extra one"));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.FollowUp(childId, "extra two"));
        turnRelease.TrySetResult();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var queued = events.OfType<AgentSubAgentFollowUpQueued>().ToArray();
        Assert.Equal(2, queued.Length);
        Assert.Equal([1, 2], queued.Select(q => q.Pending).ToArray());
        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Completed, finished.Status);
        var childRequest = provider.Requests.Last(r => Turn.LastUserText(r).Contains("extra", StringComparison.Ordinal));
        var fullText = string.Join("\n", childRequest.Messages.Where(m => m.Role == MessageRole.User).Select(Turn.Text));
        Assert.True(
            fullText.IndexOf("extra one", StringComparison.Ordinal) < fullText.IndexOf("extra two", StringComparison.Ordinal),
            "Follow-ups must be delivered in order.");
    }

    // ---- Stop ----

    [Fact]
    public async Task Stop_cancels_only_that_child_and_returns_partial_output_without_error()
    {
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new List<ITool>
        {
            new FakeTool("slow", ToolSideEffect.Write, async (_, _, ct) =>
            {
                slowEntered.TrySetResult();
                await slowRelease.Task.WaitAsync(ct);
                return new ToolResult("slow ok");
            }),
        };
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "slow")
            {
                var assistant = new ChatMessage(
                    MessageRole.Assistant,
                    [new TextBlock("partial-A"), new ToolCallBlock("c1", "slow", "{}")]);
                return [new MessageCompleted(assistant, new TokenUsage(10, 5), StopReason.ToolUse)];
            }

            if (last == "fast")
            {
                return Turn.Say("b done");
            }

            return request.Messages.Count == 1 && last == "go"
                ? Turn.Call(
                    ("call-1", "spawn_agent", Spawn("Scout", "slow")),
                    ("call-2", "spawn_agent", Spawn("Scout", "fast")))
                : Turn.Say("parent done");
        });
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var host = AgentTestHost.Create(provider, tools, options: Options(), agents: [OpenScout], policy: policy);
        var (runTask, root) = StartRun(host, "session-stop");
        await AwaitChildrenAsync(host, slowEntered.Task, childCount: 2);
        var slowId = host.SubAgents.Instances.Single(i => i.History.Any(m => Turn.Text(m).Contains("partial-A", StringComparison.Ordinal))).Id;

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop(slowId, "enough"));
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(events.OfType<AgentFailed>());
        var finished = events.OfType<AgentSubAgentFinished>().ToArray();
        Assert.Equal(2, finished.Length);
        var stopped = finished.Single(f => f.AgentId == slowId);
        Assert.Equal(SubAgentStatus.Stopped, stopped.Status);
        Assert.Contains("enough", stopped.Error ?? string.Empty, StringComparison.Ordinal);
        var sibling = finished.Single(f => f.AgentId != slowId);
        Assert.Equal(SubAgentStatus.Completed, sibling.Status);

        var results = root.History.SelectMany(m => m.Content.OfType<ToolResultBlock>()).ToArray();
        var stoppedResult = results.Single(r => r.Content.Contains(slowId, StringComparison.Ordinal));
        Assert.False(stoppedResult.IsError);
        Assert.Contains("was stopped", stoppedResult.Content, StringComparison.Ordinal);
        Assert.Contains("partial-A", stoppedResult.Content, StringComparison.Ordinal);
        var siblingResult = results.Single(r => r.Content.Contains(sibling.AgentId, StringComparison.Ordinal));
        Assert.False(siblingResult.IsError);
        Assert.Contains("b done", siblingResult.Content, StringComparison.Ordinal);
        Assert.Contains(events.OfType<AgentTurnCompleted>(), t => t.AgentId == "root");
    }

    [Fact]
    public async Task Stop_while_a_tool_awaits_approval_releases_the_wait()
    {
        var approvalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolInvoked = 0;
        var tools = new List<ITool>
        {
            new FakeTool("gated", ToolSideEffect.Execute, (_, _, _) =>
            {
                Interlocked.Increment(ref toolInvoked);
                return new ValueTask<ToolResult>(new ToolResult("gated ok"));
            }),
        };
        var handler = new GateApprovalHandler(approvalEntered, approvalRelease);
        var policy = new DefaultApprovalPolicy
        {
            ToolRules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal) { ["gated"] = ApprovalMode.Ask },
        };

        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "job")
            {
                return Turn.Call(("c1", "gated", "{}"));
            }

            return index == 0
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "job")))
                : Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, tools, options: Options(), agents: [OpenScout], policy: policy, handler: handler);
        var (runTask, _) = StartRun(host, "session-stop-approval");
        var childId = (await AwaitChildrenAsync(host, approvalEntered.Task)).Single();

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop(childId, "halt"));
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Stopped, finished.Status);
        Assert.Equal(0, Volatile.Read(ref toolInvoked));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Stop_of_a_queued_child_finishes_stopped_without_running_or_invoking_the_provider()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "first")
            {
                firstEntered.TrySetResult();
                await firstRelease.Task.WaitAsync(ct);
                return Turn.Say("first done");
            }

            Assert.NotEqual("second", last);
            return Turn.Say("unexpected");
        });
        var sink = new ListSink();
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 1), agents: [Scout]);
        var context = new ToolContext("sess-queued-stop-" + Guid.NewGuid().ToString("n"), Workspace);

        var firstTask = host.SubAgents.SpawnAsync("Scout", "first", null, context, sink, "root", CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SpinUntilAsync(() => host.SubAgents.Instances.Count == 1, TimeSpan.FromSeconds(10));
        var secondTask = host.SubAgents.SpawnAsync("Scout", "second", null, context, sink, "root", CancellationToken.None);
        await SpinUntilAsync(() => host.SubAgents.Instances.Count == 2, TimeSpan.FromSeconds(10));
        await SpinUntilAsync(
            () => sink.Events.OfType<AgentSubAgentQueued>().Any(),
            TimeSpan.FromSeconds(10));
        var queuedId = sink.Events.OfType<AgentSubAgentQueued>().Single().AgentId;

        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop(queuedId, "no longer needed"));
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(queuedId, second.AgentId);
        Assert.False(second.IsError);
        Assert.Contains("was stopped", second.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sink.Events.OfType<AgentSubAgentRunning>(),
            r => r.AgentId == queuedId);
        var finished = sink.Events.OfType<AgentSubAgentFinished>().Single(f => f.AgentId == queuedId);
        Assert.Equal(SubAgentStatus.Stopped, finished.Status);
        Assert.DoesNotContain(provider.Requests, r => Turn.LastUserText(r) == "second");

        firstRelease.TrySetResult();
        var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(first.IsError);
        var firstFinished = sink.Events.OfType<AgentSubAgentFinished>().Single(f => f.AgentId == first.AgentId);
        Assert.Equal(SubAgentStatus.Completed, firstFinished.Status);
    }

    [Fact]
    public async Task Parent_cancellation_finishes_each_child_stopped_with_parent_cancelled()
    {
        var entered = new ConcurrentBag<TaskCompletionSource>();
        var enteredCount = 0;
        var enteredSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            var last = Turn.LastUserText(request);
            if (last is "slow one" or "slow two")
            {
                if (Interlocked.Increment(ref enteredCount) == 2)
                {
                    enteredSignal.TrySetResult();
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Turn.Say("never");
            }

            return request.Messages.Count == 1 && last == "go"
                ? Turn.Call(
                    ("call-1", "spawn_agent", Spawn("Scout", "slow one")),
                    ("call-2", "spawn_agent", Spawn("Scout", "slow two")))
                : Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var root = host.CreateRoot("session-cancel-" + Guid.NewGuid().ToString("n"), Workspace);
        using var cancellation = new CancellationTokenSource();

        var runTask = Task.Run(() => AgentTestHost.CollectAsync(host.RunAsync(root, "go", cancellation.Token)));
        await enteredSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var events = await runTask.WaitAsync(TimeSpan.FromSeconds(20));

        var finished = events.OfType<AgentSubAgentFinished>().ToArray();
        Assert.Equal(2, finished.Length);
        Assert.Equal(2, finished.Select(f => f.AgentId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(finished, f =>
        {
            Assert.Equal(SubAgentStatus.Stopped, f.Status);
            Assert.Equal("parent cancelled", f.Error);
        });
    }
}
