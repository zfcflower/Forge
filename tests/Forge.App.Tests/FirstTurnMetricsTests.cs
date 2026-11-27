using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The first turn of a tab is the one no reading taken before the send can describe: the runtime has not built its root
/// agent yet, so the instance that runs the turn is not the one the view sampled (there is none). That is exactly the
/// turn a reopened session shows first, and its metrics row used to carry no <c>last_seq</c> — the position the replay
/// pairs a divider by — so its clock, outcome, rate and model read "—" forever while the token segment (from the
/// message's own usage) still showed. This pins the producer end to end: a real first turn's row names its own last
/// message, and the reopen's replay puts the row back on the card.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FirstTurnMetricsTests
{
    [Fact]
    public Task The_first_turn_of_a_new_tab_records_and_replays_its_own_position() => SharedUi.RunAsync(async () =>
    {
        SqliteTurnMetricsStore metrics = null!;
        TurnMetricsRecorder recorder = null!;
        using var session = await OverloadSession.StartAsync(
            new ScriptedProvider(),
            instruments: directory =>
            {
                metrics = new SqliteTurnMetricsStore(directory);
                recorder = new TurnMetricsRecorder(metrics);
                return new RecorderSources(recorder, metrics);
            });

        try
        {
            await session.RunTurnAsync("ship it", expectRuns: 1);
        }
        finally
        {
            // The recorder's own drain, and the deterministic moment the row has reached the store: it is queued before
            // the turn's completion event fires, so nothing here waits on a clock.
            await recorder.DisposeAsync();
        }

        var sessionId = session.Tab.SessionId;
        var messages = new List<StoredMessage>();
        await foreach (var stored in session.Store.ReadAsync(sessionId, 1, null, CancellationToken.None))
        {
            messages.Add(stored);
        }

        // The row names the seq of the turn's own last message — the root session's first turn included, whose instance
        // did not exist when the user pressed send.
        var row = Assert.Single(await metrics.ListAsync(sessionId, CancellationToken.None));
        Assert.Equal(messages[^1].Seq, row.LastSeq);
        Assert.True(row.LastSeq > 0);
        Assert.Equal(1, row.Turn);

        // What the reopen actually draws: the same pairing funnel every loading path runs. The divider carries the row's
        // clock and stream time instead of the unknown marker, which is the symptom a row without a position produced.
        // (The model segment rides the very same pairing and is asserted where a row can carry one: TranscriptRoundTripTests.)
        var replayed = new TranscriptViewModel(collapseOlderTurns: false);
        TranscriptReplay.Apply(replayed, messages, [row]);
        var divider = Assert.Single(replayed.FlatRows().OfType<TurnDividerViewModel>());
        Assert.Equal(row.EndedAt - row.StartedAt, divider.Card.Elapsed);
        Assert.Equal(row.StreamElapsed, divider.Card.StreamElapsed);
        Assert.Contains(TextFormat.Elapsed(row.EndedAt - row.StartedAt), divider.Line);
    });

    /// <summary>One answer per request, so a turn is the user's message plus a single assistant reply.</summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new TextDelta("done");
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                new TokenUsage(1_000, 200),
                StopReason.EndTurn);
        }
    }

    /// <summary>The app's sources with the turn-metrics table and its real recorder wired in; every other reading is the null object.</summary>
    private sealed class RecorderSources(TurnMetricsRecorder recorder, ITurnMetricsStore metrics) : ISessionInstrumentSources
    {
        private readonly EmptySessionInstrumentSources empty = EmptySessionInstrumentSources.Instance;

        public bool McpFailed() => empty.McpFailed();

        public event Action? McpStateChanged
        {
            add { }
            remove { }
        }

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => empty.ModelMetadata(modelId, providerId);

        public string? PromptCacheTtl(string? providerId) => empty.PromptCacheTtl(providerId);

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null) =>
            empty.TurnHistoryAsync(sessionId, ct, maxTurnExclusive);

        public TurnMetricsRecorder? TurnMetricsRecorder => recorder;

        public ITurnMetricsStore? TurnMetrics => metrics;

        public ITurnLedgerStore? TurnLedger => null;
    }
}
