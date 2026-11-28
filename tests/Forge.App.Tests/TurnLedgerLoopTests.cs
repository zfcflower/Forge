using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The loop's ledger writes, each driven through the real loop with a scripted provider and a recording store: the row
/// a compliant turn settles, the row a plain-prose turn settles, the once-only nudge the next turn's request carries, and
/// the row a failing turn leaves behind - settled, never pending. The begin's own failure is here too: the settle retries
/// it once so the row keeps its task text, and a begin that fails outright still settles, under a null number, with the
/// begin record riding the settle so the row keeps its identity. A harness-terminated turn is recorded from the run's
/// own streamed text and nothing else - a turn cancelled before its first delta never inherits the previous turn's
/// answer, and a run that died in a later request keeps everything its earlier ones said - and a cancellation that
/// arrives while the begin is still pending cannot lose the row, because the begin is not tied to the turn's token.
/// </summary>
public sealed class TurnLedgerLoopTests : IDisposable
{
    private const string ReceivedAnswer = "The task is done and verified.";

    private readonly string workspace = Directory.CreateTempSubdirectory("forge-turn-ledger-loop-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    /// <summary>
    /// Records every begin and settle the loop makes; reads answer with whatever was stored. A begin allocates the
    /// number itself - the reservation is the insert, the way the real store's is - and the first
    /// <see cref="BeginFailures"/> calls throw instead, so a test can make the begin fail once, always, or never.
    /// </summary>
    private sealed class RecordingLedger : ITurnLedgerStore
    {
        private long nextTurn;
        private int beginCalls;

        public List<TurnLedgerBegin> Begins { get; } = [];

        public List<TurnLedgerCompletion> Completions { get; } = [];

        /// <summary>How many of the first begins refuse to open the row; the settle's own retry is the call after them.</summary>
        public int BeginFailures { get; set; }

        /// <summary>Set to hold the next begin open: the store waits on it, never on the caller's token.</summary>
        public TaskCompletionSource? HoldBegin { get; set; }

        /// <summary>Completes the moment the next begin is entered, so a test knows the write is in flight.</summary>
        public TaskCompletionSource? NoteBeginEntered { get; set; }

        public async Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct)
        {
            NoteBeginEntered?.TrySetResult();
            if (HoldBegin is { } held)
            {
                await held.Task.ConfigureAwait(false);
            }

            if (Interlocked.Increment(ref beginCalls) <= BeginFailures)
            {
                throw new InvalidOperationException("the begin write failed");
            }

            Begins.Add(begin);
            return begin.TurnSequence ?? Interlocked.Increment(ref nextTurn);
        }

        public Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct)
        {
            Completions.Add(completion);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnLedgerEntry>>([]);

        public Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct) =>
            Task.FromResult<TurnLedgerEntry?>(null);

        public Task<TurnLedgerPage> ListPageAsync(string sessionId, long beforeTurnSequence, int maxTurns, CancellationToken ct) =>
            Task.FromResult(new TurnLedgerPage([], false, 0));

        public Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct) => Task.CompletedTask;


        public Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnLedgerHit>>([]);
    }

    /// <summary>
    /// Persists nothing: every message the loop writes is kept in memory with the seq a real store would have assigned,
    /// so a test can assert what did and did not reach storage without a database.
    /// </summary>
    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<(string SessionId, ChatMessage Message)> Appended { get; } = [];

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
        {
            Appended.Add((sessionId, message));
            return Task.FromResult((long)Appended.Count);
        }

        public async IAsyncEnumerable<StoredMessage> ReadAsync(
            string sessionId,
            long fromSeq,
            long? toSeq,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult((long)Appended.Count);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);
    }

    /// <summary>Answers each turn with the scripted message and records every request it was handed; a hung stream
    /// yields nothing and waits for its own cancellation, the shape of a turn cancelled in flight.</summary>
    private sealed class ScriptedProvider(Func<int, ChatMessage> script) : IChatProvider
    {
        /// <summary>When set, no delta ever arrives: the stream parks on its token until the turn is cancelled.</summary>
        public bool HangUntilCancelled { get; set; }

        /// <summary>Completes when a hanging stream has parked, so a test knows cancellation now has a turn to stop.</summary>
        public TaskCompletionSource? NoteHung { get; set; }

        /// <summary>The call whose content arrives and then dies: its text is yielded and the stream throws instead of
        /// completing, the shape of a provider that failed after it had already spoken.</summary>
        public int? FailAfterStreamingCall { get; set; }

        private int calls;

        public List<ChatRequest> Requests { get; } = [];

        public int Calls => Volatile.Read(ref calls);

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            var index = Interlocked.Increment(ref calls);
            if (HangUntilCancelled)
            {
                NoteHung?.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                yield break;
            }

            var message = script(index);
            foreach (var block in message.Content)
            {
                if (block is TextBlock text)
                {
                    yield return new TextDelta(text.Text);
                }
            }

            if (index == FailAfterStreamingCall)
            {
                throw new ProviderException("boom", retryable: false);
            }

            yield return new MessageCompleted(message, TokenUsage.Empty, StopReason.EndTurn);
        }
    }

    /// <summary>
    /// A <c>read</c> that never opens a file: the single-path call answers in the production read answer's own shape - a
    /// header line, then every line prefixed with its right-aligned number and the arrow - carrying a spec's frontmatter;
    /// a multi-file call with the interleaved shape the real tool produces for two files. The evidence is asserted against
    /// files that do not exist, so this tool is the proof that no path was ever resolved onto disk.
    /// </summary>
    private sealed class ScriptedReadTool : ToolBase
    {
        public const string SpecId = "forge-loop-test-spec";

        public ScriptedReadTool() : base("read", "reads nothing in this test", "{}", ToolSideEffect.ReadOnly)
        {
        }

        protected override ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
        {
            var shaped = arguments.TryGetProperty("paths", out _);
            return ValueTask.FromResult(new ToolResult(
                shaped
                    ? "a/x.md: 3 lines\n1→one\n2→two\n3→three\n\na/y.md: 2 lines\n1→un\n2→deux"
                    : $"spec.md: 5 lines\n1→---\n2→id: {SpecId}\n3→---\n4→\n5→body text"));
        }
    }

    private static ChatMessage Says(string text) => new(MessageRole.Assistant, [new TextBlock(text)]);

    private static string RequestText(ChatRequest request) => string.Concat(
        request.Messages.SelectMany(static message => message.Content.OfType<TextBlock>().Select(static block => block.Text)));

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<List<AgentEvent>> RunTurnAsync(
        AgentHost host,
        AgentInstance root,
        ScriptedProvider provider,
        string text,
        CancellationToken ct)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, text, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    private (AgentHost Host, AgentInstance Root, ScriptedProvider Provider, RecordingLedger Ledger) Host(
        Func<int, ChatMessage> script,
        RecordingLedger? ledger = null,
        ISessionStore? store = null,
        IEnumerable<ITool>? tools = null)
    {
        var provider = new ScriptedProvider(script);
        var actualLedger = ledger ?? new RecordingLedger();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools ?? [],
            Layers = new PromptLayers("sys", null, null),
            Store = store,
            TurnLedger = actualLedger,
        });
        return (host, host.CreateRoot("session-ledger", workspace), provider, actualLedger);
    }

    [Fact]
    public async Task A_turn_settles_one_row_with_the_answer_it_ended_on()
    {
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer));

        var events = await RunTurnAsync(host, root, provider, "do the thing", TestContext.Current.CancellationToken);

        Assert.Single(ledger.Begins);
        Assert.Equal("do the thing", ledger.Begins[0].TaskText);
        Assert.Equal("Root", ledger.Begins[0].AgentKind);
        Assert.Equal(0, ledger.Begins[0].AgentDepth);
        var completion = Assert.Single(ledger.Completions);
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
        Assert.Equal("The task is done and verified.", completion.AnswerText);
        Assert.Equal("The task is done and verified.", completion.ConclusionText);
        // The answer is delivered exactly as the model wrote it: it is the reply, not a rewritten one.
        Assert.Contains(
            "The task is done and verified.",
            string.Concat(events.OfType<AgentTextDelta>().Select(static delta => delta.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_reply_is_recorded_as_it_stands_and_sends_no_nudge()
    {
        var store = new RecordingSessionStore();
        var (host, root, provider, ledger) = Host(
            index => Says($"plain answer {index}"),
            store: store);

        await RunTurnAsync(host, root, provider, "turn one", TestContext.Current.CancellationToken);
        await RunTurnAsync(host, root, provider, "turn two", TestContext.Current.CancellationToken);

        // A reply is free-form: both turns settle, both answers are the model's own words, and nothing judges their shape.
        Assert.Equal(
            [("plain answer 1", TurnLedgerStatus.Settled), ("plain answer 2", TurnLedgerStatus.Settled)],
            ledger.Completions.Select(static completion => (completion.AnswerText, completion.Status)));
        // Nothing is injected into a later request on account of how an earlier one ended.
        Assert.DoesNotContain("TURN COMPLETION:", RequestText(provider.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TURN COMPLETION:",
            string.Concat(store.Appended.SelectMany(static appended => appended.Message.Content.OfType<TextBlock>().Select(static block => block.Text))),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_the_provider_fails_settles_its_row_instead_of_leaving_it_pending()
    {
        var (host, root, provider, ledger) = Host(_ => throw new ProviderException("boom", retryable: false));

        var events = await RunTurnAsync(host, root, provider, "turn one", TestContext.Current.CancellationToken);

        Assert.Single(ledger.Begins);
        Assert.Single(events.OfType<AgentFailed>());
        var completion = Assert.Single(ledger.Completions);
        // The turn failed before its first delta, so the text it produced is empty - a harness-terminated turn is never
        // recorded from whatever text the instance happened to hold.
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
        Assert.Equal(string.Empty, completion.AnswerText);
        Assert.Equal(string.Empty, completion.ConclusionText);
    }

    [Fact]
    public async Task A_run_that_streamed_one_request_and_died_in_the_next_settles_with_the_whole_runs_text()
    {
        // Request one streams words and calls a tool; request two streams its own words and then dies, and its BeginText
        // has already wiped the display buffer a settle could read. The row carries both requests' words, in order: a
        // settle reading the current request's buffer, or the instance's committed answer (the display fallback, which
        // still holds request one's), would come back with one half of them only.
        var read = new ScriptedReadTool();
        var first = new ChatMessage(
            MessageRole.Assistant,
            [new TextBlock("Reading the spec first."), new ToolCallBlock("c1", "read", "{\"path\":\"missing/spec.md\"}")]);
        var (host, root, provider, ledger) = Host(index => index == 1 ? first : Says("Still going."), tools: [read]);
        provider.FailAfterStreamingCall = 2;

        var events = await RunTurnAsync(host, root, provider, "read then fail", TestContext.Current.CancellationToken);

        Assert.Single(events.OfType<AgentFailed>());
        var completion = Assert.Single(ledger.Completions);
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
        Assert.Equal("Reading the spec first.Still going.", completion.AnswerText);
        Assert.Equal("Reading the spec first.Still going.", completion.ConclusionText);
    }

    [Fact]
    public async Task A_begin_that_never_lands_still_settles_under_a_number_the_store_picks()
    {
        // The F2 path: both begins fail - the turn's own and the settle's one retry - so the row is settled with a null
        // number and the store files it under the session's next one itself. The row is never lost.
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer));
        ledger.BeginFailures = int.MaxValue;

        var events = await RunTurnAsync(host, root, provider, "turn one", TestContext.Current.CancellationToken);

        Assert.Empty(ledger.Begins);
        Assert.Empty(events.OfType<AgentFailed>());
        var completion = Assert.Single(ledger.Completions);
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
        Assert.Null(completion.TurnSequence);
        Assert.Equal("The task is done and verified.", completion.ConclusionText);
        // The begin record rides the settle, so the store that has to file the row itself still gets the turn's
        // identity - task text, owner - instead of an anonymous row.
        Assert.NotNull(completion.Opened);
        Assert.Equal("turn one", completion.Opened.TaskText);
        Assert.Equal("Root", completion.Opened.AgentKind);
        Assert.Equal(0, completion.Opened.AgentDepth);
    }

    [Fact]
    public async Task A_begin_that_fails_once_is_retried_by_the_settle_so_the_row_keeps_its_task_text()
    {
        // The begin's one retry is the settle's own: it files the pending row the first write lost, so the settled row
        // carries the task text and its owner rather than being born settled with neither.
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer));
        ledger.BeginFailures = 1;

        await RunTurnAsync(host, root, provider, "the task text", TestContext.Current.CancellationToken);

        var begin = Assert.Single(ledger.Begins);
        Assert.Null(begin.TurnSequence);
        Assert.Equal("the task text", begin.TaskText);
        Assert.Equal("Root", begin.AgentKind);
        Assert.Equal(0, begin.AgentDepth);
        var completion = Assert.Single(ledger.Completions);
        // The completion is filed under the number the retried begin answered, not a null the store must repair.
        Assert.Equal(1, completion.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
    }

    [Fact]
    public async Task Consecutive_turns_of_one_session_get_increasing_numbers_from_the_store()
    {
        // The F1 path: a caller that supplies no TurnSequence asks the store, so any store - not just the SQLite one -
        // hands out honest monotonic numbers, one per turn of the session.
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer));

        await RunTurnAsync(host, root, provider, "turn one", TestContext.Current.CancellationToken);
        await RunTurnAsync(host, root, provider, "turn two", TestContext.Current.CancellationToken);
        await RunTurnAsync(host, root, provider, "turn three", TestContext.Current.CancellationToken);

        Assert.Null(root.TurnSequence);
        Assert.Equal(
            [1L, 2L, 3L],
            ledger.Completions.Select(static completion => completion.TurnSequence));
        Assert.Equal(3, ledger.Completions.Select(static completion => completion.TurnSequence).Distinct().Count());
    }

    [Fact]
    public async Task File_evidence_comes_from_arguments_and_results_without_opening_any_file()
    {
        var read = new ScriptedReadTool();
        var firstAnswer = new ChatMessage(
            MessageRole.Assistant,
            [
                new TextBlock("Reading the specs first."),
                new ToolCallBlock("c1", "read", "{\"path\":\"missing/spec.md\"}"),
                new ToolCallBlock("c2", "read", "{\"paths\":[\"a/x.md\",\"a/y.md\"]}"),
                new ToolCallBlock("c3", "spec_get", "{\"id\":\"forge-core\"}"),
            ]);
        var (host, root, provider, ledger) = Host(
            index => index == 1 ? firstAnswer : Says(ReceivedAnswer),
            tools: [read]);

        await RunTurnAsync(host, root, provider, "read the specs", TestContext.Current.CancellationToken);

        var completion = Assert.Single(ledger.Completions);
        // The named file does not exist anywhere - not in the workspace, not as an absolute path - and the path is still
        // pinned, which is only possible because the evidence never opened it.
        Assert.False(File.Exists(Path.Combine(workspace, "missing/spec.md")));
        Assert.Contains("missing/spec.md", completion.Evidence.ReadFiles);
        Assert.Equal(
            ["a/x.md", "a/y.md"],
            completion.Evidence.ReadFiles.Where(static file => file.StartsWith("a/", StringComparison.Ordinal)));
        // A turn that only read files changed none.
        Assert.Empty(completion.Evidence.ChangedFiles);
        // The single-file read's own result text carries the spec id; the multi-file result is not frontmatter-scanned,
        // so it contributes paths but no id; spec_get's argument is pinned as given.
        Assert.Equal(["forge-core", ScriptedReadTool.SpecId], completion.Evidence.Specs);
        Assert.False(File.Exists(Path.Combine(workspace, "a/x.md")));
    }
    [Fact]
    public async Task A_turn_cancelled_before_its_first_delta_is_recorded_missing_from_no_text_of_its_own()
    {
        // Turn one ends received on a templated answer; turn two is cancelled in flight, before any delta. The row it
        // leaves is the harness's verdict on the harness's own reason - missing, with the turn's own (empty) text - and
        // the previous turn's answer never leaks into it the way the display fallback would have carried it.
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer));
        await RunTurnAsync(host, root, provider, "turn one", TestContext.Current.CancellationToken);
        provider.HangUntilCancelled = true;
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.NoteHung = hung;

        using var cancellation = new CancellationTokenSource();
        var run = Task.Run(() => RunTurnAsync(host, root, provider, "turn two", cancellation.Token));
        await hung.Task;
        cancellation.Cancel();
        await run;

        Assert.Equal(2, ledger.Completions.Count);
        Assert.Equal(TurnLedgerStatus.Settled, ledger.Completions[0].Status);
        var cancelled = ledger.Completions[1];
        Assert.Equal(2, cancelled.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, cancelled.Status);
        Assert.Equal(string.Empty, cancelled.AnswerText);
        Assert.Equal(string.Empty, cancelled.ConclusionText);
        // No row carries turn one's text twice: only the row of the turn that wrote it.
        Assert.Equal(1, ledger.Completions.Count(row => row.AnswerText == ReceivedAnswer));
    }

    [Fact]
    public async Task A_cancellation_arriving_while_the_begin_is_pending_still_settles_the_row()
    {
        // The turn's token goes off while the store's begin is still in flight. The begin is not tied to the token, so
        // the row opens anyway, and the cancellation settles it - under the number the begin filed.
        var store = new RecordingSessionStore();
        var (host, root, provider, ledger) = Host(_ => Says(ReceivedAnswer), store: store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.NoteBeginEntered = entered;
        ledger.HoldBegin = release;

        using var cancellation = new CancellationTokenSource();
        var run = Task.Run(() => RunTurnAsync(host, root, provider, "the task", cancellation.Token));
        await entered.Task;
        cancellation.Cancel();
        release.TrySetResult();
        await run;

        // The opening message was persisted before the row was opened, and the row still settled.
        var opening = Assert.Single(store.Appended);
        Assert.Equal(MessageRole.User, opening.Message.Role);
        Assert.Equal("the task", Assert.IsType<TextBlock>(Assert.Single(opening.Message.Content)).Text);
        var completion = Assert.Single(ledger.Completions);
        Assert.Equal(1, completion.TurnSequence);
        Assert.Equal(TurnLedgerStatus.Settled, completion.Status);
        Assert.Equal(string.Empty, completion.AnswerText);
    }
}
