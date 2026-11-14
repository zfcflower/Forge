using System.Reflection;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the session's live tool registry (wave W8, <c>SessionWorkspace.Tools.cs</c>): a start event of the running turn
/// creates the one entry the transcript row and the resource inspector share, a streamed snapshot only becomes visible
/// when the session ticks, a snapshot that overtakes its start is parked instead of dropped, a finished call leaves the
/// registry but keeps its transcript row, and the turn's end (and <see cref="IDisposable"/> on the session) clears
/// whatever never reported a finish - including a start event that is still queued after that end.
/// <para>Every tool event is injected while a real turn is running (a scripted provider the test holds open): the session
/// only tracks a live call for a running turn, which is the invariant these tests exist for. The sink is driven directly,
/// like <see cref="SessionEventRoutingTests"/>: it is the worker-thread entry point the runtime calls, and only handing it
/// the events in the overtaking order makes that path deterministic.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionLiveToolsTests
{
    private const string SessionId = "main-session";
    private const string ChildAgentId = "child-agent";
    private const string ChildSessionId = "child-session";
    private const string ChildKind = "Explore";
    private const string ChildTask = "look at the diff";
    private const string Snapshot = "build started\ncompiling 12 files\nok";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worker-thread event sink of the session; every runtime event goes through it.</summary>
    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the live-tools test cannot drive the sink.");

    // ---------------------------------------------------------------- starting, ticking, finishing

    [Fact]
    public async Task A_started_call_is_listed_once_and_its_transcript_row_shares_the_entry()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var turn = LiveTurn.Start();
                try
                {
                    Emit(turn.Workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => turn.Workspace.RunningToolCount == 1), "the started call was never tracked");

                    var live = Assert.Single(turn.Workspace.RunningTools);
                    var row = Assert.Single(Rows(turn.Workspace));
                    return (
                        Count: turn.Workspace.RunningToolCount,
                        Live: live,
                        RowLive: row.Live,
                        HasLive: row.Live is not null,
                        Resources: turn.Workspace.Instruments.RunningResources,
                        Menu: turn.Workspace.Instruments.OpenInspectorMenuText,
                        ExpectedMenu: ResourceInspectorStrings.MenuCount(1));
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(1, state.Count);
        Assert.True(state.HasLive);
        Assert.Same(state.Live, state.RowLive);
        Assert.Equal(1, state.Resources);
        Assert.Equal("运行中的资源(1)…", state.Menu);
        Assert.Equal(state.ExpectedMenu, state.Menu);
    }

    [Fact]
    public async Task A_snapshot_from_the_output_thread_reaches_the_row_on_the_next_session_tick()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var row = Assert.Single(Rows(workspace));

                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), Snapshot)));

                    // The producer thread does not touch the row; only the session's tick publishes the snapshot.
                    var before = (HasOutput: row.HasLiveOutput, Tail: row.LiveOutputText);
                    workspace.Tick();
                    return (before, After: (HasOutput: row.HasLiveOutput, Tail: row.LiveOutputText), workspace.Instruments.RunningResources);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.False(state.before.HasOutput);
        Assert.Equal(string.Empty, state.before.Tail);
        Assert.True(state.After.HasOutput, "the tick never published the snapshot");
        Assert.Equal(Snapshot, state.After.Tail);
        Assert.Equal(1, state.RunningResources);
    }

    [Fact]
    public async Task A_snapshot_that_overtakes_its_start_event_is_kept()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;

                    // The two events come from different threads; the snapshot can be handed to the sink first.
                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), Snapshot)));
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");

                    var row = Assert.Single(Rows(workspace));
                    workspace.Tick();
                    return (HasOutput: row.HasLiveOutput, Tail: row.LiveOutputText);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.True(state.HasOutput, "the parked snapshot was dropped");
        Assert.Equal(Snapshot, state.Tail);
    }

    [Fact]
    public async Task A_snapshot_for_a_finished_call_never_brings_it_back()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    Emit(workspace, new AgentToolFinished("root", Call("call-1"), new ToolResult("done")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 0), "the finished call stayed in the registry");

                    // The snapshot path is synchronous inside the emitter, so nothing is left queued to wait for: the late
                    // snapshot simply has no entry to land in and must not create one.
                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), Snapshot)));

                    var row = Assert.Single(Rows(workspace));
                    return (workspace.RunningToolCount, workspace.Instruments.RunningResources, row.HasLiveOutput, row.LiveOutputText);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(0, state.RunningToolCount);
        Assert.Equal(0, state.RunningResources);
        Assert.False(state.HasLiveOutput);
        Assert.Equal(string.Empty, state.LiveOutputText);
    }

    [Fact]
    public async Task A_childs_call_is_labelled_with_its_kind_and_counted_with_the_live_child()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, Started());
                    Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child was never opened");
                    Emit(workspace, new AgentSubAgentRunning(ChildAgentId, At));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningSubAgentCount == 1), "the child never acquired a run slot");

                    Emit(workspace, new AgentToolStarted(ChildAgentId, Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the child's call was never tracked");

                    var live = Assert.Single(workspace.RunningTools);
                    var row = Assert.Single(ChildRows(workspace));
                    return (live.AgentLabel, SameRow: ReferenceEquals(live, row.Live), workspace.Instruments.RunningResources);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(ChildKind, state.AgentLabel);
        Assert.True(state.SameRow);
        Assert.Equal(2, state.RunningResources);
    }

    // ---------------------------------------------------------------- independence and finishing

    [Fact]
    public async Task Two_running_calls_keep_their_own_tails()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Emit(workspace, new AgentToolStarted("root", Call("call-2")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 2), "the two calls were never tracked");

                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), "tail of one")));
                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-2"), "tail of two")));
                    workspace.Tick();

                    var first = Assert.Single(workspace.RunningTools, tool => tool.Name == "bash-one");
                    var second = Assert.Single(workspace.RunningTools, tool => tool.Name == "bash-two");
                    return (First: first.TailText, Second: second.TailText, workspace.RunningToolCount);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal("tail of one", state.First);
        Assert.Equal("tail of two", state.Second);
        Assert.Equal(2, state.RunningToolCount);
    }

    [Fact]
    public async Task A_finished_call_leaves_the_registry_and_its_row_keeps_the_result()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var live = Assert.Single(workspace.RunningTools);
                    var row = Assert.Single(Rows(workspace));

                    Emit(workspace, new AgentToolFinished("root", Call("call-1"), new ToolResult("all good")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 0), "the finished call stayed in the registry");

                    return (
                        workspace.RunningToolCount,
                        workspace.Instruments.RunningResources,
                        live.IsRunning,
                        row.HasLiveOutput,
                        row.HasResult,
                        row.Result,
                        row.LiveElapsedText);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(0, state.RunningToolCount);
        Assert.Equal(0, state.RunningResources);
        Assert.False(state.IsRunning);
        Assert.False(state.HasLiveOutput);
        Assert.True(state.HasResult);
        Assert.Equal("all good", state.Result);
        Assert.Equal(string.Empty, state.LiveElapsedText);
    }

    /// <summary>
    /// A cancelled call never emits its own finish, so the turn's end is what has to clear the registry. The turn is real
    /// (a scripted provider gated open from the test) because <see cref="SessionWorkspaceViewModel.IsRunning"/> only turns
    /// false on the turn's own completion. A start event still queued behind that end must not resurrect a row either.
    /// </summary>
    [Fact]
    public async Task The_end_of_a_turn_clears_a_call_that_never_reported_its_finish()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the call was never tracked");
                    var live = Assert.Single(workspace.RunningTools);
                    var row = Assert.Single(Rows(workspace));

                    await turn.EndAsync();
                    var ended = (
                        workspace.RunningToolCount,
                        workspace.Instruments.RunningResources,
                        live.IsRunning,
                        row.HasLiveOutput,
                        row.HasResult);

                    // A cancelled turn's start event can be flushed after its own end; the card still shows the row, but no
                    // live entry may come back for a call that will never report a finish.
                    Emit(workspace, new AgentToolStarted("root", Call("call-late")));
                    Assert.True(await PumpUntilAsync(() => Rows(workspace).Any(candidate => candidate.Name == "bash-late")), "the late row never reached the transcript");
                    var late = Assert.Single(Rows(workspace), candidate => candidate.Name == "bash-late");
                    return (ended, LateLive: late.Live, workspace.RunningToolCount, workspace.Instruments.RunningResources);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(0, state.ended.RunningToolCount);
        Assert.Equal(0, state.ended.RunningResources);
        Assert.False(state.ended.IsRunning);
        Assert.False(state.ended.HasLiveOutput);
        // The card closes its unfinished row with the "no tool result" placeholder; the row survives the live entry.
        Assert.True(state.ended.HasResult);

        Assert.Null(state.LateLive);
        Assert.Equal(0, state.RunningToolCount);
        Assert.Equal(0, state.RunningResources);
    }

    // ---------------------------------------------------------------- teardown and the live line limit

    [Fact]
    public async Task Dispose_clears_the_registry_and_the_sink_still_accepts_late_events()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var live = Assert.Single(workspace.RunningTools);

                    workspace.Dispose();
                    var after = (workspace.RunningTools.Count, workspace.RunningToolCount, live.IsRunning, workspace.Instruments.RunningResources);

                    // A runtime that is still winding down can hand the sink a snapshot or a finish after the tab closed.
                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), "late")));
                    Emit(workspace, new AgentToolFinished("root", Call("call-1"), new ToolResult("done")));

                    return (after, workspace.RunningToolCount);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(0, state.after.Count);
        Assert.Equal(0, state.after.RunningToolCount);
        Assert.False(state.after.IsRunning);
        Assert.Equal(0, state.after.RunningResources);
        Assert.Equal(0, state.RunningToolCount);
    }

    [Fact]
    public async Task The_session_tick_cuts_the_tail_at_the_current_preference()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                // The preference clamps to 20..1000, so 20 is the shortest tail a user can ask for; the test then widens it
                // and proves the next tick re-reads the live value rather than the one the entry was created with.
                var preferences = new AppPreferences { InspectorLogTailLines = AppPreferences.MinInspectorLogTailLines };
                var turn = LiveTurn.Start(preferences);
                try
                {
                    var workspace = turn.Workspace;
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var row = Assert.Single(Rows(workspace));

                    var snapshot = string.Join("\n", Enumerable.Range(1, 25).Select(static line => "line " + line));
                    await Task.Run(() => Emit(workspace, new AgentToolProgress("root", Call("call-1"), snapshot)));
                    workspace.Tick();
                    var narrow = row.LiveOutputText;

                    preferences.InspectorLogTailLines = AppPreferences.MaxInspectorLogTailLines;
                    workspace.Tick();
                    return (Narrow: narrow, Wide: row.LiveOutputText, Snapshot: snapshot);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.Equal(string.Join("\n", Enumerable.Range(6, 20).Select(static line => "line " + line)), state.Narrow);
        Assert.Equal(state.Snapshot, state.Wide);
    }

    // ---------------------------------------------------------------- the inspector's view of the session

    [Fact]
    public async Task A_rows_stop_never_cancels_the_turn_and_a_call_core_does_not_run_is_not_marked_stopping()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    // An injected start: Core never registered this call, so its single-call cancel has nothing to stop. The
                    // old stop cancelled the whole turn here; the new one asks Core for exactly this call and nothing else.
                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var live = Assert.Single(workspace.RunningTools);
                    var canStop = live.CanStop;

                    live.StopCommand.Execute(null);
                    await TestPump.UntilAsync(static () => false, iterations: 20);
                    var after = (workspace.IsRunning, live.IsStopRequested, live.CanStop, workspace.RunningToolCount);
                    var result = await turn.EndAsync();
                    return (CanStop: canStop, after, Result: result);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.True(state.CanStop);
        Assert.True(state.after.IsRunning, "a row's stop cancelled the whole turn");
        Assert.False(state.after.IsStopRequested);
        Assert.True(state.after.CanStop);
        Assert.Equal(1, state.after.RunningToolCount);
        Assert.NotNull(state.Result);
    }

    [Fact]
    public async Task The_session_tells_the_inspector_when_its_rows_change_and_once_more_when_it_closes()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                var turn = LiveTurn.Start();
                try
                {
                    var workspace = turn.Workspace;
                    IResourceInspectorSource source = workspace;
                    var raised = 0;
                    source.RunningResourcesChanged += (_, _) => raised++;

                    Emit(workspace, new AgentToolStarted("root", Call("call-1")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the started call was never tracked");
                    var afterStart = raised;
                    var rows = new List<object>();
                    source.CollectRunning(rows);
                    var listed = rows.Count;

                    Emit(workspace, new AgentToolFinished("root", Call("call-1"), new ToolResult("done")));
                    Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 0), "the finished call was never removed");
                    var afterFinish = raised;

                    workspace.Dispose();
                    var afterDispose = raised;
                    rows.Clear();
                    source.CollectRunning(rows);

                    // The session forgot its listeners on close: a late event reaches nobody.
                    Emit(workspace, new AgentToolStarted("root", Call("call-2")));
                    return (afterStart, listed, afterFinish, afterDispose, AfterClose: rows.Count, Final: raised, source.Title);
                }
                finally
                {
                    await turn.CloseAsync();
                }
            },
            CancellationToken.None);

        Assert.True(state.afterStart >= 1);
        Assert.Equal(1, state.listed);
        Assert.True(state.afterFinish > state.afterStart);
        Assert.Equal(state.afterFinish + 1, state.afterDispose);
        Assert.Equal(0, state.AfterClose);
        Assert.Equal(state.afterDispose, state.Final);
        Assert.Equal("demo session", state.Title);
    }

    // ---------------------------------------------------------------- helpers

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static AgentSubAgentStarted Started() =>
        new("root", ChildAgentId, ChildKind, ChildTask, "model", ChildSessionId, At);

    private static ToolCall Call(string id) => new(id, id switch
    {
        "call-2" => "bash-two",
        "call-late" => "bash-late",
        _ => "bash-one",
    }, """{"command":"npm test"}""");

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition, iterations: 1200);

    /// <summary>The tool rows of the root transcript, in card order.</summary>
    private static List<ToolRowViewModel> Rows(SessionWorkspaceViewModel workspace) =>
        workspace.Transcript.Items.OfType<TurnCardViewModel>()
            .SelectMany(static card => card.Blocks)
            .OfType<ToolRowViewModel>()
            .ToList();

    /// <summary>The tool rows of the one live child's transcript.</summary>
    private static List<ToolRowViewModel> ChildRows(SessionWorkspaceViewModel workspace) =>
        workspace.SubAgents.SelectMany(static agent => agent.Transcript.Items.OfType<TurnCardViewModel>())
            .SelectMany(static card => card.Blocks)
            .OfType<ToolRowViewModel>()
            .ToList();

    /// <summary>
    /// One real automation turn, held open by a scripted model until the test releases it. The session only tracks a live
    /// tool call while its turn runs, so every tool event in this suite is injected into a running one.
    /// </summary>
    private sealed class LiveTurn
    {
        private readonly GatedProvider provider;
        private readonly Task<MailboxResult> turn;

        private LiveTurn(SessionWorkspaceViewModel workspace, GatedProvider provider, Task<MailboxResult> turn)
        {
            Workspace = workspace;
            this.provider = provider;
            this.turn = turn;
        }

        public SessionWorkspaceViewModel Workspace { get; }

        public static LiveTurn Start(AppPreferences? preferences = null)
        {
            var info = new SessionInfo(SessionId, Path.GetTempPath(), "demo session", At);
            var store = new RecordingSessionStore(info);
            var provider = new GatedProvider();
            var workspace = new SessionWorkspaceViewModel(
                info,
                new SessionRuntime(
                    new AgentRuntimeServices(
                        _ => provider,
                        HostClient: null,
                        store,
                        new EmptySessionCatalog(At),
                        new EmptyTodoStore("t1", At),
                        new ModelSettings("model")),
                    info),
                checkpoints: null,
                store,
                TestProviderServices.UnconfiguredProbe(),
                preferences: preferences ?? new AppPreferences());
            var turn = workspace.RunAutomationTurnAsync("run the build", LocText.Literal("automation"), agent: null);
            Assert.True(workspace.IsRunning, "the turn never started");
            return new LiveTurn(workspace, provider, turn);
        }

        /// <summary>Lets the model answer and waits for the turn's own end (the path that clears the registry).</summary>
        public async Task<MailboxResult> EndAsync()
        {
            provider.Release();
            Assert.True(await TestPump.UntilAsync(() => !Workspace.IsRunning, iterations: 1200), "the turn never ended");
            return await turn;
        }

        /// <summary>Ends the turn if the test did not, then closes the session; safe to call after a test-ended turn.</summary>
        public async Task CloseAsync()
        {
            provider.Release();
            await turn;
            Workspace.Dispose();
        }
    }

    /// <summary>A model that holds its one answer until the test releases it, so a turn can be observed while it runs.</summary>
    private sealed class GatedProvider : IChatProvider
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "gated";

        public void Release() => released.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await released.Task.WaitAsync(ct).ConfigureAwait(false);
            await Task.Yield();
            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }

    /// <summary>A message log in memory, so the real turn host has a session to resume and somewhere to append its turn.</summary>
    private sealed class RecordingSessionStore(SessionInfo session) : EmptySessionStore(DateTimeOffset.UnixEpoch)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, List<StoredMessage>> log = new(StringComparer.Ordinal);

        public override Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(session);

        public override Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(string.Equals(sessionId, session.Id, StringComparison.Ordinal) ? session : null);

        public override Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([session]);

        public override Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
        {
            lock (gate)
            {
                if (!log.TryGetValue(sessionId, out var messages))
                {
                    messages = [];
                    log[sessionId] = messages;
                }

                messages.Add(new StoredMessage(messages.Count + 1, message, usage, DateTimeOffset.UnixEpoch));
                return Task.FromResult((long)messages.Count);
            }
        }

        public override IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            ReadCoreAsync(sessionId, fromSeq, toSeq);

        public override Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult(log.TryGetValue(sessionId, out var messages) ? (long)messages.Count : 0L);
            }
        }

        private async IAsyncEnumerable<StoredMessage> ReadCoreAsync(string sessionId, long fromSeq, long? toSeq)
        {
            List<StoredMessage> snapshot;
            lock (gate)
            {
                snapshot = log.TryGetValue(sessionId, out var messages) ? [.. messages] : [];
            }

            foreach (var message in snapshot)
            {
                if (message.Seq >= fromSeq && (toSeq is null || message.Seq <= toSeq))
                {
                    yield return message;
                }
            }

            await Task.Yield();
        }
    }
}