using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The <c>@</c> picker's session group is a rule, not a window (forge-app "`@` 会话提及与授权"): which sessions one tab may
/// mention, and which tree's pickers a rename re-reads. Both are pinned here without building the shell that owns the tabs - the
/// window only hands in its open views and the rows it read from the store.
/// <para>A history row opens as a read-only view (the shell's <c>OpenAgentSessionByIdAsync</c>), so a read-only agent tab is
/// exactly what an archived session looks like to the picker.</para>
/// </summary>
public sealed class SessionMentionRulesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-mention-rules-" + Guid.NewGuid().ToString("N"));
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
    public async Task An_archived_agents_history_tab_is_not_offered_while_a_live_one_is()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "Root"), CancellationToken.None);
        var live = await AgentSessionAsync(store, root, "Live");
        var archived = await AgentSessionAsync(store, root, "Gone");
        Assert.True(await ((ISessionCatalog)store).SetStatusAsync(archived.Id, SessionStatus.Archived, CancellationToken.None));
        archived = (await store.GetSessionAsync(archived.Id, CancellationToken.None))!;

        using var runtime = Runtime(store, root);
        using var rootView = new SessionWorkspaceViewModel(root, runtime: null, checkpoints: null, store: store, Providers());
        using var liveView = new SessionWorkspaceViewModel(
            live,
            runtime,
            checkpoints: null,
            store: store,
            Providers(),
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));
        using var archivedView = new SessionWorkspaceViewModel(
            archived,
            runtime: null,
            checkpoints: null,
            store: store,
            Providers(),
            agentSession: new AgentSessionLaunch(root, Ordinal: 2, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));

        var targets = SessionMentionRules.Targets(rootView, [liveView, archivedView], stored: null);

        var target = Assert.Single(targets);
        Assert.Equal(live.Id, target.SessionId);
        Assert.Equal("Live", target.Name);
    }

    [Fact]
    public async Task A_closed_session_is_offered_from_the_stored_rows_and_never_twice()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "Root"), CancellationToken.None);
        var open = await AgentSessionAsync(store, root, "Open");
        var closed = await AgentSessionAsync(store, root, "Closed");

        using var runtime = Runtime(store, root);
        using var rootView = new SessionWorkspaceViewModel(root, runtime: null, checkpoints: null, store: store, Providers());
        using var openView = new SessionWorkspaceViewModel(
            open,
            runtime,
            checkpoints: null,
            store: store,
            Providers(),
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));

        // The shell lists the live tab first and then the rows it read from the store; the row of the open tab repeats that tab
        // rather than becoming a second session.
        var targets = SessionMentionRules.Targets(
            rootView,
            [openView],
            [new SessionMentionTarget(open.Id, "Open"), new SessionMentionTarget(closed.Id, "Closed")]);

        Assert.Collection(
            targets,
            first => Assert.Equal(open.Id, first.SessionId),
            second => Assert.Equal(closed.Id, second.SessionId));
    }

    [Fact]
    public async Task An_agents_tab_offers_the_root_it_hangs_under()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "Root"), CancellationToken.None);
        var child = await AgentSessionAsync(store, root, "Worker 1");

        using var childView = new SessionWorkspaceViewModel(
            child,
            runtime: null,
            checkpoints: null,
            store: store,
            Providers(),
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));

        var target = Assert.Single(SessionMentionRules.Targets(childView, [], stored: null));
        Assert.Equal(root.Id, target.SessionId);
        Assert.Equal(root.Title, target.Name);
    }

    [Fact]
    public async Task A_rename_re_reads_the_tree_the_session_lives_in()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "Root"), CancellationToken.None);
        var child = await AgentSessionAsync(store, root, "Worker 1");

        using var childView = new SessionWorkspaceViewModel(
            child,
            runtime: null,
            checkpoints: null,
            store: store,
            Providers(),
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));

        // An agent session's pickers belong to its root's tree; a root's own (or a closed session's, whose row only the stored
        // lists carry) to itself.
        Assert.Equal(root.Id, SessionMentionRules.TreeRootOf(child.Id, [childView]));
        Assert.Equal(root.Id, SessionMentionRules.TreeRootOf(root.Id, [childView]));
    }

    private ProviderServices Providers() => new(Path.Combine(directory, "settings.json"), directory, http);

    /// <summary>The runtime an agent tab runs on - its root's; no turn is ever started on it here.</summary>
    private SessionRuntime Runtime(SqliteSessionStore store, SessionInfo root) =>
        new(
            new AgentRuntimeServices(
                ProviderFactory: _ => throw new InvalidOperationException("this test never runs a turn"),
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            root);

    private Task<SessionInfo> AgentSessionAsync(SqliteSessionStore store, SessionInfo root, string title) =>
        store.CreateAsync(
            new SessionCreate(directory, title)
            {
                ParentSessionId = root.Id,
                AgentKind = "Worker",
                Task = title,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
            },
            CancellationToken.None);
}
