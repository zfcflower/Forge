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

    /// <summary>The tab as restore builds it: a read-only view of the stored session, with no runtime and no history yet.</summary>
    private SessionWorkspaceViewModel ReadOnlySession(SessionInfo info)
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        return new SessionWorkspaceViewModel(
            info,
            runtime: null,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            providers);
    }
}
