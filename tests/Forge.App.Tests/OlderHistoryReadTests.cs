using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Reading the page above: the number a paged transcript stamps on its rounds must be the store's own count (the derived
/// count is now what every page after the first uses, so a drift would renumber rounds), and a read that fails must show the
/// retry line instead of dropping a notice into a transcript the reader is at the top of.
/// </summary>
public sealed class OlderHistoryReadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-older-history-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    /// <summary>
    /// The paging reader hands the store the count above the page boundary, so the store numbers the page instead of walking
    /// the whole prefix. The page it returns has to be the page the counted path returns - same rows, same round number,
    /// same "is there more".
    /// </summary>
    [Fact]
    public async Task A_page_numbered_from_the_known_count_is_the_counted_page()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var session = await SeedAsync(store, turns: 60);

        var newest = await store.ReadTailAsync(session.Id, long.MaxValue, 100, CancellationToken.None);

        // Sanity on the fixture: 120 messages, the newest page is messages 21..120, so 10 turns lead into it.
        Assert.Equal(100, newest.Messages.Count);
        Assert.Equal(10, newest.TurnsBefore);
        Assert.True(newest.HasMore);

        var boundary = newest.Messages[0].Seq;
        var counted = await store.ReadTailAsync(session.Id, boundary, 100, CancellationToken.None);
        var derived = await store.ReadTailAsync(session.Id, boundary, 100, newest.TurnsBefore, CancellationToken.None);

        Assert.Equal(counted.Messages.Select(message => message.Seq), derived.Messages.Select(message => message.Seq));
        Assert.Equal(20, derived.Messages.Count);
        Assert.Equal(counted.TurnsBefore, derived.TurnsBefore);
        Assert.Equal(0, derived.TurnsBefore);
        Assert.Equal(counted.HasMore, derived.HasMore);
    }

    /// <summary>
    /// A page is whole rounds: the message count only bounds it from below, so a boundary that lands inside a turn is moved
    /// back to the user message that opened that turn. The transcript pages in rounds - a page that starts mid-turn draws the
    /// turn twice, once of them without the question that opened it - and the derived turn number stays exact, because the
    /// extra rows are inside the page.
    /// </summary>
    [Fact]
    public async Task A_page_starts_at_the_user_message_that_opened_its_oldest_turn()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);

        // Three messages a turn (question, answer, answer), so a 100-message page off the end of 50 turns starts at seq 51 -
        // the second answer of turn 17, whose question is seq 49.
        var session = await store.CreateAsync(new SessionCreate(directory, "page-seam"), CancellationToken.None);
        for (var turn = 1; turn <= 50; turn++)
        {
            await store.AppendAsync(session.Id, ChatMessage.User($"question {turn}"), null, CancellationToken.None);
            await store.AppendAsync(session.Id, ChatMessage.Assistant($"answer {turn} a"), null, CancellationToken.None);
            await store.AppendAsync(session.Id, ChatMessage.Assistant($"answer {turn} b"), null, CancellationToken.None);
        }

        var page = await store.ReadTailAsync(session.Id, long.MaxValue, 100, CancellationToken.None);

        Assert.Equal(MessageRole.User, page.Messages[0].Message.Role);
        Assert.Equal(49, page.Messages[0].Seq);
        Assert.Equal(102, page.Messages.Count);
        Assert.Equal(16, page.TurnsBefore);
        Assert.True(page.HasMore);

        // The page above it ends at the same seam: every turn is read in full exactly once.
        var older = await store.ReadTailAsync(
            session.Id, page.Messages[0].Seq, 100, page.TurnsBefore, CancellationToken.None);
        Assert.Equal(MessageRole.User, older.Messages[0].Message.Role);
        Assert.Equal(1, older.Messages[0].Seq);
        Assert.Equal(48, older.Messages.Count);
        Assert.Equal(0, older.TurnsBefore);
        Assert.False(older.HasMore);
    }

    /// <summary>
    /// A failed read is a state on the tab, not a row in the transcript: the transcript's top shows the red retry line, the
    /// list is untouched, and the next read clears it once the store answers again.
    /// </summary>
    [Fact]
    public async Task A_failed_older_page_shows_the_retry_line_and_a_later_read_clears_it()
    {
        Directory.CreateDirectory(directory);
        await using var real = new SqliteSessionStore(directory);
        var info = await SeedAsync(real, turns: 60);
        var store = new FailableStore(real);
        var session = ReadOnlySession(info, store);

        await session.EnsureHistoryAsync(CancellationToken.None);
        Assert.True(session.HasOlderHistory);
        Assert.False(session.OlderLoadFailed);
        var before = session.Transcript.Rounds.Count;

        store.Failable = true;
        Assert.Equal(SessionWorkspaceViewModel.OlderPageLoad.Failed, await session.LoadOlderHistoryAsync(CancellationToken.None));
        Assert.True(session.OlderLoadFailed);
        Assert.Equal(before, session.Transcript.Rounds.Count);
        // The failure is the tab's state, not a row: nothing quoting the store's error was appended to the transcript (a
        // notice lands at the end of the list, which is the last place a reader at the top of the history would see it).
        Assert.DoesNotContain(
            session.Transcript.Rounds.SelectMany(round => round.Rows).OfType<NoticeViewModel>(),
            notice => notice.Text.Contains("wedged", StringComparison.Ordinal));

        store.Failable = false;
        Assert.Equal(SessionWorkspaceViewModel.OlderPageLoad.Loaded, await session.LoadOlderHistoryAsync(CancellationToken.None));
        Assert.False(session.OlderLoadFailed);
        Assert.True(session.Transcript.Rounds.Count > before);
        Assert.False(session.HasOlderHistory);

        // Nothing older in the store: the scroll behaviour is told to stop asking, which is not the same as a failure.
        Assert.Equal(SessionWorkspaceViewModel.OlderPageLoad.Exhausted, await session.LoadOlderHistoryAsync(CancellationToken.None));
    }

    private async Task<SessionInfo> SeedAsync(SqliteSessionStore store, int turns)
    {
        var session = await store.CreateAsync(new SessionCreate(directory, "older-history"), CancellationToken.None);
        for (var turn = 1; turn <= turns; turn++)
        {
            await store.AppendAsync(session.Id, ChatMessage.User($"question {turn}"), null, CancellationToken.None);
            await store.AppendAsync(session.Id, ChatMessage.Assistant($"answer {turn}"), null, CancellationToken.None);
        }

        return session;
    }

    /// <summary>The tab as restore builds it: a read-only view of the stored session, with no runtime and no history yet.</summary>
    private SessionWorkspaceViewModel ReadOnlySession(SessionInfo info, ISessionStore store)
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        return new SessionWorkspaceViewModel(
            info,
            runtime: null,
            checkpoints: null,
            store,
            providers);
    }

    public void Dispose() => http.Dispose();

    /// <summary>A store whose page reads can be made to fail, everything else forwarded: the failure path is not reachable
    /// through a real SQLite store without breaking the file.</summary>
    private sealed class FailableStore(ISessionStore inner) : ISessionStore
    {
        public bool Failable { get; set; }

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            inner.CreateSessionAsync(workspacePath, title, ct);

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            inner.GetSessionAsync(sessionId, ct);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            inner.ListSessionsAsync(workspacePath, ct);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            inner.AppendAsync(sessionId, message, usage, ct);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            inner.ReadAsync(sessionId, fromSeq, toSeq, ct);

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => inner.LastSeqAsync(sessionId, ct);

        public Task<SessionPage> ReadTailAsync(string sessionId, long beforeSeq, int maxMessages, CancellationToken ct) =>
            inner.ReadTailAsync(sessionId, beforeSeq, maxMessages, ct);

        public Task<SessionPage> ReadTailAsync(
            string sessionId, long beforeSeq, int maxMessages, long? turnsAboveBeforeSeq, CancellationToken ct) =>
            Failable
                ? throw new IOException("the session store is wedged")
                : inner.ReadTailAsync(sessionId, beforeSeq, maxMessages, turnsAboveBeforeSeq, ct);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => inner.PutSummaryAsync(summary, ct);

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            inner.GetSummariesAsync(sessionId, ct);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            inner.SearchAsync(sessionId, query, limit, ct);
    }
}
