using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The rules of one metrics row, without a turn: what outcome it records, which text it keeps, how the tokens are the
/// difference of two event-thread snapshots of the instance counter, and how a turn that reported nothing is told apart
/// from a turn that reported zero.
/// </summary>
public sealed class TurnMetricsRowTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Ended = new(2026, 11, 18, 1, 31, 0, TimeSpan.Zero);

    private static TurnMetricsRow Build(
        LocText? error = null,
        string? rootFailure = null,
        TokenUsage? turnStartUsage = null,
        TokenUsage? instanceUsage = null,
        int toolCalls = 2,
        TimeSpan? rootStream = null,
        TimeSpan? turnStartStream = null,
        ThinkingEffort? effort = null) =>
        TurnMetricsRowFactory.Build(
            "s1",
            7,
            Started,
            Ended,
            error,
            rootFailure,
            instanceUsage ?? TokenUsage.Empty,
            turnStartUsage ?? TokenUsage.Empty,
            rootStream ?? TimeSpan.Zero,
            turnStartStream ?? TimeSpan.Zero,
            toolCalls,
            "anthropic",
            "claude",
            effort);

    /// <summary>
    /// The reasoning step a turn ran with is part of its row, not a display-only detail: the reopened divider reads it back
    /// from here, and a factory that dropped it would still pass every rule about tokens and outcomes.
    /// </summary>
    [Fact]
    public void The_row_carries_the_reasoning_step_the_turn_ran_with()
    {
        var row = Build(effort: ThinkingEffort.High);

        Assert.Equal(ThinkingEffort.High, row.Effort);
        Assert.Equal(ThinkingEffort.High, row.ToMetric().Effort);

        // A turn nobody recorded a step for keeps it unknown instead of reading as "off".
        Assert.Null(Build().ToMetric().Effort);
    }

    [Fact]
    public void A_turn_that_ran_through_is_completed_and_keeps_the_snapshot_difference_in_utc()
    {
        // The turn began with the instance counter already at (9000, 180, 4800, 100, 0); its own spend is the difference.
        var row = Build(
            turnStartUsage: new TokenUsage(9000, 180, 4800, 100, 0),
            instanceUsage: new TokenUsage(10_000, 380, 9800, 242, 42));

        Assert.Equal(TurnMetricStatus.Completed, row.Status);
        Assert.Null(row.Error);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(200, row.OutputTokens);
        Assert.Equal(5000, row.CacheReadTokens);
        Assert.Equal(142, row.CacheWriteTokens);
        Assert.Equal(42, row.CacheWrite1hTokens);
        Assert.Equal(2, row.ToolCalls);
        Assert.Equal("anthropic", row.Provider);
        Assert.Equal("claude", row.Model);

        // Persisted time is UTC whatever offset the turn started with; the same instant, not the same wall clock.
        Assert.Equal(Started.ToUniversalTime(), row.StartedAt);
        Assert.Equal(TimeSpan.Zero, row.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, row.EndedAt.Offset);
        Assert.Equal(Ended, row.EndedAt);
    }

    [Fact]
    public void A_turn_that_reported_nothing_keeps_every_token_column_null_rather_than_zero()
    {
        // The counter never moved: not "the turn spent zero", but "nothing was reported about it".
        var row = Build(turnStartUsage: new TokenUsage(9000, 180, 4800, 100, 0), instanceUsage: new TokenUsage(9000, 180, 4800, 100, 0));

        Assert.Equal(TurnMetricStatus.Completed, row.Status);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheReadTokens);
        Assert.Null(row.CacheWriteTokens);
        Assert.Null(row.CacheWrite1hTokens);
    }

    [Fact]
    public void A_failed_turn_keeps_its_row_with_null_tokens_whatever_usage_reached_the_counter()
    {
        // Usage can arrive before the failure; the row refuses it anyway — the red line is the row's, not the caller's.
        var row = Build(
            error: LocText.Literal("provider refused the key"),
            turnStartUsage: new TokenUsage(9000, 180, 4800, 100, 0),
            instanceUsage: new TokenUsage(9500, 300, 5300, 150, 20));

        Assert.Equal(TurnMetricStatus.Failed, row.Status);
        Assert.Equal("provider refused the key", row.Error);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheReadTokens);
        Assert.Null(row.CacheWriteTokens);
        Assert.Null(row.CacheWrite1hTokens);
    }

    [Fact]
    public void The_stream_duration_is_the_difference_of_the_two_snapshots_over_the_turns_calls()
    {
        // Two model calls in one turn: the instance counter read 100 ms when the turn began and 300 ms at its end, so the
        // turn streamed 200 ms — the same arithmetic as the tokens, over the same pair of event-thread snapshots.
        var row = Build(rootStream: TimeSpan.FromMilliseconds(300), turnStartStream: TimeSpan.FromMilliseconds(100));

        Assert.Equal(200, row.StreamMs);
        Assert.Equal(TimeSpan.FromMilliseconds(200), row.ToMetric().StreamElapsed);
    }

    [Fact]
    public void A_turn_that_streamed_nothing_records_zero_rather_than_null()
    {
        // Zero output is a measurement, not a missing one: the card and the database both read 0, and the rate reads "—"
        // because the denominator is zero — the wall clock is never a substitute.
        Assert.Equal(0, Build().StreamMs);
        Assert.Equal(TimeSpan.Zero, Build().ToMetric().StreamElapsed);

        // A counter that went backwards (a rebuilt instance) clamps to zero instead of negative time.
        Assert.Equal(0, Build(rootStream: TimeSpan.FromMilliseconds(100), turnStartStream: TimeSpan.FromMilliseconds(300)).StreamMs);
    }

    [Fact]
    public void A_turn_the_user_stopped_is_stopped_whatever_else_arrived()
    {
        var row = Build(error: SessionStrings.Stopped, rootFailure: "cancelled request");

        Assert.Equal(TurnMetricStatus.Stopped, row.Status);
        Assert.Equal(SessionStrings.Stopped.Resolve(), row.Error);
    }

    [Fact]
    public void An_agent_failure_is_failed_and_carries_the_message_the_user_saw()
    {
        // Core reports every failure of the agent loop as an AgentFailed event with nothing but this sentence, so a turn that
        // hit the turn limit is indistinguishable here and is recorded as failed (see TurnMetricsRowFactory.BuildStatus).
        const string limit = "Agent 'root' did not finish within 12 turns.";

        var row = Build(rootFailure: limit);

        Assert.Equal(TurnMetricStatus.Failed, row.Status);
        Assert.Equal(limit, row.Error);
        Assert.Null(row.OutputTokens);
    }
}
