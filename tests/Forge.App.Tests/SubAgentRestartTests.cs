using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A sub-agent that finished before the process ended is still continuable by the next one: the store keeps its id, kind,
/// model and run end, and the rebuilt scheduler hands the same task back to that child instead of starting a fresh one
/// (forge-core "子 Agent 复用"). This is the path a restart takes - two stores over one database file, two schedulers.
/// </summary>
public sealed class SubAgentRestartTests : IDisposable
{
    private const string Task1 =
        "Fix the run count in src/Forge.App/Views/SubAgentView.axaml so the digit is centred in its badge.";

    private readonly string directory = Directory.CreateTempSubdirectory("forge-subagent-restart-").FullName;

    public void Dispose()
    {
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
    public async Task A_child_finished_before_the_restart_is_continued_by_the_next_process()
    {
        var clock = new Clock();
        string rootId;
        string agentId;

        await using (var store = new SqliteSessionStore(directory))
        {
            var root = await ((ISessionCatalog)store)
                .CreateAsync(new SessionCreate(directory, "restart"), CancellationToken.None)
                ;
            rootId = root.Id;
            var first = Scheduler(store, clock);
            var outcome = await Spawn(first, rootId, Task1);
            Assert.False(outcome.IsError, outcome.Text);
            agentId = outcome.AgentId;
            await store.DisposeAsync();
        }

        clock.Now += TimeSpan.FromMinutes(20);

        await using (var store = new SqliteSessionStore(directory))
        {
            var rebuilt = Scheduler(store, clock);
            var rows = await ((ISubAgentStateStore)store).LoadSubAgentStatesAsync(rootId, TestContext.Current.CancellationToken);
            Assert.Single(rows);
            Assert.NotNull(rows[0].State.FinishedAt);
            await rebuilt.RestoreAsync(rootId, CancellationToken.None);

            // The id came back with the conversation, so an explicit resume finds it - with the whole model triple it
            // started on, the pinned output cap included (a resumed child must send the request it sent before).
            Assert.True(rebuilt.TryGetInstance(agentId, out var restored));
            Assert.Equal(agentId, restored.Id);
            Assert.Equal(new ModelSettings("m", ThinkingEffort.High, 4096), restored.Definition.Model);

            // ...and so does the automatic pick, which is what a parent's plain spawn_agent relies on.
            var second = await Spawn(rebuilt, rootId, Task1);
            Assert.True(second.Resumed);
            Assert.Equal(agentId, second.AgentId);

            // A spawn about something else does not join it, and the next id is not the one the restored child holds.
            var third = await Spawn(rebuilt, rootId, "Write the release notes for the next version.");
            Assert.False(third.Resumed);
            Assert.NotEqual(agentId, third.AgentId);
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_table_an_earlier_build_created_is_repaired_in_place()
    {
        // The state table is created by whichever build opens the file first, so a file from a build that had no `max_output`
        // column keeps that shape: reading the missing column would fail the row and the history load that asks for it.
        Directory.CreateDirectory(directory);
        var seedToken = TestContext.Current.CancellationToken;
        await using (var seed = new SqliteConnection($"Data Source={Path.Combine(directory, "forge.db")}"))
        {
            await seed.OpenAsync(seedToken);
            await using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE sub_agent_state (
                    session_id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, kind TEXT NOT NULL, model TEXT NOT NULL,
                    provider TEXT NULL, thinking TEXT NULL, finished_at TEXT NULL, reusable INTEGER NOT NULL DEFAULT 1);
                """;
            await create.ExecuteNonQueryAsync(seedToken);
        }

        await using var store = new SqliteSessionStore(directory);
        var token = TestContext.Current.CancellationToken;
        var root = await store.CreateAsync(new SessionCreate(directory, "old"), token);
        var child = await store.CreateAsync(
            new SessionCreate(directory, "Fix the badge.") { ParentSessionId = root.Id, AgentKind = "Worker", Task = "t" },
            token);
        await ((ISubAgentStateStore)store)
            .SaveSubAgentStateAsync(
                new SubAgentState(child.Id, "Worker-1", "Worker", new ModelSettings("m", ThinkingEffort.High, 2048), root.LastActiveAt),
                token);

        var rows = await ((ISubAgentStateStore)store).LoadSubAgentStatesAsync(root.Id, token);

        var row = Assert.Single(rows);
        Assert.Equal("Worker-1", row.State.AgentId);
        Assert.Equal(2048, row.State.Model.MaxOutputTokens);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task A_child_outside_the_window_is_not_restored()
    {
        var clock = new Clock();
        string rootId;

        await using (var store = new SqliteSessionStore(directory))
        {
            var root = await ((ISessionCatalog)store)
                .CreateAsync(new SessionCreate(directory, "expired"), CancellationToken.None)
                ;
            rootId = root.Id;
            var first = Scheduler(store, clock);
            await Spawn(first, rootId, Task1);
            await store.DisposeAsync();
        }

        clock.Now += TimeSpan.FromHours(2);

        await using (var store = new SqliteSessionStore(directory))
        {
            var rebuilt = Scheduler(store, clock);
            await rebuilt.RestoreAsync(rootId, CancellationToken.None);

            Assert.Empty(rebuilt.Instances);
            await store.DisposeAsync();
        }
    }

    private static SubAgentScheduler Scheduler(ISessionStore store, TimeProvider clock)
    {
        var scheduler = new SubAgentScheduler(
            new AgentCatalog([new AgentDefinition("Worker", "d", "p", new ModelSettings("m", ThinkingEffort.High, 4096), ["read"])]),
            new PromptLayers(string.Empty, null, null),
            new PromptComposer(),
            store,
            new AgentOptions { SubAgentReuseClock = clock, MaxSubAgentDepth = 2 });
        // The loop persists what it runs; without that the store has no conversation to hand back on restart.
        scheduler.Runner = async (instance, message, sink, ct) =>
        {
            instance.Add(message, await store.AppendAsync(instance.SessionId, message, null, ct));
            var answer = ChatMessage.Assistant("done.");
            instance.Add(answer, await store.AppendAsync(instance.SessionId, answer, null, ct));
            return new AgentRunResult("done.", TokenUsage.Empty, StopReason.EndTurn);
        };
        return scheduler;
    }

    private static Task<SubAgentOutcome> Spawn(SubAgentScheduler scheduler, string rootId, string task) =>
        scheduler.SpawnAsync(
            "Worker",
            task,
            resumeId: null,
            new ToolContext(rootId, Directory.GetCurrentDirectory(), 0),
            new Sink(),
            rootId,
            CancellationToken.None);

    private sealed class Sink : IAgentEventSink
    {
        public void Emit(AgentEvent value)
        {
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
