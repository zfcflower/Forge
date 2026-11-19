using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The transcript's round UI: the user-message subtitle, the divider line rules and the replay seeding that fills in
/// what the stored messages cannot reconstruct. Structural assertions only (no hard-coded language): they hold in both
/// UI languages.
/// </summary>
public sealed class TurnDividerTests
{
    private static readonly DateTimeOffset At0 = new(2026, 11, 18, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset At1 = At0.AddMinutes(12);

    private static TranscriptViewModel MainTranscript() => new(collapseOlderTurns: false);

    // ---------------------------------------------------------------- subtitle

    [Fact]
    public void The_first_user_message_omits_the_gap_segment()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);

        var message = Assert.IsType<UserMessageViewModel>(transcript.Rows[0]);
        Assert.True(message.HasRound);
        Assert.NotNull(message.RoundText);
        // The round marker rides the meta line; the first round has no gap segment, so the meta text is the bare clock.
        Assert.Equal(message.Time, message.MetaText.TrimStart(' ', '·'));
    }

    [Fact]
    public void The_second_user_message_names_its_round_and_the_gap()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        transcript.BeginTurn(At0);
        transcript.EndTurn(TimeSpan.FromSeconds(3), null);
        transcript.AddUserMessage("again", At1);

        var message = Assert.IsType<UserMessageViewModel>(transcript.Rows[^1]);
        Assert.Equal(2, message.Round);
        Assert.NotNull(message.RoundText);
        // Round marker, then the gap, then the clock — two separators on the meta line, the clock at its end.
        Assert.Equal(2, message.MetaText.Count(c => c == '·'));
        Assert.EndsWith(message.Time, message.MetaText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_agent_transcript_keeps_its_header_and_numbers_no_subtitle()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: true);
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);

        Assert.Same(card, transcript.Rows[1]);
        Assert.False(((UserMessageViewModel)transcript.Rows[0]).HasRound);
    }

    // ---------------------------------------------------------------- line rules

    [Fact]
    public void A_completed_turn_shows_its_rate_and_a_failed_turn_shows_unknown_tokens()
    {
        var line = TurnDividerText.Line(
            TurnEndStatus.Completed, TimeSpan.FromSeconds(46.2), TimeSpan.FromSeconds(40), 37_000, 2_500, 6, "claude-sonnet-4-5");
        Assert.Contains("tok/s", line);
        Assert.DoesNotContain("—", line);

        var failed = TurnDividerText.Line(
            TurnEndStatus.Failed, TimeSpan.FromSeconds(12.8), TimeSpan.FromSeconds(9), 100, 50, 2, null);
        Assert.Contains("—", failed);
        Assert.DoesNotContain("tok/s", failed);
    }

    [Fact]
    public void The_wall_clock_never_moves_the_rate()
    {
        // Queueing, an approval and a slow git snapshot lengthen the turn without changing what the model streamed, so the
        // rate reads the same at 2 s and at 30 min of wall clock, and the same as the two-argument function.
        const int output = 2_000;
        var streamed = TimeSpan.FromSeconds(40);
        Assert.Equal(50d, TurnDividerText.OutputRate(output, streamed));

        var quick = TurnDividerText.Line(TurnEndStatus.Completed, TimeSpan.FromSeconds(2), streamed, 1_000, output, 0, null);
        var slow = TurnDividerText.Line(TurnEndStatus.Completed, TimeSpan.FromMinutes(30), streamed, 1_000, output, 0, null);
        Assert.Contains(TurnDividerText.FormatRate(50d), quick);
        Assert.Contains(TurnDividerText.FormatRate(50d), slow);
        // The wall clock still times the turn, so only the 耗时 segment differs.
        Assert.NotEqual(quick, slow);
    }

    [Fact]
    public void A_zero_stream_time_reads_unknown_instead_of_dividing_by_zero()
    {
        // A turn that streamed nothing has no denominator; it reads "—" even though its wall clock is known and positive.
        var line = TurnDividerText.Line(
            TurnEndStatus.Completed, TimeSpan.FromSeconds(46.2), TimeSpan.Zero, 1_000, 2_500, 1, null);
        Assert.Contains("—", line);
        Assert.DoesNotContain("tok/s", line);
    }

    [Fact]
    public void An_unreported_completed_turn_reads_unknown_instead_of_zero()
    {
        var line = TurnDividerText.Line(TurnEndStatus.Completed, null, TimeSpan.Zero, null, null, 0, null);
        Assert.Contains("—", line);
        Assert.DoesNotContain("0 tok/s", line);
    }

    // ---------------------------------------------------------------- live rows

    [Fact]
    public void A_finished_main_session_turn_leaves_a_divider_and_no_header_row()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AddUsage(new TokenUsage(1000, 200, 5000, 142, 42), StopReason.EndTurn);
        card.NoteStream(TimeSpan.FromSeconds(40));
        card.AppendText("done");
        transcript.EndTurn(TimeSpan.FromSeconds(46.2), null);

        Assert.DoesNotContain(transcript.Rows, row => row is TurnCardViewModel);
        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.Same(card, divider.Card);
        Assert.Contains("tok/s", divider.Line);
        Assert.DoesNotContain("—", divider.Line);
    }

    [Fact]
    public void A_running_turn_shows_no_divider_row()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        transcript.BeginTurn(At0);

        Assert.DoesNotContain(transcript.Rows, row => row is TurnDividerViewModel);
    }

    [Fact]
    public void A_cancelled_turn_keeps_its_status_with_unknown_tokens()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AddUsage(new TokenUsage(100, 50), StopReason.EndTurn);
        transcript.EndTurn(TimeSpan.FromSeconds(4), Stopped());

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.Same(card, divider.Card);
        Assert.True(divider.HasFailed);
        Assert.Contains("—", divider.Line);
        Assert.DoesNotContain("tok/s", divider.Line);
    }

    [Fact]
    public void The_card_stream_time_drives_the_line_and_refreshes_it()
    {
        // Stream time can land after the finish exactly like the usage can (the coalescer's Background flush trails the
        // turn's continuation): the divider recomputes its rate as soon as the card is told, and until then it reads "—".
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AddUsage(new TokenUsage(1_000, 500), StopReason.EndTurn);
        card.AppendText("hello");
        transcript.EndTurn(TimeSpan.FromSeconds(46.2), null);

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.DoesNotContain("tok/s", divider.Line);

        card.NoteStream(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), card.StreamElapsed);
        Assert.Contains(TurnDividerText.FormatRate(50d), divider.Line);

        // Every completion of the turn adds its own share; the card keeps the total, the line follows.
        card.NoteStream(TimeSpan.FromSeconds(10));
        Assert.Contains(TurnDividerText.FormatRate(25d), divider.Line);
    }

    private static Forge.App.Localization.LocText Stopped() =>
        Forge.App.Localization.SessionStrings.Stopped;

    // ---------------------------------------------------------------- replay seeding

    [Fact]
    public void Replay_seeding_fills_the_clock_the_tools_and_the_outcome_by_round()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AppendText("hello");
        transcript.EndTurn(null, null);

        var dividerBefore = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.Contains("—", dividerBefore.Line);

        transcript.SeedTurnMetrics(
        [
            new TurnMetric(
                "s", card.Round, At0.ToUniversalTime(), At0.AddSeconds(46.2).ToUniversalTime(),
                TurnMetricStatus.Failed, "p", "m", "boom",
                null, null, null, null, null, 6),
        ]);

        Assert.Equal(TurnEndStatus.Failed, card.EndStatus);
        Assert.Equal(6, card.ToolCount);
        Assert.Equal("m (p)", card.DividerModelText);
        Assert.Contains("—", dividerBefore.Line);
        Assert.DoesNotContain("tok/s", dividerBefore.Line);
        Assert.Contains("boom", dividerBefore.Tooltip);
    }

    [Fact]
    public void A_round_the_table_never_recorded_keeps_reading_unknown()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        transcript.EndTurn(null, null);

        transcript.SeedTurnMetrics(
        [
            new TurnMetric("s", card.Round + 10, At0, At0.AddSeconds(1), TurnMetricStatus.Completed),
        ]);

        Assert.Equal(TurnEndStatus.Completed, card.EndStatus);
        Assert.Null(card.Elapsed);
        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.Contains("—", divider.Line);
    }

    [Fact]
    public void A_failed_turn_shows_unknown_tokens_and_the_tool_count_in_their_own_slots()
    {
        var failed = TurnDividerText.Line(
            TurnEndStatus.Failed, TimeSpan.FromSeconds(12.8), TimeSpan.FromSeconds(9), 100, 50, 7, "glm-5.3");

        // Both templates put the token slots (—) before the tool slot, so a swapped argument order — the tool count
        // landing in the token slots with "—" as the tool count — fails this.
        Assert.Contains(TextFormat.None, failed);
        Assert.Contains(TextFormat.Number(7), failed);
        Assert.True(
            failed.IndexOf(TextFormat.None, StringComparison.Ordinal) < failed.IndexOf(TextFormat.Number(7), StringComparison.Ordinal),
            $"the unknown-token placeholder must come before the tool count: {failed}");
    }

    [Fact]
    public void Replay_seeding_brings_the_stream_denominator()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AppendText("hello");
        // What the replay restores from the stored messages: the tokens, but not the stream time.
        card.AddUsage(new TokenUsage(1_000, 2_000), StopReason.EndTurn);
        transcript.EndTurn(null, null);

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.DoesNotContain("tok/s", divider.Line);

        transcript.SeedTurnMetrics(
        [
            new TurnMetric(
                "s", card.Round, At0.ToUniversalTime(), At0.AddSeconds(46.2).ToUniversalTime(),
                TurnMetricStatus.Completed, "p", "m", null,
                null, null, null, null, null, null, TimeSpan.FromSeconds(40)),
        ]);

        Assert.Equal(TimeSpan.FromSeconds(40), card.StreamElapsed);
        Assert.Contains(TurnDividerText.FormatRate(50d), divider.Line);

        // Seeding twice (a reloaded history) keeps the row's total instead of doubling it.
        transcript.SeedTurnMetrics(
        [
            new TurnMetric(
                "s", card.Round, At0.ToUniversalTime(), At0.AddSeconds(46.2).ToUniversalTime(),
                TurnMetricStatus.Completed, "p", "m", null,
                null, null, null, null, null, null, TimeSpan.FromSeconds(40)),
        ]);
        Assert.Equal(TimeSpan.FromSeconds(40), card.StreamElapsed);
    }

    [Fact]
    public void A_rolled_back_clock_does_not_inflate_the_next_interval()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("one", At0);
        transcript.AddUserMessage("two", At0.AddMinutes(10));
        transcript.AddUserMessage("three", At0.AddMinutes(9)); // the clock rolled back: no interval is shown
        transcript.AddUserMessage("four", At0.AddMinutes(20));

        var messages = transcript.Rows.OfType<UserMessageViewModel>().ToArray();
        Assert.Null(messages[0].GapSincePrevious);
        Assert.Equal(TimeSpan.FromMinutes(10), messages[1].GapSincePrevious);
        Assert.Null(messages[2].GapSincePrevious);

        // Measured against the last forward time (At0+10), not the rolled-back At0+9 — otherwise this would read 11.
        Assert.Equal(TimeSpan.FromMinutes(10), messages[3].GapSincePrevious);
    }

    [Fact]
    public void The_model_stamped_at_the_turn_start_rides_at_the_end_of_the_line()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.NoteModel("anthropic", "claude-sonnet-4-5");
        card.AppendText("hello");
        transcript.EndTurn(TimeSpan.FromSeconds(46.2), null);

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.EndsWith("claude-sonnet-4-5", divider.Line, StringComparison.Ordinal);
        Assert.Contains("claude-sonnet-4-5", divider.Tooltip, StringComparison.Ordinal);

        // A turn nobody stamped (a replay whose metrics row had no model) omits the segment entirely.
        card.NoteModel(null, null);
        var bare = TurnDividerText.Line(
            TurnEndStatus.Completed, TimeSpan.FromSeconds(46.2), TimeSpan.FromSeconds(40), 37_000, 2_500, 6, null);
        Assert.DoesNotContain(" · ·", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_arriving_after_the_finish_refreshes_the_divider()
    {
        var transcript = MainTranscript();
        transcript.AddUserMessage("hi", At0);
        var card = transcript.BeginTurn(At0);
        card.AppendText("hello");
        card.NoteStream(TimeSpan.FromSeconds(40));
        transcript.EndTurn(TimeSpan.FromSeconds(46.2), null);

        var divider = Assert.IsType<TurnDividerViewModel>(transcript.Rows[^1]);
        Assert.Contains("—", divider.Line);

        // The Background flush can trail the turn's Normal-priority continuation: the usage lands after the finish and
        // the divider must pick it up instead of keeping "—" until some later refresh.
        card.AddUsage(new TokenUsage(37_000, 2_500, 31_500, 2_100, 900), StopReason.EndTurn);

        Assert.DoesNotContain("—", divider.Line);
        Assert.Contains("tok/s", divider.Line);
    }
}
