using System.Reflection;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the frame coalescer contract (forge-app spec "事件路由与渲染"): many worker-thread items become a few UI-thread
/// flushes, order is preserved per producer, a single flush is capped, disposal drops unprocessed items, and the session
/// routes a mixed stream through the coalescer without changing the final transcript state.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FrameCoalescingTests
{
    private const string SessionId = "main-session";
    private const string ChildAgentId = "child-agent";
    private const string ChildSessionId = "child-session";
    private const string ChildKind = "Explore";
    private const string ChildTask = "look at the diff";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the coalescing test cannot drive the sink.");

    [Fact]
    public async Task Burst_of_ten_thousand_items_is_coalesced_into_few_flushes_and_keeps_order()
    {
        var session = SharedUi.Session;
        var (flushes, seen) = await session.Dispatch(
            async () =>
            {
                var processed = 0;
                var flushCount = 0;
                var handled = new List<int>();
                var coalescer = new FrameCoalescer<int>(
                    item =>
                    {
                        processed++;
                        handled.Add(item);
                    },
                    () => flushCount++);

                await Task.Run(
                    () =>
                    {
                        for (var i = 0; i < 10000; i++)
                        {
                            coalescer.Enqueue(i);
                        }
                    });

                Assert.True(
                    await PumpUntilAsync(() => processed == 10000),
                    "not all items were processed");
                Assert.True(
                    flushCount <= 100,
                    $"expected far fewer flushes than items, got {flushCount}");
                return (flushCount, handled);
            },
            CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 10000).ToArray(), seen.ToArray());
        Assert.True(flushes <= 100);
    }

    [Fact]
    public async Task Concurrent_producers_keep_each_producers_order()
    {
        var session = SharedUi.Session;
        const int producers = 4;
        const int perProducer = 2500;

        var handled = await session.Dispatch(
            async () =>
            {
                var seen = new List<int>();
                var coalescer = new FrameCoalescer<int>(seen.Add);

                await Task.WhenAll(
                    Enumerable.Range(0, producers).Select(
                        producer => Task.Run(
                            () =>
                            {
                                for (var j = 0; j < perProducer; j++)
                                {
                                    coalescer.Enqueue((producer * perProducer) + j);
                                }
                            })));

                Assert.True(
                    await PumpUntilAsync(() => seen.Count == producers * perProducer),
                    "not all items were processed");
                return seen;
            },
            CancellationToken.None);

        var byProducer = handled.GroupBy(item => item / perProducer).ToDictionary(group => group.Key, group => group.ToList());
        Assert.Equal(producers, byProducer.Count);
        foreach (var group in byProducer)
        {
            var expected = Enumerable.Range(0, perProducer).Select(j => (group.Key * perProducer) + j).ToArray();
            Assert.Equal(expected, group.Value.ToArray());
        }
    }

    [Fact]
    public async Task Single_flush_respects_the_item_cap()
    {
        var session = SharedUi.Session;
        var passCounts = await session.Dispatch(
            async () =>
            {
                const int total = 10000;
                var processed = 0;
                var passes = new List<int>();
                var currentPass = 0;
                var coalescer = new FrameCoalescer<int>(
                    _ =>
                    {
                        processed++;
                        currentPass++;
                    },
                    () =>
                    {
                        if (currentPass > 0)
                        {
                            passes.Add(currentPass);
                        }

                        currentPass = 0;
                    });

                // Enqueue synchronously on the UI thread so no flush can run mid-burst; the pass sizes are then exact.
                for (var i = 0; i < total; i++)
                {
                    coalescer.Enqueue(i);
                }

                Assert.True(
                    await PumpUntilAsync(() => processed == total),
                    "not all items were processed");
                if (currentPass > 0)
                {
                    passes.Add(currentPass);
                }

                return passes;
            },
            CancellationToken.None);

        Assert.NotEmpty(passCounts);
        Assert.All(passCounts, count => Assert.True(count <= FrameCoalescer<int>.MaxItemsPerFlush));
        Assert.Equal(10000, passCounts.Sum());
    }

    [Fact]
    public async Task Items_enqueued_after_dispose_are_dropped_without_throwing()
    {
        var session = SharedUi.Session;
        var processed = await session.Dispatch(
            async () =>
            {
                var handled = 0;
                var coalescer = new FrameCoalescer<int>(_ => handled++);
                coalescer.Enqueue(1);
                coalescer.Dispose();
                coalescer.Dispose();
                coalescer.Enqueue(2);

                // Let any already-posted flush run; it must be a no-op after disposal.
                await Task.Delay(50);

                return handled;
            },
            CancellationToken.None);

        Assert.Equal(0, processed);
    }

    [Fact]
    public async Task Session_mixed_event_stream_matches_per_event_processing()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                Emit(workspace, new AgentTextDelta("root", "hello"));
                Emit(workspace, new AgentToolStarted("root", new ToolCall("t1", "Read", "{}")));
                Emit(workspace, new AgentToolFinished("root", new ToolCall("t1", "Read", "{}"), new ToolResult("done")));
                Emit(workspace, Started());
                Emit(workspace, new AgentTextDelta(ChildAgentId, "child answer"));
                Emit(workspace, new AgentSubAgentFinished(
                    ChildAgentId,
                    SubAgentStatus.Completed,
                    TokenUsage.Empty,
                    TimeSpan.FromSeconds(1),
                    null,
                    At));

                Assert.True(
                    await PumpUntilAsync(() => workspace.Finished.Count == 1 && RootTools(workspace).Any(row => row.Status == "✓")),
                    "the mixed stream never reached its final state");
                return (RootTexts(workspace), RootTools(workspace), ChildTexts(workspace), workspace.SubAgents.Count, workspace.Finished.Count);
            },
            CancellationToken.None);

        Assert.Equal(new[] { "hello" }, state.Item1.ToArray());
        var tool = Assert.Single(state.Item2);
        Assert.Equal("Read", tool.Name);
        Assert.Equal("✓", tool.Status);
        Assert.Equal("done", tool.Result);
        Assert.Contains("child answer", state.Item3);
        Assert.Equal(0, state.Item4);
        Assert.Equal(1, state.Item5);
    }

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static AgentSubAgentStarted Started() =>
        new("root", ChildAgentId, ChildKind, ChildTask, "model", ChildSessionId, At);

    private static SessionWorkspaceViewModel CreateWorkspace()
    {
        var info = new SessionInfo(SessionId, Path.GetTempPath(), "demo session", At);
        var store = new EmptySessionStore(At);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", Forge.App.Localization.ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptySessionCatalog(At),
                    new EmptyTodoStore("t1", At),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            TestProviderServices.UnconfiguredProbe());
    }

    /// <summary>An empty catalogue for tests that never touch providers; self-cleaning because the catalogue is in memory after load.</summary>
    private static List<string> RootTexts(SessionWorkspaceViewModel workspace) =>
        CardBlocks(workspace.Transcript).OfType<TurnTextViewModel>()
            .Select(text => string.Concat(text.Segments.Select(segment => segment.Text)))
            .ToList();

    private static List<ToolRowViewModel> RootTools(SessionWorkspaceViewModel workspace) =>
        CardBlocks(workspace.Transcript).OfType<ToolRowViewModel>().ToList();

    private static List<string> ChildTexts(SessionWorkspaceViewModel workspace)
    {
        var texts = new List<string>();
        foreach (var agent in workspace.SubAgents.Concat(workspace.Finished))
        {
            texts.AddRange(
                CardBlocks(agent.Transcript).OfType<TurnTextViewModel>()
                    .Select(text => string.Concat(text.Segments.Select(segment => segment.Text))));
        }

        return texts;
    }

    private static IEnumerable<TurnBlockViewModel> CardBlocks(TranscriptViewModel transcript) =>
        transcript.Items.OfType<TurnCardViewModel>().SelectMany(card => card.Blocks);

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition);

}
