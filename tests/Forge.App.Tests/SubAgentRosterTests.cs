using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The live roster a session keeps: a child that finishes stays on it - tab docked, tree row intact, still the next task's
/// best context - until its reuse window (one hour, counted from the end of its last run) closes. Only a finished child can
/// be retired; closing the tab of a running one must change nothing, because the run still reports to that tab. The clock
/// is driven here so the window is a test fact rather than wall time.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SubAgentRosterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-subagent-roster-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public Task A_finished_child_stays_on_the_roster_for_one_hour() => SharedUi.RunAsync(async () =>
    {
        var clock = new Clock();
        var session = await SessionAsync(clock);
        var startedAt = clock.GetUtcNow();

        Start(session, startedAt);
        var agent = Assert.Single(session.SubAgents);

        Finish(session, SubAgentStatus.Completed, startedAt.AddMinutes(1));
        Assert.Same(agent, Assert.Single(session.SubAgents));
        Assert.Empty(session.Finished);
        Assert.Equal(startedAt.AddMinutes(1), agent.FinishedAt);
        Assert.False(agent.IsLive);

        // Half a minute short of the hour: still docked, still reusable.
        clock.Now = startedAt.AddMinutes(59);
        session.Tick();
        Assert.Single(session.SubAgents);

        // Past it: the tab and the row give way to the history menu.
        clock.Now = startedAt.AddMinutes(61);
        session.Tick();
        Assert.Empty(session.SubAgents);
        Assert.Same(agent, Assert.Single(session.Finished));

        session.Dispose();
    });

    [Fact]
    public Task A_running_child_is_never_retired() => SharedUi.RunAsync(async () =>
    {
        var clock = new Clock();
        var session = await SessionAsync(clock);
        var startedAt = clock.GetUtcNow();

        Start(session, startedAt);
        session.OnAgentEvent(new AgentSubAgentRunning("Worker-1", startedAt));
        Pump();
        var agent = Assert.Single(session.SubAgents);

        // This is what closing a running child's tab asks for; it must be refused - the run keeps reporting to the tab, and
        // a child torn out of the roster would stream into nothing and never finish.
        session.RetireSubAgent(agent);
        Assert.Same(agent, Assert.Single(session.SubAgents));
        Assert.Empty(session.Finished);

        // Its window is meaningless while it runs: a finished stamp only ever comes from a run that ended.
        clock.Now = startedAt.AddHours(5);
        session.Tick();
        Assert.Single(session.SubAgents);

        session.Dispose();
    });

    [Fact]
    public Task A_resumed_child_comes_back_to_the_roster_after_being_retired() => SharedUi.RunAsync(async () =>
    {
        var clock = new Clock();
        var session = await SessionAsync(clock);
        var startedAt = clock.GetUtcNow();

        Start(session, startedAt);
        Finish(session, SubAgentStatus.Completed, startedAt.AddMinutes(1));
        clock.Now = startedAt.AddHours(2);
        session.Tick();
        var retired = Assert.Single(session.Finished);
        Assert.Empty(session.SubAgents);

        // The parent hand the same child its next task: the tab is pulled back out of the history and continues.
        Start(session, clock.GetUtcNow(), "Worker-1");
        Assert.Same(retired, Assert.Single(session.SubAgents));
        Assert.Empty(session.Finished);
        Assert.Null(retired.FinishedAt);

        session.Dispose();
    });

    private async Task<SessionWorkspaceViewModel> SessionAsync(
        TimeProvider clock,
        Func<string, IReadOnlyList<SessionInfo>>? childRows = null)
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        // The stored children a reopened session reads back: keyed by the parent so the recursive descent the restore
        // walks does not find the same child under itself.
        Func<string, CancellationToken, Task<IReadOnlyList<SessionInfo>>>? query = null;
        if (childRows is not null)
        {
            var rows = childRows(root.Id);
            query = (parent, _) => Task.FromResult<IReadOnlyList<SessionInfo>>(
                rows.Where(row => string.Equals(row.ParentSessionId, parent, StringComparison.Ordinal)).ToArray());
        }

        return new SessionWorkspaceViewModel(
            root,
            runtime: null,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http))
        {
            Clock = clock,
            ChildQuery = query,
        };
    }

    [Fact]
    public Task A_stored_row_the_run_resumes_is_a_lived_tab_of_this_run() => SharedUi.RunAsync(async () =>
    {
        var clock = new Clock();
        var startedAt = clock.GetUtcNow();
        var session = await SessionAsync(
            clock,
            rootId => new[]
            {
                new SessionInfo("child-1", directory, "First task.", startedAt)
                {
                    ParentSessionId = rootId,
                    AgentKind = "Worker",
                    Task = "First task.",
                },
            });

        // The reopened tab replays the stored child into the history menu: a row of a run before this process, whose
        // dot in the menu must read as replayed history, not as a live tab of this run.
        await session.LoadHistoryAsync(CancellationToken.None);
        Pump();
        var stored = Assert.Single(session.Finished);
        Assert.True(stored.IsReadOnly);
        Assert.False(stored.WasLiveTab);

        // The scheduler hands that stored child its next task: the start event names the run id and, in its child
        // session, the id the menu's row is keyed by - the resume pulls the row back out of history onto the roster.
        session.OnAgentEvent(new AgentSubAgentStarted("root", "Worker-9", "Worker", "Next task.", "test-model", "child-1", startedAt.AddMinutes(1)));
        Pump();
        Assert.Same(stored, Assert.Single(session.SubAgents));
        Assert.True(stored.WasLiveTab);
        Assert.Empty(session.Finished);

        // And back into the menu: the row's dot now says the tab was live in this run, whatever it was before.
        session.OnAgentEvent(new AgentSubAgentFinished("Worker-9", SubAgentStatus.Completed, TokenUsage.Empty, TimeSpan.FromMinutes(1), null, startedAt.AddMinutes(2)));
        Pump();
        clock.Now = startedAt.AddHours(2);
        session.Tick();
        Assert.Same(stored, Assert.Single(session.Finished));
        Assert.True(stored.WasLiveTab);

        session.Dispose();
    });

    private static void Start(SessionWorkspaceViewModel session, DateTimeOffset at, string childId = "Worker-1")
    {
        session.OnAgentEvent(new AgentSubAgentStarted("root", childId, "Worker", "Do the thing.", "test-model", childId + "-session", at));
        Pump();
    }

    private static void Finish(SessionWorkspaceViewModel session, SubAgentStatus status, DateTimeOffset at)
    {
        session.OnAgentEvent(new AgentSubAgentFinished("Worker-1", status, TokenUsage.Empty, TimeSpan.FromMinutes(1), null, at));
        Pump();
    }

    /// <summary>Drains the dispatcher, which the coalescer posts the queued agent events to.</summary>
    private static void Pump()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
