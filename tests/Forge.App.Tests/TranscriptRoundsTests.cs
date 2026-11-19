using Forge.Abstractions;
using Forge.App.ViewModels;
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

        TranscriptReplay.Apply(transcript, messages);

        var rows = transcript.FlatRows();
        var thinking = Assert.IsType<TurnThinkingViewModel>(rows[1]);
        Assert.Contains("first thought", thinking.Text, StringComparison.Ordinal);

        // The reasoning precedes the answer it produced, exactly as it arrived.
        Assert.IsType<TurnTextViewModel>(rows[2]);
    }

    [Fact]
    public void A_sub_agent_round_holds_its_task_row_and_its_turn_together()
    {
        // A sub-agent transcript collapses older turns, but the task row and the turn it opened belong to one round: two rounds
        // would cut the axis between the task's node and the turn's first node.
        var transcript = new TranscriptViewModel(collapseOlderTurns: true);
        transcript.AddUserMessage("summarise the diff", At);
        var card = transcript.BeginTurn(At);
        card.AppendText("done");

        var round = Assert.Single(transcript.Rounds);
        Assert.Equal(3, round.Rows.Count);
        Assert.IsType<UserMessageViewModel>(round.Rows[0]);
        Assert.Same(card, round.Rows[1]);
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
        transcript.SeedTurnMetrics(
        [
            new Forge.Core.Storage.TurnMetric(
                "s", 1, at.AddMinutes(-4), at.AddMinutes(-4) + TimeSpan.FromSeconds(9), Forge.Core.Storage.TurnMetricStatus.Completed,
                "openai", "gpt-5", null, 10, 20, 0, 0, 0, 1, TimeSpan.FromSeconds(4)),
        ]);

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.FlatRows()[^1]);
        Assert.Contains("gpt-5", divider.Line, StringComparison.Ordinal);
        Assert.Contains("tok/s", divider.Line, StringComparison.Ordinal);
        Assert.Contains("9.0 秒", divider.Line, StringComparison.Ordinal);
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
