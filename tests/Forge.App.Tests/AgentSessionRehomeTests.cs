using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A promotion hands the workspace to another root. The agent-session tabs of the branch move under it in place: the view
/// keeps its transcript and draft, only the root it hangs under (and the runtime it runs on) changes. A promotion used to
/// close and reopen every one of those tabs, which rebuilt each view and re-attached every panel - the slow part of a
/// promotion on a branch with several open agent tabs.
/// </summary>
public sealed class AgentSessionRehomeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-rehome-" + Guid.NewGuid().ToString("N"));
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
    public async Task Rehoming_an_agent_tab_moves_its_root_and_parent_link_without_replaying_it()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(new SessionCreate(directory, "Old Root"), CancellationToken.None);
        var promoted = await store.CreateAsync(
            new SessionCreate(directory, "Promoted")
            {
                ParentSessionId = oldRoot.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = "Worker",
            },
            CancellationToken.None);
        var worker = await store.CreateAsync(
            new SessionCreate(directory, "Worker 2")
            {
                ParentSessionId = oldRoot.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = "Worker",
            },
            CancellationToken.None);
        await store.AppendAsync(worker.Id, ChatMessage.User("question"), null, CancellationToken.None);
        await store.AppendAsync(worker.Id, ChatMessage.Assistant("answer"), null, CancellationToken.None);

        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        var view = new SessionWorkspaceViewModel(
            worker,
            runtime: null,
            checkpoints: null,
            store: store,
            providers,
            agentSession: new AgentSessionLaunch(oldRoot, Ordinal: 2, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));
        await view.LoadHistoryAsync(CancellationToken.None);
        var rounds = view.Transcript.Rounds.Count;

        view.RehomeTo(promoted, runtime: null);

        Assert.Equal(promoted.Id, view.AgentSessionRoot!.Id);
        Assert.Equal(promoted.Id, view.Info.ParentSessionId);
        Assert.StartsWith("Promoted \u203a ", view.Title, StringComparison.Ordinal);
        // The transcript the tab already showed is the one it keeps: nothing is replayed by the move.
        Assert.Equal(rounds, view.Transcript.Rounds.Count);
    }

    /// <summary>
    /// The <c>@</c> picker's session group is the root the tab hangs under (forge-app "`@` 会话提及与授权"): moving that root
    /// has to re-read the shell's source, or the tab keeps offering the root it left behind - archived by the same promotion,
    /// so a mention of it can no longer be delivered - and never offers the one that now runs it.
    /// </summary>
    [Fact]
    public async Task A_rehomed_tab_offers_the_new_root_in_the_at_picker()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(new SessionCreate(directory, "Old Root"), CancellationToken.None);
        var promoted = await store.CreateAsync(
            new SessionCreate(directory, "Promoted")
            {
                ParentSessionId = oldRoot.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = "Worker",
            },
            CancellationToken.None);
        var worker = await store.CreateAsync(
            new SessionCreate(directory, "Worker 2")
            {
                ParentSessionId = oldRoot.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = "Worker",
            },
            CancellationToken.None);

        var services = new AgentRuntimeServices(
            ProviderFactory: _ => throw new InvalidOperationException("this test never runs a turn"),
            HostClient: null,
            Store: store,
            Catalog: store,
            Todos: new SqliteTodoStore(directory),
            Model: new ModelSettings("test-model"));
        using var runtime = new SessionRuntime(services, oldRoot);
        using var promotedRuntime = new SessionRuntime(services, promoted);
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        SessionWorkspaceViewModel? created = null;
        using var view = new SessionWorkspaceViewModel(
            worker,
            runtime,
            checkpoints: null,
            store: store,
            providers,
            fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
            agentSession: new AgentSessionLaunch(oldRoot, Ordinal: 2, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker),
            // The shell's own source in an agent-session tab's shape (MainWindowViewModel.SessionMentionTargetsFor): the
            // root the tab hangs under at the moment it is asked.
            sessionMentions: () => created?.AgentSessionRoot is { } root
                ? [new SessionMentionTarget(root.Id, root.Title)]
                : []);
        created = view;

        // The fill the shell does right after it builds a tab; from here on the picker shows what the view answers.
        view.RefreshSessionMentions();
        view.Composer.UpdateSuggestions("@", 1);
        Assert.Equal(oldRoot.Id, Assert.Single(view.Composer.Suggestions).SessionId);

        view.RehomeTo(promoted, promotedRuntime);

        var row = Assert.Single(view.Composer.Suggestions);
        Assert.Equal(promoted.Id, row.SessionId);
        Assert.Equal("Promoted", row.Title);
    }

    /// <summary>
    /// A renamed root is offered under its new name. The tab's only copy of the root is the one it was opened with, and the
    /// <c>@</c> picker reads that copy - so a rename that only re-prefixed the tab's own label kept the old name in the list.
    /// </summary>
    [Fact]
    public async Task A_renamed_root_is_offered_under_its_new_name_in_the_at_picker()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "Old Root"), CancellationToken.None);
        var worker = await store.CreateAsync(
            new SessionCreate(directory, "Worker 1")
            {
                ParentSessionId = root.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = "Worker",
            },
            CancellationToken.None);

        var services = new AgentRuntimeServices(
            ProviderFactory: _ => throw new InvalidOperationException("this test never runs a turn"),
            HostClient: null,
            Store: store,
            Catalog: store,
            Todos: new SqliteTodoStore(directory),
            Model: new ModelSettings("test-model"));
        using var runtime = new SessionRuntime(services, root);
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        SessionWorkspaceViewModel? created = null;
        using var view = new SessionWorkspaceViewModel(
            worker,
            runtime,
            checkpoints: null,
            store: store,
            providers,
            fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker),
            sessionMentions: () => created?.AgentSessionRoot is { } current
                ? [new SessionMentionTarget(current.Id, current.Title)]
                : []);
        created = view;
        view.RefreshSessionMentions();
        view.Composer.UpdateSuggestions("@", 1);
        Assert.Equal("Old Root", Assert.Single(view.Composer.Suggestions).Title);

        // What the shell does when the store holds a new title for the root: push it to the tabs, then re-read their pickers.
        view.RenameRoot("Renamed Root");
        view.RefreshSessionMentions();

        var row = Assert.Single(view.Composer.Suggestions);
        Assert.Equal(root.Id, row.SessionId);
        Assert.Equal("Renamed Root", row.Title);
    }
}
