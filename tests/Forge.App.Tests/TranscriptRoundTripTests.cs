using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The live transcript and the reopened one must agree on every round's divider, whatever the stored stream happens to
/// contain. Stored user messages exist that never opened a live turn — a session reply receipt, a steering message — and
/// each one used to shift the replay's counted round number past the <c>turn_metrics</c> key, so every round after the
/// first read "—" for its clock, model and step. The rows now carry <c>last_seq</c>, the seq of the last message the turn
/// persisted, and the replay pairs by that exact position instead of by count: a drift of any size cannot move it. This
/// test drives the pairing through the one funnel every loading path shares (<see cref="TranscriptReplay"/>), receipt
/// and steering included, and fails on any live/replay divergence — not on today's bug, on the class of it.
/// </summary>
public sealed class TranscriptRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset At0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-round-trip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then. Clearing the global
            // pool here would race every concurrently running store (the suite's flake window), for a temp dir.
        }
    }

    [Fact]
    public async Task Every_round_shows_the_clock_model_and_step_it_ran_with_despite_receipts_and_steering()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);

        // The live transcript: three turns on one model, with a receipt arriving between turns (its own card, no turn)
        // and a steering message delivered inside turn two (a notice in the running card, never a round of its own).
        var live = new TranscriptViewModel(collapseOlderTurns: false);
        var liveCards = new List<TurnCardViewModel>();

        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        var turn1Answer = await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);
        live.AddUserMessage("first question", At0);
        var card1 = live.BeginTurn(At0);
        card1.NoteModel("probe", "glm-5.3", ThinkingEffort.High);
        card1.AppendText("first answer");
        live.EndTurn(TimeSpan.FromSeconds(96.2), null);
        liveCards.Add(card1);

        // The receipt: stored as a user message (that is what the next turn reads), shown live as a card that opens no turn.
        var receipt = "[会话回复 #probe · 来自 worker · 状态:completed · 任务:thing]\n改动: 1 个文件 (+2/−0)\n报告: done";
        await store.AppendAsync(root.Id, ChatMessage.User(receipt), null, CancellationToken.None);
        live.AddSessionReply(receipt, At0.AddSeconds(30));

        // Turn two runs to completion; its last persisted message is the assistant after the steering, and that seq is
        // the position its row must name.
        await store.AppendAsync(root.Id, ChatMessage.User("second question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("[插话] more detail"), null, CancellationToken.None);
        var turn2Answer = await store.AppendAsync(root.Id, ChatMessage.Assistant("steered answer"), null, CancellationToken.None);
        live.AddUserMessage("second question", At0.AddMinutes(2));
        var card2 = live.BeginTurn(At0.AddMinutes(2));
        card2.NoteModel("probe", "glm-5.3", ThinkingEffort.Medium);
        card2.AppendText("second answer");
        live.EndTurn(TimeSpan.FromSeconds(41), null);
        liveCards.Add(card2);

        await store.AppendAsync(root.Id, ChatMessage.User("third question"), null, CancellationToken.None);
        var turn3Answer = await store.AppendAsync(root.Id, ChatMessage.Assistant("third answer"), null, CancellationToken.None);
        live.AddUserMessage("third question", At0.AddMinutes(4));
        var card3 = live.BeginTurn(At0.AddMinutes(4));
        card3.NoteModel("probe", "model-a", ThinkingEffort.Off);
        card3.AppendText("third answer");
        live.EndTurn(TimeSpan.FromSeconds(7), null);
        liveCards.Add(card3);

        // What the live turn ends recorded: the row each replayed round must take its divider from, each naming the seq
        // of the turn's own last message — the exact position a recount cannot shift.
        var streams = new[] { TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(7) };
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id, 1, At0, At0.AddSeconds(96.2), TurnMetricStatus.Completed,
                "probe", "glm-5.3", Effort: ThinkingEffort.High, StreamElapsed: streams[0],
                LastSeq: turn1Answer),
            CancellationToken.None);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id, 2, At0.AddMinutes(2), At0.AddMinutes(2).AddSeconds(41), TurnMetricStatus.Completed,
                "probe", "glm-5.3", Effort: ThinkingEffort.Medium, StreamElapsed: streams[1],
                LastSeq: turn2Answer),
            CancellationToken.None);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id, 3, At0.AddMinutes(4), At0.AddMinutes(4).AddSeconds(7), TurnMetricStatus.Completed,
                "probe", "model-a", Effort: ThinkingEffort.Off, StreamElapsed: streams[2],
                LastSeq: turn3Answer),
            CancellationToken.None);

        // The reopen: one read, one replay — the same funnel every loading path goes through.
        var load = await TranscriptReplay.ReadPageAsync(store, metrics, root.Id, long.MaxValue, 100, CancellationToken.None);
        var (replayed, _) = TranscriptReplay.ReplayPage(load);

        // The receipt draws as the reply card it is and opens no turn; the steering message lands as the notice inside
        // turn two, never a card of its own — so the transcript carries exactly the three turns' dividers.
        Assert.Single(replayed.FlatRows().OfType<SessionReplyViewModel>());
        var dividers = replayed.FlatRows().OfType<TurnDividerViewModel>().ToList();
        Assert.Equal(3, dividers.Count);
        Assert.All(dividers, row => Assert.NotNull(row.Card.UserMessage));

        var questions = new[] { "first question", "second question", "third question" };
        for (var turn = 0; turn < 3; turn++)
        {
            var liveCard = liveCards[turn];
            var divider = Assert.Single(dividers, row => row.Card.StreamElapsed == streams[turn]);
            var replayCard = divider.Card;

            // The paired card is the one the actual question opened — not the steering message inside the turn, and not
            // the receipt between the turns.
            Assert.Equal(questions[turn], replayCard.UserMessage!.Text);

            // The recorded turn number is the one the replay adopts — the recount would read 1, 3, 5 here (the receipt
            // and the steering each opened a stored round the live session never had).
            Assert.Equal(turn + 1, replayCard.Round);
            Assert.Equal(turn + 1, replayCard.UserMessage!.Round);

            // The fields the stored messages cannot reconstruct: clock, outcome, model, step, stream time.
            Assert.Equal(liveCard.Elapsed, replayCard.Elapsed);
            Assert.Equal(liveCard.EndStatus, replayCard.EndStatus);
            Assert.Equal(liveCard.ModelLabel, replayCard.ModelLabel);
            Assert.Equal(liveCard.Effort, replayCard.Effort);
            Assert.Equal(streams[turn], replayCard.StreamElapsed);

            // And the line the user reads on the round's divider: the model segment is there, the elapsed is the row's.
            Assert.EndsWith(" · " + liveCard.ModelLabel, divider.Line, StringComparison.Ordinal);
            Assert.Contains(TextFormat.Elapsed(liveCard.Elapsed!.Value), divider.Line);
        }

        // The steering message stayed inside turn two: its text shows as the interjection notice on that turn's card.
        var steered = dividers.Single(row => row.Card.UserMessage!.Text == "second question").Card;
        var notice = Assert.IsType<TurnNoticeViewModel>(Assert.Single(steered.Blocks.OfType<TurnNoticeViewModel>()));
        Assert.Contains("[插话] more detail", notice.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row recorded before the seq column existed names no position, so it pairs with nothing and its round keeps the
    /// replayed "—" — never with a guessed card.
    /// </summary>
    [Fact]
    public async Task A_row_without_a_seq_pairs_with_nothing_and_keeps_reading_unknown()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id, 1, At0, At0.AddSeconds(30), TurnMetricStatus.Completed, "probe", "model-b"),
            CancellationToken.None);

        var load = await TranscriptReplay.ReadPageAsync(store, metrics, root.Id, long.MaxValue, 100, CancellationToken.None);
        var (replayed, _) = TranscriptReplay.ReplayPage(load);

        var card = Assert.Single(replayed.FlatRows().OfType<TurnDividerViewModel>()).Card;
        Assert.Null(card.Elapsed);
        Assert.Null(card.ModelLabel);
        var divider = Assert.Single(replayed.FlatRows().OfType<TurnDividerViewModel>(), row => row.Card == card);
        Assert.Contains("—", divider.Line);
    }

    /// <summary>
    /// The recorder may drop a row (a full queue, a failed write), and the rows left behind then skip a turn number: the
    /// span such a row bounds holds more than one turn's content — turn two's question and turn three's, steering
    /// included. No card in that span can be proven to be the one turn three ended on, so the surviving row stays
    /// unclaimed entirely: the turns still keep their own cards (the merge is the bug), and every divider in the
    /// ambiguous span reads "—" — an unprovable pairing is skipped rather than stamped on the wrong card.
    /// </summary>
    [Fact]
    public async Task A_row_dropped_between_two_turns_keeps_the_turns_apart_and_claims_nothing()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);

        long last = 0;
        for (var turn = 1; turn <= 3; turn++)
        {
            await store.AppendAsync(root.Id, ChatMessage.User($"question {turn}"), null, CancellationToken.None);
            if (turn == 3)
            {
                // Steering inside turn three: with the dropped row's gap the span cannot tell it from an opening
                // question, so it opens a card of its own — and that card must not receive turn three's row.
                await store.AppendAsync(root.Id, ChatMessage.User("[插话] more detail"), null, CancellationToken.None);
            }

            last = await store.AppendAsync(root.Id, ChatMessage.Assistant($"answer {turn}"), null, CancellationToken.None);
            if (turn != 2)
            {
                await metrics.WriteAsync(
                    new TurnMetric(
                        root.Id, turn, At0.AddMinutes(turn), At0.AddMinutes(turn).AddSeconds(turn),
                        TurnMetricStatus.Completed, "probe", $"model-{turn}", LastSeq: last),
                    CancellationToken.None);
            }
        }

        var load = await TranscriptReplay.ReadPageAsync(store, metrics, root.Id, long.MaxValue, 100, CancellationToken.None);
        var (replayed, _) = TranscriptReplay.ReplayPage(load);

        var dividers = replayed.FlatRows().OfType<TurnDividerViewModel>().ToList();

        // Every question — the steering message included, degraded to a card inside the gap span — kept its own round:
        // the two turns were never merged into one card.
        Assert.Equal(4, dividers.Count);
        Assert.Single(dividers, row => row.Card.UserMessage!.Text == "question 2");
        Assert.Single(dividers, row => row.Card.UserMessage!.Text == "question 3");
        Assert.Single(dividers, row => row.Card.UserMessage!.Text == "[插话] more detail");

        // No card in the ambiguous span carries a row: turn one's row is claimed where it belongs, turn three's
        // surviving row is not — it would have landed on the steering card (the old merge) or on turn two's.
        Assert.Equal("probe/model-1", dividers.Single(row => row.Card.UserMessage!.Text == "question 1").Card.DividerModelText);
        Assert.All(
            dividers.Where(row => row.Card.UserMessage!.Text != "question 1"),
            row => Assert.Null(row.Card.ModelLabel));
    }

    /// <summary>
    /// The last turn's row is the one most likely to be dropped (the queue fills under a burst), and a replay must not
    /// fold the numbering back onto the rows that survived: the rowless tail still counted, and the next live turn
    /// continues from the transcript's own count, not from the last surviving row's number.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_row_was_dropped_keeps_the_numbering_the_replay_counted()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);

        await store.AppendAsync(root.Id, ChatMessage.User("question 1"), null, CancellationToken.None);
        var first = await store.AppendAsync(root.Id, ChatMessage.Assistant("answer 1"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("question 2"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("answer 2"), null, CancellationToken.None);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id, 1, At0, At0.AddSeconds(10), TurnMetricStatus.Completed, "probe", "model-1", LastSeq: first),
            CancellationToken.None);

        var load = await TranscriptReplay.ReadPageAsync(store, metrics, root.Id, long.MaxValue, 100, CancellationToken.None);
        var (replayed, _) = TranscriptReplay.ReplayPage(load);

        // Two turns on screen, the rowless one reading unknown — and the count did not fold back: the turn after the
        // last stored one is number three, not a repeat of the row's two.
        var dividers = replayed.FlatRows().OfType<TurnDividerViewModel>().ToList();
        Assert.Equal(2, dividers.Count);
        Assert.Contains("—", Assert.Single(dividers, row => row.Card.UserMessage!.Text == "question 2").Line);
        Assert.Equal(3, replayed.BeginTurn(At0).Round);
    }
}
