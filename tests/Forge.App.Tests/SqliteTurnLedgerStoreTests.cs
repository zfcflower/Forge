using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The turn-ledger store's persisted contract: what a row round-trips to, that a begun row cannot be reset by a second
/// begin, that a settle without a begin still leaves exactly one row - carrying the identity the completion's begin
/// record rode in on - and that the search surface finds a turn by a word only its conclusion carries.
/// </summary>
public sealed class SqliteTurnLedgerStoreTests : IDisposable
{
    private const string Session = "s1";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-turn-ledger-" + Guid.NewGuid().ToString("n"));

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

    private static TurnLedgerBegin Begin(
        long? turn,
        string task = "fix the parser",
        long fromSeq = 4,
        DateTimeOffset? started = null) =>
        new(
            Session,
            turn,
            task,
            "anthropic",
            "claude-sonnet",
            "Root",
            0,
            fromSeq,
            started ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private static TurnLedgerCompletion Completion(
        long turn,
        string answer = "the answer as shown",
        string conclusion = "the parsed conclusion",
        long answerSeq = 12,
        long toSeq = 12) =>
        new(
            Session,
            turn,
            TurnLedgerStatus.Settled,
            answer,
            conclusion,
            new TurnEvidence(["src/Forge.Core/Agents/ManagedContext.cs"], ["src/Forge.Core/Agents/AgentLoop.cs"], ["forge-core"], 4, toSeq),
            answerSeq,
            new DateTimeOffset(2026, 1, 2, 3, 4, 40, TimeSpan.Zero));

    [Fact]
    public async Task A_begun_and_settled_turn_round_trips_every_column()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        var startedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var endedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 40, TimeSpan.Zero);
        await store.BeginAsync(Begin(7, started: startedAt), CancellationToken.None);
        await store.CompleteAsync(Completion(7, toSeq: 14), CancellationToken.None);

        var row = await store.GetAsync(Session, 7, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(Session, row.SessionId);
        Assert.Equal(7, row.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, row.Status);
        Assert.Equal("fix the parser", row.TaskText);
        Assert.Equal("the answer as shown", row.AnswerText);
        Assert.Equal("the parsed conclusion", row.ConclusionText);
        Assert.Equal(["src/Forge.Core/Agents/ManagedContext.cs"], row.ReadFiles);
        Assert.Equal(["src/Forge.Core/Agents/AgentLoop.cs"], row.ChangedFiles);
        Assert.Equal(["forge-core"], row.Specs);
        Assert.Equal(4, row.FromSeq);
        Assert.Equal(14, row.ToSeq);
        Assert.Equal(12, row.AnswerSeq);
        Assert.Equal("anthropic", row.ProviderId);
        Assert.Equal("claude-sonnet", row.Model);
        Assert.Equal("Root", row.AgentKind);
        Assert.Equal(0, row.AgentDepth);
        Assert.Equal(startedAt, row.CreatedAt);
        Assert.Equal(endedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task A_row_this_turn_already_has_is_left_alone_by_a_second_begin()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        var first = Begin(1, task: "the real task");
        await store.BeginAsync(first, CancellationToken.None);
        var answered = await store.BeginAsync(Begin(1, task: "a retried turn's text"), CancellationToken.None);

        var row = await store.GetAsync(Session, 1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(TurnLedgerStatus.Pending, row.Status);
        Assert.Equal("the real task", row.TaskText);
        Assert.Equal(first.StartedAt, row.CreatedAt);
        // One turn keeps exactly one row: the second begin neither reset the first nor added a second, and it answers
        // the number the row already has - a retried begin never leaves the caller guessing at a new one.
        Assert.Equal(1, answered);
        var rows = await store.ListAsync(Session, CancellationToken.None);
        var rowAgain = Assert.Single(rows);
        Assert.Equal(1, rowAgain.TurnSequence);
    }

    [Fact]
    public async Task A_settle_without_a_begin_creates_the_row_settled()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        await store.CompleteAsync(Completion(3), CancellationToken.None);

        var row = await store.GetAsync(Session, 3, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(TurnLedgerStatus.Settled, row.Status);
        Assert.Equal(string.Empty, row.TaskText);
        Assert.Equal(4, row.FromSeq);
    }

    [Fact]
    public async Task A_settle_that_creates_the_row_files_the_identity_the_begin_carried()
    {
        // No begin ever landed under any number, so the settle creates the row itself - and the begin record riding
        // the completion is what it files: the row is born settled but not anonymous.
        await using var store = new SqliteTurnLedgerStore(dir);
        await store.CompleteAsync(
            Completion(0) with
            {
                TurnSequence = null,
                Opened = Begin(null, task: "the task text of a turn whose begins both failed", fromSeq: 7) with
                {
                    AgentKind = "Researcher",
                    AgentDepth = 2
                }
            },
            CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(Session, CancellationToken.None));
        Assert.Equal(1, row.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, row.Status);
        Assert.Equal("the task text of a turn whose begins both failed", row.TaskText);
        Assert.Equal("anthropic", row.ProviderId);
        Assert.Equal("claude-sonnet", row.Model);
        Assert.Equal("Researcher", row.AgentKind);
        Assert.Equal(2, row.AgentDepth);
        // The begin's from-seq wins over the completion's evidence (4), the way the row's own opener would know it.
        Assert.Equal(7, row.FromSeq);
    }

    [Fact]
    public async Task List_reads_one_sessions_turns_oldest_first()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        foreach (var turn in new long[] { 3, 1, 2 })
        {
            await store.BeginAsync(Begin(turn), CancellationToken.None);
        }

        // Another session's rows are not this session's timeline.
        await store.BeginAsync(Begin(9) with { SessionId = "s2" }, CancellationToken.None);

        var rows = await store.ListAsync(Session, CancellationToken.None);
        Assert.Equal([1L, 2L, 3L], rows.Select(static row => row.TurnSequence));
    }

    [Fact]
    public async Task A_begin_without_a_number_answers_the_number_its_insert_filed()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        Assert.Equal(1, await store.BeginAsync(Begin(null), CancellationToken.None));
        Assert.Equal(2, await store.BeginAsync(Begin(null), CancellationToken.None));
        // A caller's own number is answered back as filed, and the next allocation follows the maximum either
        // writer left behind - the reserved 9 counts, a gap does not skip numbers.
        Assert.Equal(9, await store.BeginAsync(Begin(9), CancellationToken.None));
        Assert.Equal(10, await store.BeginAsync(Begin(null), CancellationToken.None));
        // Every number a begin answered is a number its own insert filed, so the session's rows are exactly the
        // answered numbers, in order. What that pins is sequential allocation - and nothing about overlap:
        // Microsoft.Data.Sqlite's async surface runs synchronously, so two begins here never run at the same instant,
        // and a "read MAX+1, then insert" pair would answer these same numbers in this same order. That the number and
        // its insert share one transaction is a property of the write the code makes, not something this test observes.
        Assert.Equal(
            [1L, 2L, 9L, 10L],
            (await store.ListAsync(Session, CancellationToken.None)).Select(static row => row.TurnSequence));
    }

    [Fact]
    public async Task A_completion_with_a_null_number_files_row_one_on_an_empty_session()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        await store.CompleteAsync(Completion(0) with { TurnSequence = null }, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(Session, CancellationToken.None));
        Assert.Equal(1, row.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, row.Status);
        // Born settled: only the opener knows the task text, and the begin never landed.
        Assert.Equal(string.Empty, row.TaskText);
        Assert.Equal("the answer as shown", row.AnswerText);
    }

    [Fact]
    public async Task The_row_a_turn_ends_on_is_found_by_its_answer_seq_and_only_when_it_is_alone()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        await store.BeginAsync(Begin(1), CancellationToken.None);
        await store.CompleteAsync(Completion(1, answerSeq: 12, toSeq: 12), CancellationToken.None);
        await store.BeginAsync(Begin(2), CancellationToken.None);
        await store.CompleteAsync(Completion(2, answerSeq: 21, toSeq: 21), CancellationToken.None);

        var found = await store.FindByAnswerSeqAsync(Session, 21, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(2, found.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, found.Status);
        // No row of this session ends on that message.
        Assert.Null(await store.FindByAnswerSeqAsync(Session, 999, CancellationToken.None));
        // Another session's rows are not this session's timeline.
        Assert.Null(await store.FindByAnswerSeqAsync("s2", 21, CancellationToken.None));

        // A position two rows share pins nothing: the pairing key must be a position, not a guess.
        await store.CompleteAsync(Completion(3, answerSeq: 21, toSeq: 21), CancellationToken.None);
        Assert.Null(await store.FindByAnswerSeqAsync(Session, 21, CancellationToken.None));
    }

    [Fact]
    public async Task Search_finds_a_turn_by_a_word_only_its_conclusion_carries()
    {
        await using var store = new SqliteTurnLedgerStore(dir);
        await store.BeginAsync(Begin(1), CancellationToken.None);
        await store.CompleteAsync(Completion(1, conclusion: "the xylophone case is closed", toSeq: 9), CancellationToken.None);
        await store.BeginAsync(Begin(2), CancellationToken.None);
        await store.CompleteAsync(
            Completion(2, answer: "plain", conclusion: "nothing about instruments", answerSeq: 21, toSeq: 21),
            CancellationToken.None);

        var hits = await store.SearchAsync(Session, "xylophone", 10, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(1, hit.TurnSequence);
        Assert.Contains("xylophone", hit.Snippet, StringComparison.Ordinal);
    }
}
