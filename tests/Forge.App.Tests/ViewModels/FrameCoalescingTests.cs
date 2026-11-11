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
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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
        var store = new EmptyStore();
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", Forge.App.Localization.ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptyCatalog(),
                    new EmptyTodoStore(),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            UnconfiguredProviders());
    }

    /// <summary>An empty catalogue for tests that never touch providers; self-cleaning because the catalogue is in memory after load.</summary>
    private static ProviderServices UnconfiguredProviders()
    {
        var probe = Path.Combine(Path.GetTempPath(), "forge-unconfigured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var settingsPath = Path.Combine(probe, "settings.json");
            File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
            var dataDir = Path.Combine(probe, "data");
            Directory.CreateDirectory(dataDir);
            return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

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

    private static async Task<bool> PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        return condition();
    }

    private sealed class EmptyStore : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", workspacePath, title, At));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(1L);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            Empty();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Task.FromResult(0L);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        private static async IAsyncEnumerable<StoredMessage> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class EmptyCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, At));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyTodoStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            Task.FromResult(new TodoItem("t1", sessionId, title, TodoStatus.Pending, note, 0, At));

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }
}
