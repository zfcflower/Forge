using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins session wiring of sub-agents: catalog fields, touch failures, resume and host isolation.</summary>
public sealed class SubAgentSessionPinTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private static AgentOptions Options(int maxParallel = 4, int maxDepth = 3, int maxTurns = 10) =>
        new() { MaxParallelSubAgents = maxParallel, MaxSubAgentDepth = maxDepth, MaxTurns = maxTurns };

    private sealed class FakeCatalogStore : ISessionStore, ISessionCatalog
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, SessionInfo> sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<StoredMessage>> messages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SummaryRecord>> summaries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> sequences = new(StringComparer.Ordinal);

        public List<SessionCreate> Created { get; } = new();
        public int TouchCalls;
        public bool FailTouch;

        public void SeedParent(SessionInfo parent)
        {
            lock (gate)
            {
                sessions[parent.Id] = parent;
            }
        }

        public IReadOnlyList<SessionCreate> CreatedRequests
        {
            get
            {
                lock (gate)
                {
                    return Created.ToArray();
                }
            }
        }

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct)
        {
            var session = new SessionInfo(Guid.NewGuid().ToString("n"), workspacePath, title, DateTimeOffset.UtcNow);
            lock (gate)
            {
                sessions[session.Id] = session;
            }

            return Task.FromResult(session);
        }

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult(sessions.GetValueOrDefault(sessionId));
            }
        }

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<SessionInfo>>(
                    sessions.Values.Where(s => workspacePath is null || s.WorkspacePath == workspacePath).ToArray());
            }
        }

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
        {
            lock (gate)
            {
                var seq = sequences.GetValueOrDefault(sessionId) + 1;
                sequences[sessionId] = seq;
                if (!messages.TryGetValue(sessionId, out var list))
                {
                    list = [];
                    messages[sessionId] = list;
                }

                list.Add(new StoredMessage(seq, message, usage, DateTimeOffset.UtcNow));
                return Task.FromResult(seq);
            }
        }

        public async IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            StoredMessage[] snapshot;
            lock (gate)
            {
                snapshot = messages.TryGetValue(sessionId, out var list)
                    ? list.Where(m => m.Seq >= fromSeq && m.Seq <= (toSeq ?? long.MaxValue)).ToArray()
                    : [];
            }

            foreach (var message in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return message;
            }
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult(sequences.GetValueOrDefault(sessionId));
            }
        }

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct)
        {
            lock (gate)
            {
                if (!summaries.TryGetValue(summary.SessionId, out var list))
                {
                    list = [];
                    summaries[summary.SessionId] = list;
                }

                list.Add(summary);
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<SummaryRecord>>(
                    summaries.TryGetValue(sessionId, out var list) ? list.ToArray() : []);
            }
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
        {
            lock (gate)
            {
                Created.Add(request);
                var session = new SessionInfo(Guid.NewGuid().ToString("n"), request.WorkspacePath, request.Title, DateTimeOffset.UtcNow)
                {
                    ProjectId = request.ProjectId,
                    WorktreePath = request.WorktreePath,
                    ParentSessionId = request.ParentSessionId,
                    AgentKind = request.AgentKind,
                    Task = request.Task,
                };
                sessions[session.Id] = session;
                return Task.FromResult(session);
            }
        }

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<SessionInfo>>(
                    sessions.Values
                        .Where(s =>
                            (query.ProjectId is null || s.ProjectId == query.ProjectId) &&
                            (query.WorktreePath is null || s.WorktreePath == query.WorktreePath) &&
                            (!query.Status.HasValue || s.Status == query.Status) &&
                            (query.ParentSessionId is null || s.ParentSessionId == query.ParentSessionId))
                        .OrderByDescending(s => s.LastActiveAt)
                        .ToArray());
            }
        }

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct)
        {
            lock (gate)
            {
                if (!sessions.TryGetValue(sessionId, out var session))
                {
                    return Task.FromResult(false);
                }

                sessions[sessionId] = session with { Status = status };
                return Task.FromResult(true);
            }
        }

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct)
        {
            lock (gate)
            {
                if (!sessions.TryGetValue(sessionId, out var session))
                {
                    return Task.FromResult(false);
                }

                sessions[sessionId] = session with { Title = title };
                return Task.FromResult(true);
            }
        }

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct)
        {
            lock (gate)
            {
                TouchCalls++;
                if (FailTouch)
                {
                    throw new InvalidOperationException("touch failed");
                }

                if (sessions.TryGetValue(sessionId, out var session))
                {
                    sessions[sessionId] = session with { LastActiveAt = at };
                }

                return Task.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Child_session_carries_parent_link_kind_task_and_inherited_paths()
    {
        var store = new FakeCatalogStore();
        var parentSeed = new SessionInfo("parent-1", Workspace, "parent", DateTimeOffset.UtcNow)
        {
            ProjectId = "proj-1",
            WorktreePath = "/repo/wt-1",
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        store.SeedParent(parentSeed);
        var provider = new FakeProvider((request, index) =>
            Turn.LastUserText(request) == "do research"
                ? Turn.Say("child report")
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", """{"kind":"Scout","task":"do research"}"""))
                    : Turn.Say("parent done"));
        var host = AgentTestHost.Create(
            provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout], store: store);
        var root = host.CreateRoot(parentSeed.Id, Workspace);

        var events = await host.RunToCompletionAsync(root);
        var created = Assert.Single(store.CreatedRequests);
        Assert.Equal(parentSeed.Id, created.ParentSessionId);
        Assert.Equal("Scout", created.AgentKind);
        Assert.Equal("do research", created.Task);
        Assert.Equal("proj-1", created.ProjectId);
        Assert.Equal("/repo/wt-1", created.WorktreePath);
        var child = Assert.Single(host.SubAgents.Instances);
        Assert.Equal(parentSeed.Id, (await store.GetSessionAsync(child.SessionId, CancellationToken.None))?.ParentSessionId);
    }

    [Fact]
    public async Task Touch_failure_does_not_break_the_loop()
    {
        var store = new FakeCatalogStore { FailTouch = true };
        var provider = new FakeProvider((request, index) => Turn.Say("done"));
        var host = AgentTestHost.Create(
            provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout], store: store);
        var root = host.CreateRoot("sess-touch-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await host.RunToCompletionAsync(root, "hello");
        Assert.Contains(events.OfType<AgentTurnCompleted>(), t => t.AgentId == "root");
        Assert.True(store.TouchCalls > 0);
    }

    [Fact]
    public async Task ResumeRootAsync_round_trips_prior_context()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "resume me", CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("first answer"), new TokenUsage(3, 4), CancellationToken.None);

        var host = AgentTestHost.Create(
            new FakeProvider((request, index) => Turn.Say("continued")),
            [FakeTool.Echo("read")],
            options: Options(),
            agents: [Scout],
            store: store);
        var resumed = await host.ResumeRootAsync(session.Id, Workspace, CancellationToken.None);

        Assert.Equal(2, resumed.History.Count);
        Assert.Equal("first question", Turn.Text(resumed.History[0]));
        Assert.Equal("first answer", Turn.Text(resumed.History[1]));

        var events = await host.RunToCompletionAsync(resumed, "second question");
        Assert.True(resumed.History.Count > 3);
        Assert.Contains(resumed.History, m => Turn.Text(m).Contains("first question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeRootAsync_trims_dangling_tool_call_turn_and_orphan_leading_tool_result()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "trim me", CancellationToken.None);
        // Orphan leading tool result (no assistant turn produced it).
        await store.AppendAsync(
            session.Id,
            new ChatMessage(MessageRole.Tool, [new ToolResultBlock("orphan", "stale", IsError: false)]),
            null,
            CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("hi"), null, CancellationToken.None);
        // Dangling assistant tool-call turn with no persisted results (crash mid-turn).
        await store.AppendAsync(
            session.Id,
            new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("dangling", "read", "{}")]),
            null,
            CancellationToken.None);

        var host = AgentTestHost.Create(
            new FakeProvider((request, index) => Turn.Say("done")),
            [FakeTool.Echo("read")],
            options: Options(),
            agents: [Scout],
            store: store);
        var resumed = await host.ResumeRootAsync(session.Id, Workspace, CancellationToken.None);

        var single = Assert.Single(resumed.History);
        Assert.Equal(MessageRole.User, single.Role);
        Assert.Equal("hi", Turn.Text(single));
    }

    [Fact]
    public async Task ResumeRootAsync_uses_summaries_like_the_compactor()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "summarized", CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("old question"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("old answer"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("recent question"), null, CancellationToken.None);
        await store.PutSummaryAsync(
            new SummaryRecord(session.Id, 1, 1, 2, "the user asked about X", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var host = AgentTestHost.Create(
            new FakeProvider((request, index) => Turn.Say("done")),
            [FakeTool.Echo("read")],
            options: Options(),
            agents: [Scout],
            store: store);
        var resumed = await host.ResumeRootAsync(session.Id, Workspace, CancellationToken.None);

        Assert.True(resumed.History.Count >= 2);
        Assert.Equal(MessageRole.User, resumed.History[0].Role);
        Assert.Contains("the user asked about X", Turn.Text(resumed.History[0]), StringComparison.Ordinal);
        Assert.Contains(resumed.History, m => Turn.Text(m).Contains("recent question", StringComparison.Ordinal));
        Assert.DoesNotContain(resumed.History, m => Turn.Text(m).Contains("old question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_hosts_running_concurrently_have_independent_ids_and_no_crosstalk()
    {
        AgentHost MakeHost(string tag)
        {
            var provider = new FakeProvider((request, index) =>
                Turn.LastUserText(request) == "child-" + tag
                    ? Turn.Say("report-" + tag)
                    : index == 0
                        ? Turn.Call(("call-1", "spawn_agent", $$"""{"kind":"Scout","task":"child-{{tag}}"}"""))
                        : Turn.Say("parent-" + tag));
            return AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        }

        var hostA = MakeHost("a");
        var hostB = MakeHost("b");
        var rootA = hostA.CreateRoot("session-a-" + Guid.NewGuid().ToString("n"), Workspace);
        var rootB = hostB.CreateRoot("session-b-" + Guid.NewGuid().ToString("n"), Workspace);

        var taskA = Task.Run(() => AgentTestHost.CollectAsync(hostA.RunAsync(rootA, "go")));
        var taskB = Task.Run(() => AgentTestHost.CollectAsync(hostB.RunAsync(rootB, "go")));
        var eventsA = await taskA.WaitAsync(TimeSpan.FromSeconds(15));
        var eventsB = await taskB.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(eventsA.OfType<AgentFailed>());
        Assert.Empty(eventsB.OfType<AgentFailed>());
        var childA = Assert.Single(hostA.SubAgents.Instances);
        var childB = Assert.Single(hostB.SubAgents.Instances);
        Assert.Equal("Scout-1", childA.Id);
        Assert.Equal("Scout-1", childB.Id);
        Assert.NotEqual(childA.SessionId, childB.SessionId);
        Assert.Contains(childA.History, m => Turn.Text(m).Contains("report-a", StringComparison.Ordinal));
        Assert.Contains(childB.History, m => Turn.Text(m).Contains("report-b", StringComparison.Ordinal));
        Assert.DoesNotContain(childA.History, m => Turn.Text(m).Contains("report-b", StringComparison.Ordinal));
        Assert.DoesNotContain(childB.History, m => Turn.Text(m).Contains("report-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stress_random_steer_stop_followUp_interleavings_finish_every_child_exactly_once()
    {
        const int Children = 8;
        var random = new Random(42);
        var ops = new List<(string Kind, string Arg)>();
        for (var i = 0; i < Children * 3; i++)
        {
            string[] kinds = ["steer", "followup", "stop"];
            ops.Add((kinds[random.Next(3)], "op-" + i));
        }

        var provider = new FakeProvider(async (request, index, ct) =>
        {
            var last = Turn.LastUserText(request);
            if (last.StartsWith("stress-", StringComparison.Ordinal))
            {
                await Task.Delay(50 + random.Next(100), ct);
                return Turn.Say("done " + last);
            }

            await Task.Delay(10, ct);
            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 3, maxTurns: 20), agents: [Scout]);
        var sink = new StressSink();
        var context = new ToolContext("sess-stress-" + Guid.NewGuid().ToString("n"), Workspace);

        var tasks = Enumerable.Range(0, Children)
            .Select(i => host.SubAgents.SpawnAsync("Scout", "stress-" + i, null, context, sink, "root", CancellationToken.None))
            .ToArray();
        // Let every run admit before interfering.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (sink.RunningCount < Children && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        var ids = host.SubAgents.Instances.Select(i => i.Id).ToArray();
        Assert.Equal(Children, ids.Length);
        var background = Task.Run(() =>
        {
            foreach (var (kind, arg) in ops)
            {
                var id = ids[random.Next(ids.Length)];
                switch (kind)
                {
                    case "steer": host.SubAgentControl.Steer(id, arg); break;
                    case "followup": host.SubAgentControl.FollowUp(id, arg); break;
                    case "stop": host.SubAgentControl.Stop(id, arg); break;
                }
            }
        });

        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40));
        await background.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Children, outcomes.Length);
        var finished = sink.Finished;
        Assert.Equal(Children, finished.Count);
        Assert.Equal(Children, finished.Select(f => f.AgentId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(finished, f => Assert.True(
            f.Status is SubAgentStatus.Completed or SubAgentStatus.Stopped,
            $"Unexpected status {f.Status} for {f.AgentId}."));
    }

    private sealed class StressSink : IAgentEventSink
    {
        private readonly ConcurrentQueue<AgentEvent> events = new();
        public void Emit(AgentEvent value) => events.Enqueue(value);
        public int RunningCount => events.OfType<AgentSubAgentRunning>().Count();
        public IReadOnlyList<AgentSubAgentFinished> Finished => events.OfType<AgentSubAgentFinished>().ToArray();
    }
}
