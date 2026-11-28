using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The managed context: what the model's history is made of once the ledger has rows. One test per promise - one entry per
/// finished turn, raw tool traffic gone from the next turn, folding that only ever absorbs older turns, and the two tools
/// that lead back to a folded range.
/// </summary>
public sealed class ManagedContextTests : IDisposable
{
    private const string Answered = "The task is done.";

    private readonly string workspace = Directory.CreateTempSubdirectory("forge-managed-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    /// <summary>A ledger the loop can read back: rows are created by begin/settle, folding flips the flag, search is exact-prefix.</summary>
    private sealed class SeededLedger : ITurnLedgerStore
    {
        public List<TurnLedgerEntry> Rows { get; } = [];

        public int Folds { get; private set; }

        public Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct)
        {
            var turn = begin.TurnSequence ?? (Rows.Count == 0 ? 1 : Rows.Max(row => row.TurnSequence) + 1);
            if (Rows.All(row => row.TurnSequence != turn))
            {
                Rows.Add(new TurnLedgerEntry(
                    begin.SessionId, turn, TurnLedgerStatus.Pending,
                    begin.TaskText, string.Empty, string.Empty, [], [], [],
                    begin.FromSeq, 0, 0, begin.ProviderId, begin.Model, begin.AgentKind, begin.AgentDepth, false,
                    begin.StartedAt, begin.StartedAt));
            }

            return Task.FromResult(turn);
        }

        public Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct)
        {
            var turn = completion.TurnSequence ?? (Rows.Count == 0 ? 1 : Rows.Max(row => row.TurnSequence) + 1);
            var index = Rows.FindIndex(row => row.TurnSequence == turn);
            var opened = completion.Opened;
            var row = new TurnLedgerEntry(
                completion.SessionId,
                turn,
                completion.Status,
                index >= 0 ? Rows[index].TaskText : opened?.TaskText ?? string.Empty,
                completion.AnswerText,
                completion.ConclusionText,
                completion.Evidence.ReadFiles,
                completion.Evidence.ChangedFiles,
                completion.Evidence.Specs,
                completion.Evidence.FromSeq,
                completion.Evidence.ToSeq,
                completion.AnswerSeq,
                opened?.ProviderId,
                opened?.Model,
                opened?.AgentKind,
                opened?.AgentDepth ?? 0,
                index >= 0 && Rows[index].Folded,
                completion.EndedAt,
                completion.EndedAt);
            if (index >= 0)
            {
                Rows[index] = row;
            }
            else
            {
                Rows.Add(row);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnLedgerEntry>>(
                [.. Rows.Where(row => row.SessionId == sessionId).OrderBy(row => row.TurnSequence)]);

        public Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(row => row.SessionId == sessionId && row.TurnSequence == turnSequence));

        public Task<TurnLedgerPage> ListPageAsync(string sessionId, long beforeTurnSequence, int maxTurns, CancellationToken ct)
        {
            var all = Rows.Where(row => row.SessionId == sessionId).OrderBy(row => row.TurnSequence).ToArray();
            var below = all.Where(row => row.TurnSequence < beforeTurnSequence).ToArray();
            var page = below.TakeLast(maxTurns).ToArray();
            return Task.FromResult(new TurnLedgerPage(page, below.Length > page.Length, all.Length));
        }

        public Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct)
        {
            var hits = Rows
                .Where(row => row.SessionId == sessionId && row.ConclusionText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(row => new TurnLedgerHit(row.TurnSequence, 1, row.ConclusionText))
                .ToArray();
            return Task.FromResult<IReadOnlyList<TurnLedgerHit>>(hits);
        }

        public Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct)
        {
            Folds++;
            for (var index = 0; index < Rows.Count; index++)
            {
                if (Rows[index].SessionId == sessionId && Rows[index].TurnSequence <= throughTurn && !Rows[index].Folded)
                {
                    Rows[index] = Rows[index] with { Folded = true };
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>A finished turn of this session, ready to be folded or read back.</summary>
        public void Seed(string sessionId, long turn, string task, string conclusion, long fromSeq, long toSeq)
        {
            Rows.Add(new TurnLedgerEntry(
                sessionId, turn, TurnLedgerStatus.Settled,
                task, conclusion, conclusion, [$"src/File{turn}.cs"], [$"src/Edited{turn}.cs"], ["forge-core"], fromSeq, toSeq, toSeq,
                "test", "test-model", "Root", 0, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }
    }

    /// <summary>A session store that numbers messages like the real one, so "seq past the last settled turn" is testable.</summary>
    private sealed class NumberedStore : ISessionStore
    {
        private readonly List<(long Seq, ChatMessage Message)> messages = [];
        private long next = 100;

        public long Append(string sessionId, ChatMessage message)
        {
            var seq = next++;
            messages.Add((seq, message));
            return seq;
        }

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(Append(sessionId, message));

        public async IAsyncEnumerable<StoredMessage> ReadAsync(
            string sessionId,
            long fromSeq,
            long? toSeq,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var (seq, message) in messages.ToArray())
            {
                if (seq >= fromSeq && (toSeq is not { } end || seq <= end))
                {
                    yield return new StoredMessage(seq, message, null, DateTimeOffset.UnixEpoch);
                }
            }
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(messages.Count == 0 ? 0 : messages[^1].Seq);

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);
    }

    /// <summary>Answers each request with the scripted message and records what it was handed.</summary>
    private sealed class ScriptedProvider(Func<int, ChatMessage> script) : IChatProvider
    {
        private int calls;

        public List<ChatRequest> Requests { get; } = [];

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            var message = script(Interlocked.Increment(ref calls));
            foreach (var block in message.Content)
            {
                if (block is TextBlock text)
                {
                    yield return new TextDelta(text.Text);
                }
            }

            yield return new MessageCompleted(message, new TokenUsage(10, 5), StopReason.EndTurn);
        }
    }

    private static ChatRequest RequestOf(ScriptedProvider provider, int index) => provider.Requests[index];

    private static string Flat(ChatMessage message) =>
        string.Join("\n", message.Content.OfType<TextBlock>().Select(static block => block.Text));

    /// <summary>A message's text whichever block carries it: a reply is text, a receipt is a tool result.</summary>
    private static string FlatWithBlocks(ChatMessage message) =>
        string.Join("\n", message.Content.Select(static block => block switch
        {
            TextBlock text => text.Text,
            ToolResultBlock result => result.Content,
            ToolCallBlock call => call.ArgumentsJson,
            _ => string.Empty,
        }));

    [Fact]
    public void An_entry_is_the_task_the_reply_and_a_receipt_the_model_cannot_write()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "fix the redirect", "Redirect fixed.", 11, 40);

        var row = Assert.Single(ledger.Rows);

        Assert.Equal("fix the redirect", ManagedContext.RenderTask(row));
        Assert.Equal("Redirect fixed.", ManagedContext.RenderReply(row));
        Assert.Equal(
            """{"turn":1,"seq":"11-40","reads":["src/File1.cs"],"writes":["src/Edited1.cs"],"specs":["forge-core"]}""",
            ManagedContext.RenderReceipt(row));
        // The receipt travels as a tool result - the one role a model cannot produce - so it can never be copied into its
        // own answer the way a label inside the assistant message was.
        Assert.Equal(
            "fix the redirect\n\nRedirect fixed.\n\n" + ManagedContext.RenderReceipt(row),
            ManagedContext.RenderEntry(row));
    }

    [Fact]
    public void A_long_task_reaches_the_model_whole()
    {
        // The task is the user's own words in the request, so cutting it would put words in their mouth: the entry carries it
        // verbatim however long it is (the report keeps its own budget; the task has none).
        var ledger = new SeededLedger();
        var task = string.Concat(Enumerable.Repeat("rewrite the importer so it streams; ", 400));
        ledger.Seed("s1", 1, task, "done", 1, 10);

        Assert.Equal(task, ManagedContext.RenderTask(Assert.Single(ledger.Rows)));
    }

    [Fact]
    public void An_entry_written_under_the_old_template_does_not_show_its_heading_twice()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "greet again", "👋 Hi!\n\n## 结论\nGreeted you, nothing changed.", 5, 6);

        var text = ManagedContext.RenderReply(Assert.Single(ledger.Rows));

        // One label: the legacy heading the answer carried is unwrapped here, so the entry does not draw both.
        Assert.Equal(0, CountOf(text, "Conclusion"));
        Assert.StartsWith("Greeted you, nothing changed.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("👋 Hi!", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_line_the_model_copied_back_is_not_stored_or_drawn_again()
    {
        // Measured on a real session: the model read the receipt under its own earlier answers and wrote one itself, with an
        // invented seq. The entry must not then show two receipt lines - the harness's and the copy.
        var ledger = new SeededLedger();
        ledger.Seed("s1", 2, "again", "Hi again! 👋  <evidence turn=2 seq=5-8>\n\n<evidence turn=2 seq=5-6>", 5, 6);

        var text = ManagedContext.RenderReply(Assert.Single(ledger.Rows));

        Assert.Equal(0, CountOf(text, "evidence"));
        Assert.Equal("Hi again! 👋", text);
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void The_fold_block_names_its_range_and_gives_both_lookups()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "task one", "done one", 1, 10);
        ledger.Seed("s1", 2, "task two", "done two", 11, 20);
        ledger.Seed("s1", 30, "task thirty", "done thirty", 201, 210);
        var folded = ledger.Rows.Where(row => row.TurnSequence <= 2).ToArray();

        var text = ManagedContext.RenderFold(folded);

        Assert.Equal(
            """
            [compacted: turns 1–2 · seq 1–20 · 2 turns]
            retrieve: read_context(from_turn=1, to_turn=2)
            originals: read_history(from_seq=1, to_seq=20)
            """,
            text);
    }

    [Fact]
    public void The_managed_view_puts_the_fold_block_first_then_one_task_and_one_report_per_unfolded_turn()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "old task", "old conclusion", 1, 10);
        ledger.Seed("s1", 2, "new task", "new conclusion", 11, 20);
        ledger.Rows[0] = ledger.Rows[0] with { Folded = true };

        var messages = ManagedContext.Build(ledger.Rows);

        // The view is the history and nothing else: the fold block, then per turn its task (user), its reply (assistant) and
        // the receipt exchange (an assistant call and the tool result it answers). The legend that explains those fields is
        // the request's own first line, not part of the view - so a session's very first turn still leads with it.
        Assert.Equal(5, messages.Count);
        Assert.StartsWith("[compacted: turns 1–1", Flat(messages[0]), StringComparison.Ordinal);
        Assert.Equal(MessageRole.User, messages[1].Role);
        Assert.Equal("new task", Flat(messages[1]));
        Assert.Equal(MessageRole.Assistant, messages[2].Role);
        Assert.Equal("new conclusion", Flat(messages[2]));
        Assert.Equal(MessageRole.Assistant, messages[3].Role);
        Assert.Equal(
            ManagedContext.ReceiptCallId(2),
            Assert.IsType<ToolCallBlock>(Assert.Single(messages[3].Content)).Id);
        Assert.Equal(MessageRole.Tool, messages[4].Role);
        Assert.Equal(
            ManagedContext.ReceiptCallId(2),
            Assert.IsType<ToolResultBlock>(Assert.Single(messages[4].Content)).CallId);
    }

    [Fact]
    public async Task The_next_turn_reads_the_previous_entry_and_none_of_its_tool_traffic()
    {
        var ledger = new SeededLedger();
        var provider = new ScriptedProvider(index => index == 1
            ? WithCall("read", """{"path":"src/A.cs"}""")
            : Says(Answered));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [new StubReadTool()],
            Layers = new PromptLayers("sys", null, null),
            TurnLedger = ledger,
            Store = null,
        });
        var root = host.CreateRoot("s1", workspace);

        await RunTurnAsync(host, root, "first turn");
        await RunTurnAsync(host, root, "second turn");

        var second = RequestOf(provider, 2);
        // The only tool results a managed request carries are the harness's receipts: the previous turn's own tool traffic
        // (its read call and the file it returned) is represented by the entry, not replayed.
        Assert.All(
            second.Messages.Where(message => message.Role == MessageRole.Tool),
            message => Assert.IsType<ToolResultBlock>(Assert.Single(message.Content)).CallId.StartsWith("ev-", StringComparison.Ordinal));
        // Every managed request leads with the legend, the session's first turn included.
        Assert.Equal(ManagedContext.Legend, Flat(second.Messages[0]));
        Assert.Equal(ManagedContext.Legend, Flat(RequestOf(provider, 0).Messages[0]));
        var managed = second.Messages.TakeWhile(message => message.Role != MessageRole.User || Flat(message) != "second turn").ToArray();
        // The previous turn is two messages now: its task as the user's own words, then its report as the assistant's.
        Assert.Contains(managed, message => message.Role == MessageRole.User && Flat(message) == "first turn");
        Assert.Contains(
            managed,
            message => message.Role == MessageRole.Assistant && Flat(message).StartsWith("The task is done.", StringComparison.Ordinal));
        Assert.DoesNotContain(managed, message => Flat(message).Contains("tool says hi", StringComparison.Ordinal));
        // The opening message of the turn being run is still the user's own words, last in the list: this host adds no
        // dynamic tail, so the turn's question closes the request.
        Assert.Equal("second turn", Flat(second.Messages[^1]));
    }

    [Fact]
    public async Task A_context_over_the_line_folds_the_oldest_turns_and_keeps_the_newest_entries()
    {
        var ledger = new SeededLedger();
        for (var turn = 1; turn <= 6; turn++)
        {
            ledger.Seed("s1", turn, $"task {turn}", $"conclusion {turn}", turn * 10, turn * 10 + 5);
        }

        var provider = new ScriptedProvider(_ => Says(Answered));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            TurnLedger = ledger,
            Options = new AgentOptions
            {
                // A window small enough that the six seeded entries are over the trigger line on the turn's first request.
                Compaction = new CompactionPolicy
                {
                    ContextWindow = _ => 900,
                    TriggerRatio = 0.5,
                    EmergencyRatio = 0.9,
                },
            },
        });
        var root = host.CreateRoot("s1", workspace);

        await RunTurnAsync(host, root, "seventh turn");

        Assert.True(ledger.Folds >= 1, "the ledger was asked to fold");
        var first = RequestOf(provider, 0);
        var managed = first.Messages.Select(FlatWithBlocks).ToArray();
        Assert.Contains(managed, text => text.StartsWith("[compacted: turns 1–", StringComparison.Ordinal));
        // The newest entries stay verbatim, and the newest of them is the one right before this turn.
        Assert.Contains(managed, text => text.Contains("\"turn\":6,", StringComparison.Ordinal));
        Assert.DoesNotContain(managed, text => text.Contains("\"turn\":1,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_kept_turn_count_is_what_decides_where_a_fold_stops()
    {
        // The same six settled turns and the same trigger line, twice: only the configured count differs, and it is the count
        // - not a token budget - that decides how many turns survive verbatim.
        async Task<string[]> FoldedAsync(int keepTurns)
        {
            var ledger = new SeededLedger();
            for (var turn = 1; turn <= 6; turn++)
            {
                ledger.Seed("s1", turn, $"task {turn}", $"conclusion {turn}", turn * 10, turn * 10 + 5);
            }

            var provider = new ScriptedProvider(_ => Says(Answered));
            var host = new AgentHost(new AgentHostOptions
            {
                Providers = _ => provider,
                Tools = [],
                Layers = new PromptLayers("sys", null, null),
                TurnLedger = ledger,
                Options = new AgentOptions
                {
                    Compaction = new CompactionPolicy
                    {
                        ContextWindow = _ => 900,
                        TriggerRatio = 0.5,
                            EmergencyRatio = 0.9,
                        KeepRecentTurns = keepTurns,
                    },
                },
            });
            // The class's own temp workspace: a test must never delete a directory it did not create - an earlier version
            // of this one swept the shared temp root and took other tests' repositories with it.
            var root = host.CreateRoot("s1", workspace);
            await RunTurnAsync(host, root, "seventh turn");

            return provider.Requests[0].Messages
                .Where(message => message.Content.OfType<ToolResultBlock>().Any(block => block.CallId.StartsWith("ev-", StringComparison.Ordinal)))
                .Select(message => Assert.IsType<ToolResultBlock>(Assert.Single(message.Content)).Content)
                .ToArray();
        }

        // Five kept: only turn 1 is folded, so turns 2..6 still carry their receipts.
        var wide = await FoldedAsync(keepTurns: 5);
        Assert.Equal(5, wide.Length);
        Assert.DoesNotContain(wide, receipt => receipt.Contains("\"turn\":1,", StringComparison.Ordinal));
        Assert.Contains(wide, receipt => receipt.Contains("\"turn\":2,", StringComparison.Ordinal));
        Assert.Contains(wide, receipt => receipt.Contains("\"turn\":6,", StringComparison.Ordinal));

        // One kept: everything but the newest turn is folded.
        var narrow = await FoldedAsync(keepTurns: 1);
        Assert.Contains("\"turn\":6,", Assert.Single(narrow), StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_the_same_rows_twice_yields_the_same_bytes()
    {
        // The entry text is re-rendered from the store at the start of every turn, so its bytes are what the prompt cache
        // sees. Any nondeterminism here - a hash order, a culture-formatted number, a volatile field - would invalidate the
        // whole history prefix on every request without anything failing. Two renders of the same rows must be identical,
        // and so must two rows built from the same values (a rebuilt instance renders what the live one rendered).
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "first task", "first conclusion", 1, 10);
        ledger.Seed("s1", 2, "second task", "second conclusion", 11, 20);
        ledger.Seed("s1", 3, "third task", "third conclusion", 21, 30);
        ledger.Rows[0] = ledger.Rows[0] with { Folded = true };
        ledger.Rows[1] = ledger.Rows[1] with { Folded = true };

        var first = ManagedContext.Build(ledger.Rows);
        var second = ManagedContext.Build([.. ledger.Rows]);

        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(Flat(first[index]), Flat(second[index]));
        }

        // Under another culture too, where the host allows one: a `ToString()` without InvariantCulture, or a to-lower a
        // Turkish locale folds differently, would change bytes and silently invalidate a user's whole prompt cache. The test
        // host runs globalization-invariant, so the attempt is made and skipped where the runtime refuses the culture.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        }
        catch (CultureNotFoundException)
        {
            // Invariant-only runtime: nothing to compare against, and the invariant assertions above still hold.
            return;
        }

        try
        {
            var turkish = ManagedContext.Build([.. ledger.Rows]);
            Assert.Equal(first.Count, turkish.Count);
            for (var index = 0; index < first.Count; index++)
            {
                Assert.Equal(Flat(first[index]), Flat(turkish[index]));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // The folded block in particular: open items come out in row order, not in whatever order a set would hand back.
        Assert.StartsWith("[compacted: turns 1–2 · seq 1–20 · 2 turns]", Flat(first[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fold_in_the_middle_of_a_turn_keeps_that_turn_s_exchange_verbatim()
    {
        // The emergency line can fire on a request inside a turn. Folding then must not touch what the turn has already
        // produced: the opening message, its tool call and the result are what the model is working with, and a fold that
        // wiped them would leave it re-running the same calls.
        var ledger = new SeededLedger();
        for (var turn = 1; turn <= 5; turn++)
        {
            ledger.Seed("s1", turn, $"task {turn}", $"conclusion {turn}", turn * 10, turn * 10 + 5);
        }

        var provider = new ScriptedProvider(index => index == 1
            ? WithCall("read", """{"path":"src/A.cs"}""")
            : Says(Answered));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [new StubReadTool()],
            Layers = new PromptLayers("sys", null, null),
            TurnLedger = ledger,
            Store = new NumberedStore(),
            Options = new AgentOptions
            {
                // Small enough that the second request of the turn is over the emergency line and folds.
                Compaction = new CompactionPolicy
                {
                    ContextWindow = _ => 900,
                    TriggerRatio = 0.5,
                    EmergencyRatio = 0.6,
                },
            },
        });
        var root = host.CreateRoot("s1", workspace);

        await RunTurnAsync(host, root, "sixth turn");

        var second = RequestOf(provider, 1);
        var texts = second.Messages.Select(Flat).ToArray();
        Assert.Contains("sixth turn", texts);
        // A tool result is a ToolResultBlock, not a TextBlock: the exchange has to be checked where it really lives.
        Assert.Contains(
            second.Messages.SelectMany(message => message.Content.OfType<ToolResultBlock>()),
            result => result.Content.Contains("tool says hi from the raw exchange", StringComparison.Ordinal));
        Assert.Contains(second.Messages, message => message.Content.OfType<ToolCallBlock>().Any(call => call.Name == "read"));
        Assert.True(ledger.Folds >= 1, "the fold happened");
    }

    [Fact]
    public async Task A_message_delivered_between_turns_survives_the_rebuild_and_is_read_by_the_model()
    {
        // A receipt delivered while the session is idle is stored as a user message and appended to the instance. The managed
        // rebuild at the next turn must keep it, or the model never reads a child's report.
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "first task", "first conclusion", 1, 10);

        var provider = new ScriptedProvider(_ => Says(Answered));
        var store = new NumberedStore();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            TurnLedger = ledger,
            Store = store,
        });
        var root = host.CreateRoot("s1", workspace);
        // The delivery the idle branch writes: a user message in the store, and the same message on the instance.
        var delivered = ChatMessage.User("[会话回复] the child finished its task");
        root.Add(delivered, store.Append("s1", delivered));

        await RunTurnAsync(host, root, "second turn");

        var texts = RequestOf(provider, 0).Messages.Select(Flat).ToArray();
        Assert.Contains(texts, text => text.Contains("the child finished its task", StringComparison.Ordinal));
        Assert.Equal("second turn", texts[^1]);
    }

    [Fact]
    public async Task The_receipt_tool_answers_the_turn_it_was_asked_for_and_is_never_offered_to_the_model()
    {
        // The receipt exchange sits in the model's history, so a call it copies must resolve - and resolve with the real
        // numbers, not with "unknown tool". It is dispatch-only: the request's tool list never names it.
        var ledger = new SeededLedger();
        ledger.Seed("s1", 1, "first task", "first conclusion", 1, 10);
        ledger.Seed("s1", 2, "second task", "second conclusion", 11, 20);
        var tool = new EvidenceReceiptTool(ledger);
        var context = new ToolContext("s1", workspace, 0);

        var first = await tool.InvokeAsync(new ToolCall("c1", EvidenceReceiptTool.Name, """{"turn":1}"""), context, TestContext.Current.CancellationToken);
        Assert.False(first.IsError);
        Assert.Equal("""{"turn":1,"seq":"1-10","reads":["src/File1.cs"],"writes":["src/Edited1.cs"],"specs":["forge-core"]}""", first.Content);

        // No turn named: the newest finished one.
        var newest = await tool.InvokeAsync(new ToolCall("c2", EvidenceReceiptTool.Name, "{}"), context, TestContext.Current.CancellationToken);
        Assert.Equal("""{"turn":2,"seq":"11-20","reads":["src/File2.cs"],"writes":["src/Edited2.cs"],"specs":["forge-core"]}""", newest.Content);

        var missing = await tool.InvokeAsync(new ToolCall("c3", EvidenceReceiptTool.Name, """{"turn":9}"""), context, TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
        Assert.Contains("No finished turn #9", missing.Content, StringComparison.Ordinal);

        // Dispatch-only: a registry that resolves it never offers it.
        var registry = new ToolRegistry(System.Array.Empty<ITool>(), [tool], preserveOrder: true);
        Assert.Empty(registry.Definitions);
        Assert.True(registry.TryGet(EvidenceReceiptTool.Name, out var resolved));
        Assert.Same(tool, resolved);
    }

    [Fact]
    public void The_fold_block_stays_a_pointer_line_however_many_turns_it_covers()
    {
        // The block carries no prose of its own - it names the range and the two lookups - so its size follows the numbers,
        // never the content of the turns it stands in for.
        var ledger = new SeededLedger();
        for (var turn = 1; turn <= 20; turn++)
        {
            ledger.Seed("s1", turn, $"task {turn}", $"done {turn}", turn * 10, turn * 10 + 5);
        }

        var text = ManagedContext.RenderFold(ledger.Rows.ToArray());

        Assert.True(text.Length <= 200, $"block was {text.Length} chars");
        Assert.Contains("retrieve: read_context(from_turn=1, to_turn=20)", text, StringComparison.Ordinal);
        Assert.Contains("originals: read_history(from_seq=10, to_seq=205)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_context_answers_the_folded_range_from_the_entries()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 3, "third task", "third conclusion", 31, 40);
        var tool = new ReadContextTool(ledger);

        var result = await tool.InvokeAsync(
            new ToolCall("c1", ReadContextTool.Name, """{"from_turn":3,"to_turn":3}"""),
            new ToolContext("s1", workspace, 0),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("""
            {"turn":3,"seq":"31-40","reads":["src/File3.cs"],"writes":["src/Edited3.cs"],"specs":["forge-core"]}
            """, result.Content, StringComparison.Ordinal);
        Assert.Contains("third conclusion", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_context_finds_a_turn_by_a_word_only_in_its_conclusion()
    {
        var ledger = new SeededLedger();
        ledger.Seed("s1", 7, "some task", "the executor pool is warm now", 71, 80);
        var tool = new SearchContextTool(ledger);

        var result = await tool.InvokeAsync(
            new ToolCall("c1", SearchContextTool.Name, """{"query":"executor"}"""),
            new ToolContext("s1", workspace, 0),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("turn 7", result.Content, StringComparison.Ordinal);
    }

    private static ChatMessage Says(string text) => new(MessageRole.Assistant, [new TextBlock(text)]);

    private static ChatMessage WithCall(string name, string arguments) =>
        new(MessageRole.Assistant, [new ToolCallBlock("call_1", name, arguments)]);

    private static async Task RunTurnAsync(AgentHost host, AgentInstance root, string task)
    {
        await foreach (var _ in host.RunAsync(root, task, CancellationToken.None))
        {
        }
    }

    /// <summary>Answers one call with a marker the next turn must no longer see once the turn became an entry.</summary>
    private sealed class StubReadTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "read",
            "read a file",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("tool says hi from the raw exchange"));
    }
}
