using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A session tab restored at start-up keeps its history in the store and replays it the first time it is shown: the window
/// used to replay every remembered session's whole transcript on the UI thread before it was usable, while only the tab on
/// screen was ever looked at. The replay stays once-per-tab, which is what keeps a later activation from stacking a second
/// copy of the same rows on top of the first.
/// </summary>
public sealed class LazyHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-lazy-history-" + Guid.NewGuid().ToString("N"));
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
    public async Task A_restored_tab_replays_its_history_once_when_it_is_first_shown()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);

        var session = ReadOnlySession(root);

        // Restored, not shown: the only round is the "provider not configured" notice this test host never configured - the
        // two stored messages have not been read back, and the view is not claiming to be loading anything yet.
        Assert.Single(session.Transcript.Rounds);
        Assert.False(session.IsHistoryLoading);

        await session.EnsureHistoryAsync(CancellationToken.None);
        var loaded = session.Transcript.Rounds.Count;
        Assert.Equal(2, loaded);
        Assert.False(session.IsHistoryLoading);

        // A second activation (the shell asks on every ActiveSession change) must not replay the same rows again.
        await session.EnsureHistoryAsync(CancellationToken.None);
        Assert.Equal(loaded, session.Transcript.Rounds.Count);
    }

    /// <summary>
    /// Opening a session that is not restored asks for its history twice - the shell loads a tab it is about to show, and
    /// the activation right after asks the tab to ensure it. Both entry points are the same one pass: a second replay would
    /// append the same rounds to the transcript and the same stored children to the sub-agent list on top of the first copy.
    /// </summary>
    [Fact]
    public async Task A_tab_whose_history_was_loaded_directly_is_not_replayed_again_when_it_is_activated()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);
        var spawned = await store.CreateAsync(
            new SessionCreate(directory, "Scout: say hi") { ParentSessionId = root.Id, AgentKind = "Scout", Task = "say hi" },
            CancellationToken.None);
        await store.AppendAsync(spawned.Id, ChatMessage.User("say hi"), null, CancellationToken.None);
        await store.AppendAsync(spawned.Id, ChatMessage.Assistant("hi"), null, CancellationToken.None);

        var session = ReadOnlySession(root, store);
        await session.LoadHistoryAsync(CancellationToken.None);

        // The host's own "provider not configured" notice, then the stored turn.
        Assert.Equal(2, session.Transcript.Rounds.Count);
        Assert.Equal(spawned.Id, Assert.Single(session.Finished).ChildSessionId);

        // The activation that follows the load asks again: one copy of every round, one row per stored child.
        await session.EnsureHistoryAsync(CancellationToken.None);
        Assert.Equal(2, session.Transcript.Rounds.Count);
        Assert.Equal(spawned.Id, Assert.Single(session.Finished).ChildSessionId);
    }

    /// <summary>
    /// A session the user opened under a root (an "agent session") has its own tab and its own row beside the root, so it is
    /// not one of the root's sub-agent rows - and neither are the sub-agents it spawned itself, which hang under its own row.
    /// The list is what the project tree draws under the session, so listing them here drew the same session twice.
    /// </summary>
    [Fact]
    public async Task The_restored_sub_agent_rows_hold_the_model_spawned_children_only()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        var worker = await store.CreateAsync(
            new SessionCreate(directory, "Worker 1")
            {
                ParentSessionId = root.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                AgentKind = AgentSessionKinds.Default,
                Task = "list the tools you have",
            },
            CancellationToken.None);
        await store.CreateAsync(
            new SessionCreate(directory, "Scout: say hi")
            {
                ParentSessionId = worker.Id,
                AgentKind = "Scout",
                Task = "say hi",
            },
            CancellationToken.None);
        var spawned = await store.CreateAsync(
            new SessionCreate(directory, "Plan: think") { ParentSessionId = root.Id, AgentKind = "Plan", Task = "think" },
            CancellationToken.None);

        var session = ReadOnlySession(root, store);
        await session.LoadHistoryAsync(CancellationToken.None);

        Assert.Equal(spawned.Id, Assert.Single(session.Finished).ChildSessionId);
    }

    [Fact]
    public async Task A_long_history_is_read_one_page_at_a_time_with_the_session_s_own_round_numbers()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        const int turns = 250;
        for (var turn = 1; turn <= turns; turn++)
        {
            await store.AppendAsync(root.Id, ChatMessage.User($"question {turn}"), null, CancellationToken.None);
            await store.AppendAsync(root.Id, ChatMessage.Assistant($"answer {turn}"), null, CancellationToken.None);
        }

        var session = ReadOnlySession(root);
        await session.EnsureHistoryAsync(CancellationToken.None);

        // One page of 100 messages is the last 50 turns; the round numbers stay the session's own (201..250), not 1..50.
        // (The rounds without a card are the host's own notices.)
        static List<int> Cards(TranscriptViewModel transcript) =>
            [.. transcript.Rounds.Where(round => round.Card is not null).Select(round => round.Card!.Round)];

        Assert.Equal(SessionWorkspaceViewModel.HistoryPageMessages / 2, Cards(session.Transcript).Count);
        Assert.Equal(201, Cards(session.Transcript)[0]);
        Assert.True(session.HasOlderHistory);

        // Each older page lands in front of what is already read, and nothing is read twice.
        var pages = 0;
        while (session.HasOlderHistory && pages < 10)
        {
            await session.LoadOlderHistoryAsync(CancellationToken.None);
            pages++;
        }

        var loaded = Cards(session.Transcript);
        Assert.Equal(turns, loaded.Count);
        Assert.Equal(1, loaded[0]);
        Assert.Equal(turns, loaded[^1]);
        Assert.Equal(Enumerable.Range(1, turns), loaded);
        Assert.Equal(4, pages);
    }

    /// <summary>
    /// A draft tab carries an id no row has yet, and it is still an ordinary session view: showing it replays its history
    /// through that id. The id has to be a legal storage identifier or the replay comes back as a 「读取会话失败」 notice under
    /// the empty transcript - what 「新增 Agent 会话」 showed as soon as its draft tab was activated.
    /// </summary>
    [Fact]
    public async Task A_draft_tab_replays_no_history_and_reports_no_read_failure()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        var session = DraftSession(root);

        var roundsBefore = session.Transcript.Rounds.Count;
        await session.EnsureHistoryAsync(CancellationToken.None);

        // Nothing was replayed (a draft has no row) and, above all, the replay did not fail: the id the tab carries is one the
        // store accepts and simply finds nothing under. When it was rejected ("not allowed in a storage identifier") the
        // failure landed in the transcript as a 「读取会话失败」 notice quoting that very id.
        Assert.Equal(roundsBefore, session.Transcript.Rounds.Count);
        Assert.DoesNotContain(
            session.Transcript.Rounds.SelectMany(round => round.Rows).OfType<NoticeViewModel>(),
            notice => notice.Text.Contains(session.SessionId, StringComparison.Ordinal));
    }

    /// <summary>The tab as restore builds it: a read-only view of the stored session, with no runtime and no history yet.</summary>
    private SessionWorkspaceViewModel ReadOnlySession(SessionInfo info, SqliteSessionStore? store = null)
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        var sessions = store ?? new SqliteSessionStore(directory);
        return new SessionWorkspaceViewModel(
            info,
            runtime: null,
            checkpoints: null,
            store: sessions,
            providers)
        {
            // What the shell hands a read-only tab: the store read behind the restored sub-agent rows.
            ChildQuery = (parent, ct) => sessions.QueryAsync(new SessionQuery { ParentSessionId = parent }, ct),
        };
    }

    /// <summary>The tab 「新增 Agent 会话」 opens: a draft whose session row the first message has not created yet.</summary>
    private SessionWorkspaceViewModel DraftSession(SessionInfo root)
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        var draft = root with
        {
            Id = MainWindowViewModel.NewAgentDraftId(root.Id, 1),
            ParentSessionId = root.Id,
            ChildDriver = AgentSessionKinds.ChildDriverUser,
        };
        return new SessionWorkspaceViewModel(
            draft,
            runtime: null,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            providers,
            agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: true));
    }
}
