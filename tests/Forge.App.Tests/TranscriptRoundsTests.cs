using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

// Regressions from the round-container redesign: what a reopened session must still show (the reasoning of every turn), how a
// sub-agent round hosts its task row, and how the ask_user card and the tool card grid present themselves.
public sealed class TranscriptRoundsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static TranscriptViewModel MainTranscript() => new(collapseOlderTurns: false);

    /// <summary>A stored assistant message exactly as a provider reports it: reasoning, then the answer, then the calls.</summary>
    private static StoredMessage Assistant(long seq, params ContentBlock[] content) =>
        new(seq, new ChatMessage(MessageRole.Assistant, content), null, At);

    /// <summary><paramref name="turns"/> stored turns of a question and its answer each, one message per seq.</summary>
    private static List<StoredMessage> Turns(int turns)
    {
        var messages = new List<StoredMessage>(turns * 2);
        for (var turn = 1; turn <= turns; turn++)
        {
            messages.Add(new StoredMessage(messages.Count + 1, ChatMessage.User($"question {turn}"), null, At));
            messages.Add(new StoredMessage(messages.Count + 1, ChatMessage.Assistant($"answer {turn}"), null, At));
        }

        return messages;
    }

    /// <summary>One metrics row: a completed turn numbered <paramref name="turn"/>, ending at <paramref name="lastSeq"/>.</summary>
    private static TurnMetric Row(int turn, long lastSeq) =>
        new("s", turn, At, At.AddSeconds(3), TurnMetricStatus.Completed, LastSeq: lastSeq);

    [Fact]
    public void A_reopened_session_shows_the_thinking_rows_of_every_turn()
    {
        var transcript = MainTranscript();
        var call = new ToolCallBlock("t1", "read", "{\"path\":\"a.cs\"}");
        var messages = new[]
        {
            new StoredMessage(0, ChatMessage.User("look at a.cs"), null, At),
            Assistant(1, new ThinkingBlock("first thought"), new TextBlock("here it is"), call),
            new StoredMessage(2, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("t1", [new TextBlock("ok")])]), null, At),
        };

        TranscriptReplay.Apply(transcript, messages, []);

        var rows = transcript.FlatRows();
        var thinking = Assert.IsType<TurnThinkingViewModel>(rows[1]);
        Assert.Contains("first thought", thinking.Text, StringComparison.Ordinal);

        // The reasoning precedes the answer it produced, exactly as it arrived.
        Assert.IsType<TurnTextViewModel>(rows[2]);
    }

    /// <summary>One ledger row: a turn the shadow ledger settled, ending at <paramref name="answerSeq"/>.</summary>
    private static TurnLedgerEntry LedgerRow(long turn, long answerSeq) =>
        new(
            "s",
            turn,
            TurnLedgerStatus.Settled,
            "the task text",
            "the answer as shown",
            "the parsed conclusion",
            [],
            [],
            [],
            turn * 2 - 1,
            answerSeq,
            answerSeq,
            null,
            null,
            "Root",
            0,
            false,
            At,
            At.AddSeconds(3));

    [Fact]
    public void A_ledger_row_alone_gives_its_replayed_card_the_turn_entry_button()
    {
        var transcript = MainTranscript();
        var messages = Turns(2);

        // No turn_metrics row exists at all - the database predates the metrics table - and the ledger row alone still
        // has to prove which card its turn was, paired by its own answer_seq.
        TranscriptReplay.Apply(transcript, messages, [], [LedgerRow(turn: 1, answerSeq: 2)]);

        var cards = transcript.Rounds.Select(round => round.Card!).ToArray();
        Assert.True(cards[0].HasLedger);
        Assert.False(cards[1].HasLedger);
    }

    [Fact]
    public void An_answer_seq_two_rows_share_pairs_no_card()
    {
        var transcript = MainTranscript();
        var messages = Turns(2);

        // Two rows claim the same ending position: no card can be proven to be the one either of them ended on, so
        // neither gets the button - the replay refuses the same ambiguity FindByAnswerSeqAsync refuses, instead of
        // letting the last row written win the pairing.
        TranscriptReplay.Apply(transcript, messages, [], [LedgerRow(turn: 1, answerSeq: 2), LedgerRow(turn: 2, answerSeq: 2)]);

        var cards = transcript.Rounds.Select(round => round.Card!).ToArray();
        Assert.False(cards[0].HasLedger);
        Assert.False(cards[1].HasLedger);
    }

    [Fact]
    public void A_sub_agent_round_holds_its_task_row_and_its_turn_together()
    {
        // A sub-agent transcript folds older turns, but the task row and the turn it opened belong to one round: two rounds
        // would cut the axis between the task's node and the turn's first node. The turn itself is never a row — the rows
        // are its blocks, and its numbers ride the round-end rule.
        var transcript = new TranscriptViewModel(collapseOlderTurns: true);
        transcript.AddUserMessage("summarise the diff", At);
        var card = transcript.BeginTurn(At);
        card.AppendText("done");

        var round = Assert.Single(transcript.Rounds);
        Assert.Equal(2, round.Rows.Count);
        Assert.IsType<UserMessageViewModel>(round.Rows[0]);
        Assert.Same(card.Blocks[0], round.Rows[1]);
        Assert.Same(card, round.Card);
    }

    [Fact]
    public void A_running_command_shows_its_streamed_output_on_the_card()
    {
        var row = new ToolRowViewModel("bash", "{\"command\":\"dotnet build\"}");
        Assert.Equal("dotnet build", row.CardBody);

        row.AttachLive(new LiveToolViewModel("root", () => "main", new ToolCall("t1", "bash", "{}"), At, stopCall: null));
        row.Live!.RecordTail("first line\nsecond line\n");
        row.Live!.RefreshTail(50);
        Assert.Equal("second line", row.CardBody);
    }

    [Fact]
    public void A_question_card_offers_every_option_and_marks_the_chosen_one()
    {
        const string arguments = """
            {"questions":[{"header":"Scope","question":"Which files?","options":[
              {"label":"Only the diff","description":"Review what changed"},
              {"label":"The whole file","description":"Read every line"}]}]}
            """;
        var row = new ToolRowViewModel("ask_user", arguments);
        row.Complete(new ToolResult([new TextBlock("Scope: Only the diff")]));

        Assert.True(row.IsQuestionCard);
        var question = Assert.Single(row.QuestionItems);
        Assert.Equal("Which files?", question.Prompt);
        Assert.Equal(2, question.Options.Count);
        Assert.True(question.Options[0].IsChosen);
        Assert.False(question.Options[1].IsChosen);

        // The tool joins the picked labels with ", ", so a comma-separated answer must mark every pick.
        var multi = new ToolRowViewModel(
            "ask_user",
            """{"questions":[{"header":"Files","question":"Which?","multiSelect":true,"options":[{"label":"a.cs"},{"label":"b.cs"},{"label":"c.cs"}]}]}""");
        multi.Complete(new ToolResult([new TextBlock("Files: a.cs, c.cs")]));
        var picks = Assert.Single(multi.QuestionItems).Options;
        Assert.True(picks[0].IsChosen);
        Assert.False(picks[1].IsChosen);
        Assert.True(picks[2].IsChosen);
        Assert.Equal("Read every line", question.Options[1].Description);
    }

    /// <summary>
    /// The metrics table can skip a turn (the recorder may drop a row) or repeat one (a live counter that ran ahead of the
    /// stored user messages). A replay still labels its rounds 1..N in reading order: adopting the row's own number card by
    /// card is what made two rounds of one page both read "第 4 轮" and the numbers between them disappear.
    /// </summary>
    [Fact]
    public void Rounds_stay_one_sequence_even_when_the_metrics_rows_skip_and_repeat()
    {
        var transcript = MainTranscript();
        TranscriptReplay.Apply(
            transcript,
            Turns(4),
            [Row(1, lastSeq: 2), Row(3, lastSeq: 4), Row(4, lastSeq: 6), Row(4, lastSeq: 8)]);

        Assert.Equal([1, 2, 3, 4], transcript.Rounds.Select(round => round.Card!.Round));
        Assert.Equal<int?>(
            [1, 2, 3, 4],
            transcript.Rounds.Select(round => Assert.IsType<UserMessageViewModel>(round.Rows[0]).Round));
    }

    /// <summary>
    /// The count a page is seeded with is an approximation - stored user messages exist that opened no live turn (receipts,
    /// interjections). When every paired row agrees on how many turns preceded the page, the page moves to that number in one
    /// step, and the turn after it continues from the moved sequence.
    /// </summary>
    [Fact]
    public void Agreeing_rows_move_the_page_to_where_its_turns_really_were()
    {
        var transcript = MainTranscript();
        transcript.SeedRoundCounter(2);
        TranscriptReplay.Apply(transcript, Turns(2), [Row(2, lastSeq: 2), Row(3, lastSeq: 4)]);

        Assert.Equal([2, 3], transcript.Rounds.Select(round => round.Card!.Round));

        transcript.AddUserMessage("next", At);
        Assert.Equal(4, Assert.IsType<UserMessageViewModel>(transcript.Rounds[^1].Rows[0]).Round);
    }

    [Fact]
    public void A_reopened_turn_takes_its_numbers_from_the_metrics_row_even_when_the_clocks_drifted()
    {
        var transcript = MainTranscript();
        var at = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        transcript.AddUserMessage("hi", at);
        var card = transcript.BeginTurn(at);
        card.AppendText("hello");
        card.AddUsage(new Forge.Abstractions.TokenUsage(10, 20, 0, 0, 0), Forge.Abstractions.StopReason.EndTurn);
        transcript.EndTurn(TimeSpan.FromSeconds(9), null);

        // The stored turn started behind a checkpoint, so its clock sits minutes away from the message the replay stamps.
        // Pairing is by the row's seq, so the distance never enters the decision — the row's numbers arrive whole.
        card.SeedReplayMetrics(
            new Forge.Core.Storage.TurnMetric(
                "s", 1, at.AddMinutes(-4), at.AddMinutes(-4) + TimeSpan.FromSeconds(9), Forge.Core.Storage.TurnMetricStatus.Completed,
                "openai", "gpt-5", null, 10, 20, 0, 0, 0, 1, TimeSpan.FromSeconds(4)));

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.FlatRows()[^1]);
        Assert.Contains("gpt-5", divider.Line, StringComparison.Ordinal);
        Assert.Contains("tok/s", divider.Line, StringComparison.Ordinal);
        Assert.Contains("9.0 秒", divider.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_turn_shows_the_working_row_only_while_nothing_is_in_flight()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("go", At);
        var card = transcript.BeginTurn(At);

        // The turn started and nothing else arrived yet: the row is there.
        Assert.Contains(transcript.FlatRows(), row => row is TurnWorkingViewModel);

        // Reasoning that just arrived speaks for the turn: the row leaves.
        card.AppendThinking("thinking about it");
        Assert.DoesNotContain(transcript.FlatRows(), row => row is TurnWorkingViewModel);

        card.AppendText("answer");
        Assert.DoesNotContain(transcript.FlatRows(), row => row is TurnWorkingViewModel);

        // The turn ends: nothing is left to announce.
        transcript.EndTurn(TimeSpan.FromSeconds(2), null);
        Assert.DoesNotContain(transcript.FlatRows(), row => row is TurnWorkingViewModel);
    }

    [Fact]
    public void A_typed_answer_gets_an_option_card_of_its_own()
    {
        var row = new ToolRowViewModel(
            "ask_user",
            """{"questions":[{"header":"Scope","question":"Which files?","options":[{"label":"Only the diff"},{"label":"Everything"}]}]}""");
        row.Complete(new ToolResult([new TextBlock("Scope: the new parser only")]));

        var question = Assert.Single(row.QuestionItems);
        Assert.Equal(3, question.Options.Count);
        Assert.False(question.Options[0].IsChosen);
        Assert.True(question.Options[2].IsChosen);
        Assert.Equal("the new parser only", question.Options[2].Label);
    }

    [Fact]
    public void The_question_card_keeps_only_the_questions_the_user_answered_or_wants_to_discuss()
    {
        const string arguments = """
            {"questions":[
              {"header":"Scope","question":"Which files?","options":[{"label":"Only the diff"},{"label":"Everything"}]},
              {"header":"Style","question":"Which style?","options":[{"label":"Concise"},{"label":"Detailed"}]}]}
            """;

        // Still waiting: nothing is hidden yet, because nothing has been decided.
        var pending = new ToolRowViewModel("ask_user", arguments);
        Assert.Equal(2, pending.QuestionItems.Count);

        // Answered: the second question was skipped, which is not an answer and does not belong in the record.
        var row = new ToolRowViewModel("ask_user", arguments);
        row.Complete(new ToolResult([new TextBlock("Scope: Only the diff\nStyle: (skipped)")]));
        var answered = Assert.Single(row.QuestionItems);
        Assert.Equal("Scope", answered.Header);
        Assert.True(answered.Options[0].IsChosen);

        // Asked to discuss: the question stays and reads as still to be discussed, the note riding along.
        var discussed = new ToolRowViewModel("ask_user", arguments);
        discussed.Complete(new ToolResult([new TextBlock("Scope: Only the diff\nStyle: (discussion requested) pick the tone with me")]));
        Assert.Equal(2, discussed.QuestionItems.Count);
        var question = discussed.QuestionItems[1];
        Assert.StartsWith(Loc.Instance[QuestionsStrings.TranscriptDiscussingKey], question.Answer, StringComparison.Ordinal);
        Assert.Contains("pick the tone with me", question.Answer, StringComparison.Ordinal);

        // Dismissed: no question was answered at all, so the card keeps only its status.
        var dismissed = new ToolRowViewModel("ask_user", arguments);
        dismissed.Complete(new ToolResult([new TextBlock("The user dismissed the questions without answering.")]));
        Assert.Empty(dismissed.QuestionItems);
        Assert.Equal(Loc.Instance[QuestionsStrings.TranscriptDismissedKey], dismissed.QuestionStatusText);
    }

    [Fact]
    public void A_long_result_stays_capped_until_the_user_asks_for_all_of_it()
    {
        var row = new ToolRowViewModel("bash", "{\"command\":\"dir\"}");
        row.Complete(new ToolResult([new TextBlock(string.Join('\n', Enumerable.Range(0, 40).Select(n => "line " + n)))]));
        Assert.True(row.CanExpandResult);
        Assert.Equal(200, row.ResultMaxHeight);
        row.ToggleResultCommand.Execute(null);
        Assert.True(double.IsPositiveInfinity(row.ResultMaxHeight));
    }

    [Fact]
    public void The_open_card_body_belongs_to_the_row_it_was_clicked_in()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("run everything", At);
        var card = transcript.BeginTurn(At);
        for (var i = 0; i < 6; i++)
        {
            var call = new ToolCall($"t{i}", "read", $"{{\"path\":\"f{i}.cs\"}}");
            var row = card.AddTool(call);
            row.Complete(new ToolResult([new TextBlock("ok")]));
        }

        var group = Assert.IsType<TurnToolGroupViewModel>(Assert.Single(card.Blocks));
        Assert.Equal(6, group.Tools.Count);
        Assert.DoesNotContain(group.Items, item => item is ToolOpenViewModel);

        // Opening the second card of the first row puts the body directly after that row's fourth card, not at the end.
        var second = group.Tools[1];
        second.IsExpanded = true;
        var body = Assert.IsType<ToolOpenViewModel>(group.Items[4]);
        Assert.Same(second, body.Tool);

        // Only one card is open at a time: opening another closes the first and moves the body under the second row.
        var fifth = group.Tools[4];
        fifth.IsExpanded = true;
        Assert.False(second.IsExpanded);
        Assert.Equal(7, group.Items.Count);
        var moved = Assert.IsType<ToolOpenViewModel>(group.Items[^1]);
        Assert.Same(fifth, moved.Tool);
    }
}
