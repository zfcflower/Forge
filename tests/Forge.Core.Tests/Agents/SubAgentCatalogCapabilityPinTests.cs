using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Agents.SubAgentTestSupport;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Pins the catalog-capability routing of sub-agent session creation.
/// <see cref="LoggingSessionStore"/> always implements <see cref="ISessionCatalog"/>, but its
/// members throw when the inner store has no catalog. <c>SessionCatalogs.From</c> therefore
/// returns null for <c>LoggingSessionStore { SupportsCatalog: false }</c>; a host built over
/// one must spawn through plain <c>CreateSessionAsync</c> instead of failing like the old
/// direct <c>as ISessionCatalog</c> cast did.
/// </summary>
public sealed class SubAgentCatalogCapabilityPinTests
{
    /// <summary>Plain store with call counting; deliberately NOT an <see cref="ISessionCatalog"/>.</summary>
    private sealed class PlainStore : ISessionStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, SessionInfo> sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<StoredMessage>> messages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> sequences = new(StringComparer.Ordinal);

        public int CreateSessionCalls { get; private set; }

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct)
        {
            lock (gate)
            {
                CreateSessionCalls++;
                var session = new SessionInfo(Guid.NewGuid().ToString("n"), workspacePath, title, DateTimeOffset.UtcNow);
                sessions[session.Id] = session;
                return Task.FromResult(session);
            }
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
                IReadOnlyList<SessionInfo> all = [.. sessions.Values];
                return Task.FromResult(all);
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

        public async IAsyncEnumerable<StoredMessage> ReadAsync(
            string sessionId, long fromSeq, long? toSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            StoredMessage[] snapshot;
            lock (gate)
            {
                snapshot = messages.TryGetValue(sessionId, out var list)
                    ? [.. list.Where(m => m.Seq >= fromSeq && m.Seq <= (toSeq ?? long.MaxValue))]
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

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);
    }

    private static AgentHost CreateHost(IChatProvider provider, ISessionStore store) =>
        AgentTestHost.Create(
            provider,
            [FakeTool.Echo("read")],
            options: Options(),
            agents: [SubAgentTestSupport.Scout],
            store: store);

    private static FakeProvider SpawningProvider() =>
        new((request, index) =>
            Turn.LastUserText(request) == "do research"
                ? Turn.Say("child report")
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "do research")))
                    : Turn.Say("parent done"));

    [Fact]
    public async Task Spawn_over_non_catalog_inner_succeeds_through_plain_create()
    {
        using var directories = new TempDir();
        var inner = new PlainStore();
        await using var log = new JsonlEventLog(Path.Combine(directories.Path, "logs"));
        await using var store = new LoggingSessionStore(inner, log);

        // Capability probe: the decorator exposes the catalog surface but has nothing behind it.
        Assert.False(store.SupportsCatalog);
        // Documents the trap the fix avoids: a direct cast sees a catalog whose members throw.
        Assert.Throws<InvalidOperationException>(() => _ = store.Catalog);

        var host = CreateHost(SpawningProvider(), store);
        var root = host.CreateRoot("session-plain-" + Guid.NewGuid().ToString("n"), Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(30));

        // No InvalidOperationException from a misrouted catalog path, no failed child.
        Assert.Empty(events.OfType<AgentFailed>());
        var child = Assert.Single(host.SubAgents.Instances);
        var outcome = events.OfType<AgentSubAgentFinished>().FirstOrDefault(e => e.AgentId == child.Id);
        Assert.NotNull(outcome);
        Assert.Equal(SubAgentStatus.Completed, outcome.Status);

        // The child session went through plain CreateSessionAsync, exactly once.
        Assert.Equal(1, inner.CreateSessionCalls);
        Assert.NotNull(await inner.GetSessionAsync(child.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Spawn_over_sqlite_uses_the_catalog_path_with_parent_link_kind_and_task()
    {
        using var directories = new TempDir();
        var dbDir = Path.Combine(directories.Path, "db");
        Directory.CreateDirectory(dbDir);
        await using var sqlite = new SqliteSessionStore(dbDir);
        await using var log = new JsonlEventLog(Path.Combine(directories.Path, "logs"));
        await using var store = new LoggingSessionStore(inner: sqlite, log);

        Assert.True(store.SupportsCatalog);

        var catalog = (ISessionCatalog)sqlite;
        var parent = await catalog.CreateAsync(
            new SessionCreate(Path.GetTempPath(), "parent")
            {
                ProjectId = "proj-1",
                WorktreePath = "/repo/wt-1",
            },
            CancellationToken.None);

        var host = CreateHost(SpawningProvider(), store);
        var root = host.CreateRoot(parent.Id, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(events.OfType<AgentFailed>());
        var child = Assert.Single(host.SubAgents.Instances);
        var children = await catalog.QueryAsync(
            new SessionQuery { ParentSessionId = parent.Id }, CancellationToken.None);
        var stored = Assert.Single(children);
        Assert.Equal(child.SessionId, stored.Id);
        Assert.Equal(parent.Id, stored.ParentSessionId);
        Assert.Equal("Scout", stored.AgentKind);
        Assert.Equal("do research", stored.Task);
        // The catalog path inherits the parent's project/worktree binding.
        Assert.Equal("proj-1", stored.ProjectId);
        Assert.Equal("/repo/wt-1", stored.WorktreePath);
    }
}
