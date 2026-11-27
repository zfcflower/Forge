using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The ledger read beside a transcript replay is best-effort, exactly like the metrics read it travels with: a store that
/// wedges answers no rows, and a history that would otherwise load still loads. The two reads used to differ - the ledger
/// one let its exceptions escape, so a ledger-only failure aborted history loading entirely.
/// </summary>
public sealed class TranscriptReplayLedgerReadTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-replay-ledger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_ledger_read_that_fails_does_not_stop_the_history_from_loading()
    {
        Directory.CreateDirectory(dir);
        await using var store = new SqliteSessionStore(dir);
        var session = await store.CreateAsync(new SessionCreate(dir, "ledger-wedged"), CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("hi"), null, TestContext.Current.CancellationToken);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("hello"), null, TestContext.Current.CancellationToken);

        var load = await TranscriptReplay.ReadAllAsync(store, null, session.Id, TestContext.Current.CancellationToken, new WedgedLedger());

        // The session's own read succeeded; the ledger read answered no rows instead of taking the whole load down.
        Assert.Equal(2, load.Page.Messages.Count);
        Assert.NotNull(load.Ledger);
        Assert.Empty(load.Ledger);
    }

    /// <summary>The failing source the fix must survive: every read it is asked for throws the storage's own exception.</summary>
    private sealed class WedgedLedger : ITurnLedgerStore
    {
        public Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");

        public Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");

        public Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");

        public Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");

        public Task<TurnLedgerEntry?> FindByAnswerSeqAsync(string sessionId, long answerSeq, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");

        public Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct) => Task.CompletedTask;


        public Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            throw new IOException("the ledger store is wedged");
    }
}
