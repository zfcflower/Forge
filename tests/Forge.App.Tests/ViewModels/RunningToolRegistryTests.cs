using System.Reflection;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The one race the running-call registry cannot reproduce with a real turn in a test: the turn's end clears the registry on a
/// <c>DispatcherPriority.Normal</c> continuation, while the coalescer flushes a cancelled turn's <c>AgentToolStarted</c> at
/// <c>Background</c>. The end therefore runs first, and a start event that arrives afterwards must not create a row that will
/// never be removed - the call it belongs to was cancelled and will never report a finish, so the entry would show as running
/// until the next turn ended (and its stop button would pause the queue as a side effect).
/// </summary>
[Collection("forge-app-ui")]
public sealed class RunningToolRegistryTests
{
    private const string SessionId = "registry-session";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the registry test cannot drive the sink.");

    /// <summary>The private setter the turn's own <c>finally</c> uses; reflection is the only way in without a real turn.</summary>
    private static readonly PropertyInfo IsRunningProperty = typeof(SessionWorkspaceViewModel).GetProperty(nameof(SessionWorkspaceViewModel.IsRunning))
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.IsRunning is gone.");

    [Fact]
    public async Task A_start_event_that_arrives_after_the_turn_ended_does_not_create_a_running_row()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var workspace = CreateWorkspace();
                var cancelled = new ToolCall("call-cancelled", "bash", "{\"command\":\"dotnet test\"}");

                // The turn was cancelled: no AgentToolFinished will ever arrive for this call.
                SetRunning(workspace, true);
                Emit(workspace, new AgentToolStarted("root", cancelled));
                Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the start event never registered");
                SetRunning(workspace, false);
                var cleared = workspace.RunningToolCount;

                // The start event of a call the cancelled turn already left behind, delivered after the end cleared the table.
                var late = new ToolCall("call-late", "bash", "{}");
                Emit(workspace, new AgentToolStarted("root", late));
                Assert.True(await PumpUntilAsync(() => ToolRows(workspace).Count == 2), "the late start's row never appeared");
                var afterLateStart = workspace.RunningToolCount;
                var lateRow = ToolRows(workspace).LastOrDefault();
                var lateHasLiveState = lateRow?.Live is not null;
                var menu = workspace.Instruments.OpenInspectorMenuText ?? string.Empty;

                // A turn that starts again still registers normally, so the gate is not "never track anything after a cancel".
                SetRunning(workspace, true);
                var live = new ToolCall("call-live", "bash", "{}");
                Emit(workspace, new AgentToolStarted("root", live));
                Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the next turn's call never registered");
                var tracked = workspace.RunningToolCount;
                return (cleared, afterLateStart, lateHasLiveState, menu, tracked, rows: ToolRows(workspace).Count);
            },
            CancellationToken.None);

        Assert.Equal(0, state.cleared);
        Assert.Equal(0, state.afterLateStart);
        Assert.False(state.lateHasLiveState);
        // The late call still gets its transcript row (the model asked for a tool); only its running state is refused.
        Assert.Equal(3, state.rows);
        Assert.Equal(1, state.tracked);
        // Nothing is running, so the strip's row offers no count.
        Assert.DoesNotContain("(", state.menu);
    }

    /// <summary>
    /// A sub-agent also runs outside a turn (the CI chain's Scout, a mailbox request), so its calls must stay tracked while
    /// <c>IsRunning</c> is false. Its own finish is what prunes them: a cancelled child never reports a finish of its calls.
    /// </summary>
    [Fact]
    public async Task Calls_of_a_child_started_outside_a_turn_are_tracked_until_that_child_ends()
    {
        const string childId = "child-outside-turn";
        const string childSessionId = "child-session-outside-turn";
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            async () =>
            {
                using var workspace = CreateWorkspace();
                var call = new ToolCall("call-child", "bash", "{\"command\":\"npm ci\"}");

                Emit(workspace, new AgentSubAgentStarted("root", childId, "Scout", "read the CI log", "model", childSessionId, At));
                Emit(workspace, new AgentSubAgentRunning(childId, At));
                Assert.True(await PumpUntilAsync(() => workspace.SubAgents.Count == 1), "the child never opened");

                // No turn runs (`IsRunning` is false the whole way), which is exactly the CI chain / mailbox path.
                Emit(workspace, new AgentToolStarted(childId, call));
                Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 1), "the child's call was not tracked");
                // The child's call lives in the child's own transcript, not the main one.
                var row = workspace.SubAgents.Single().Transcript.Items
                    .OfType<TurnCardViewModel>()
                    .SelectMany(static card => card.Blocks)
                    .OfType<ToolRowViewModel>()
                    .Single();
                var label = row.Live?.AgentLabel;
                var menu = workspace.Instruments.OpenInspectorMenuText;

                // A turn boundary in between (the user sends a message while the child keeps streaming): the child's call must
                // survive both ends of the turn, otherwise the inspector loses exactly the command it exists to show.
                SetRunning(workspace, true);
                SetRunning(workspace, false);
                var acrossBoundary = workspace.RunningToolCount;

                Emit(workspace, new AgentSubAgentFinished(childId, SubAgentStatus.Completed, TokenUsage.Empty, TimeSpan.Zero, null, At));
                Assert.True(await PumpUntilAsync(() => workspace.RunningToolCount == 0), "the finished child's call stayed tracked");
                return (label, menu, hasLive: row.Live is not null, acrossBoundary, remaining: workspace.RunningToolCount);
            },
            CancellationToken.None);

        Assert.Equal("Scout", state.label);
        Assert.True(state.hasLive);
        Assert.Contains("(", state.menu);
        Assert.Equal(1, state.acrossBoundary);
        Assert.Equal(0, state.remaining);
    }

    private static void SetRunning(SessionWorkspaceViewModel workspace, bool running) =>
        IsRunningProperty.SetValue(workspace, running);

    private static List<ToolRowViewModel> ToolRows(SessionWorkspaceViewModel workspace) =>
        workspace.Transcript.Items
            .OfType<TurnCardViewModel>()
            .SelectMany(static card => card.Blocks)
            .OfType<ToolRowViewModel>()
            .ToList();

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition);

    private static SessionWorkspaceViewModel CreateWorkspace()
    {
        var info = new SessionInfo(SessionId, Path.GetTempPath(), "registry", At);
        var store = new EmptySessionStore(At);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", Forge.App.Localization.ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptySessionCatalog(At),
                    new EmptyTodoStore("todo-1", At),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            TestProviderServices.UnconfiguredProbe());
    }
}