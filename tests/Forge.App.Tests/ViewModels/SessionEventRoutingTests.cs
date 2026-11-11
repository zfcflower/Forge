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
/// Pins the event routing of <see cref="SessionWorkspaceViewModel"/> (forge-app spec "子 Agent 区": a child's text never
/// reaches the parent's bubble): text is routed by <see cref="AgentEvent.AgentId"/>, and text of a child whose start event
/// is still queued for the dispatcher is parked instead of dropped, because a child's first deltas can overtake
/// <c>AgentSubAgentStarted</c>.
///
/// The sink is driven directly: it is the worker-thread entry point the runtime calls, and the only way to produce the
/// overtaking order deterministically is to hand it the two events in that order.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionEventRoutingTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string SessionId = "main-session";
    private const string ChildAgentId = "child-agent";
    private const string ChildSessionId = "child-session";
    private const string ChildKind = "Explore";
    private const string ChildTask = "look at the diff";
    private const string EarlyText = "first words of the child";
    private const string RootText = "words of the main agent";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worker-thread event sink of the session; every runtime event goes through it.</summary>
    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the routing test cannot drive the sink.");

    [Fact]
    public async Task Text_that_arrives_before_its_start_event_is_not_lost()
    {
        var session = SharedUi.Session;
        var texts = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                await Task.Run(() => Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText)));
                Emit(workspace, Started());
                Assert.True(
                    await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal))),
                    "the parked text never reached the child's transcript");
                return ChildTexts(workspace);
            },
            CancellationToken.None);

        Assert.Contains(EarlyText, texts);
    }

    /// <summary>The ordering is not guaranteed in production, so both orders must keep the text.</summary>
    [Fact]
    public async Task Text_that_races_its_start_event_is_not_lost()
    {
        var session = SharedUi.Session;
        var texts = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                var delta = Task.Run(() => Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText)));
                Emit(workspace, Started());
                await delta;
                Assert.True(
                    await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal))),
                    "the parked text never reached the child's transcript");
                return ChildTexts(workspace);
            },
            CancellationToken.None);

        Assert.Contains(EarlyText, texts);
    }

    [Fact]
    public async Task A_child_agents_text_never_reaches_the_main_transcript()
    {
        var session = SharedUi.Session;
        var main = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                Emit(workspace, Started());
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child was never opened");
                Emit(workspace, new AgentTextDelta(ChildAgentId, EarlyText));
                await PumpUntilAsync(() => ChildTexts(workspace).Any(text => text.Contains(EarlyText, StringComparison.Ordinal)));
                return Texts(workspace.Transcript);
            },
            CancellationToken.None);

        Assert.DoesNotContain(EarlyText, main);
    }

    [Fact]
    public async Task Text_of_the_main_agent_reaches_the_main_transcript()
    {
        var session = SharedUi.Session;
        var main = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                await Task.Run(() => Emit(workspace, new AgentTextDelta("root", RootText)));
                Assert.True(
                    await PumpUntilAsync(() => Texts(workspace.Transcript).Any(text => text.Contains(RootText, StringComparison.Ordinal))),
                    "the main agent's text never reached the main transcript");
                return Texts(workspace.Transcript);
            },
            CancellationToken.None);

        Assert.Contains(RootText, main);
    }

    /// <summary>
    /// The strip badge answers "how many sub-agents are working": one that is still waiting for a run slot is live but not
    /// running, a finished one has left the live list, and neither may be counted.
    /// </summary>
    [Fact]
    public async Task The_strip_badge_counts_only_sub_agents_that_hold_a_run_slot()
    {
        const string SecondAgentId = "child-agent-two";
        const string SecondSessionId = "child-session-two";
        var session = SharedUi.Session;
        var reading = await session.Dispatch(
            async () =>
            {
                var workspace = CreateWorkspace();
                var notifications = 0;
                workspace.PropertyChanged += (_, e) =>
                {
                    if (string.Equals(e.PropertyName, nameof(SessionWorkspaceViewModel.RunningSubAgentCount), StringComparison.Ordinal))
                    {
                        notifications++;
                    }
                };

                var readings = new List<(string Step, int Running, bool Badge)>
                {
                    ("nothing started", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents),
                };

                Emit(workspace, Started());
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child was never opened");
                readings.Add(("started", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                // A second child joins while the first is still queued: two live children, none of them running.
                Emit(workspace, Started(SecondAgentId, SecondSessionId));
                Emit(workspace, new AgentSubAgentQueued(ChildAgentId, At));
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 2), "the second child was never opened");
                readings.Add(("queued", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                Emit(workspace, new AgentSubAgentRunning(ChildAgentId, At));
                Assert.True(
                    await PumpUntilAsync(() => workspace.RunningSubAgentCount == 1),
                    "the child that acquired a slot never reached the badge");
                readings.Add(("one running", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));
                var name = workspace.SubAgentToggleName;

                Emit(workspace, Finished(ChildAgentId));
                Assert.True(
                    await PumpUntilAsync(() => workspace.RunningSubAgentCount == 0),
                    "a finished child kept its slot on the badge");
                readings.Add(("finished", workspace.RunningSubAgentCount, workspace.HasRunningSubAgents));

                return (readings, notifications, name, label: workspace.SubAgentLabel);
            },
            CancellationToken.None);

        Assert.Equal([0, 0, 0, 1, 0], reading.readings.Select(static entry => entry.Running));
        Assert.Equal([false, false, false, true, false], reading.readings.Select(static entry => entry.Badge));
        Assert.True(reading.notifications > 0, "the badge was never told to re-read the count");
        // The label is the noun only, so the count can never appear twice; the button's accessible name carries it instead.
        Assert.False(reading.label.Any(char.IsAsciiDigit), "the chip label still spells out a count: " + reading.label);
        Assert.Contains("1", reading.name);
    }

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static AgentSubAgentStarted Started() =>
        new("root", ChildAgentId, ChildKind, ChildTask, "model", ChildSessionId, At);

    private static AgentSubAgentStarted Started(string childAgentId, string childSessionId) =>
        new("root", childAgentId, ChildKind, ChildTask, "model", childSessionId, At);

    private static AgentSubAgentFinished Finished(string childAgentId) =>
        new(childAgentId, SubAgentStatus.Completed, new TokenUsage(0, 0), TimeSpan.Zero, null, At);

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

    /// <summary>Every text of the one live sub-agent; empty until its start event reached the dispatcher.</summary>
    private static List<string> ChildTexts(SessionWorkspaceViewModel workspace)
    {
        var texts = new List<string>();
        foreach (var agent in workspace.SubAgents)
        {
            texts.AddRange(Texts(agent.Transcript));
        }

        return texts;
    }

    private static List<string> Texts(TranscriptViewModel transcript)
    {
        var texts = new List<string>();
        foreach (var item in transcript.Items)
        {
            switch (item)
            {
                case UserMessageViewModel user:
                    texts.Add(user.Text);
                    break;
                case NoticeViewModel notice:
                    texts.Add(notice.Text);
                    break;
                case TurnCardViewModel card:
                    texts.AddRange(card.Blocks.OfType<TurnTextViewModel>().Select(static text => text.Segments
                        .Aggregate(string.Empty, static (all, segment) => all + segment.Text)));
                    break;
            }
        }

        return texts;
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
