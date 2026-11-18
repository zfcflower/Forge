using Forge.Abstractions;
using Forge.Core.Tools;
using Xunit;

namespace Forge.App.Tests;

public sealed class StepGateTests
{
    private const string Session = "s1";

    private sealed class FakeProbe(Func<StepChangeProbe> answer) : IStepChangeProbe
    {
        public Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(answer());
    }

    private sealed class ThrowingProbe(Exception exception) : IStepChangeProbe
    {
        public Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct) =>
            throw exception;
    }

    private static TodoItem Step(string? commitId = null, string? baseline = null) =>
        new("1", Session, "step", TodoStatus.InProgress, null, 0, DateTimeOffset.UtcNow, CommitId: commitId, BaselineTree: baseline);

    private static StepGate Gate(StepChangeProbe probe, StepReviewLedger? ledger = null, Func<bool>? enabled = null) =>
        new(ledger ?? new StepReviewLedger(), new FakeProbe(() => probe), enabled);

    private static Task<StepGateDecision> Check(StepGate gate, TodoItem step, string? verification = "build ok") =>
        gate.CheckAsync(step, verification, "wd", CancellationToken.None);

    [Fact]
    public async Task Approve_bound_to_current_tree_is_accepted()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, "abc1234", "X");
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty, Tree: "X"), ledger), Step("abc1234"));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Approve_with_placeholder_verification_is_refused()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, "abc1234", "X");
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty, Tree: "X"), ledger), Step("abc1234"), "Not verified.");
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Approve_for_a_different_tree_is_refused_and_asks_for_review()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, "abc1234", "X");
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty, Tree: "Y"), ledger), Step("abc1234"));
        Assert.False(decision.Allowed);
        Assert.Contains("request_review", decision.NextStep);
    }

    [Fact]
    public async Task Approve_recorded_without_tree_is_never_accepted()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, "abc1234", null);
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty, Tree: "X"), ledger), Step("abc1234"));
        Assert.False(decision.Allowed);
        Assert.Contains("request_review", decision.NextStep);
    }

    [Fact]
    public async Task Approve_is_refused_when_probe_has_no_tree()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, "abc1234", "X");
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty), ledger), Step("abc1234"));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Clean_without_commit_and_baseline_is_allowed()
    {
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Clean, Tree: "X")), Step());
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Clean_with_baseline_differing_from_current_tree_needs_review()
    {
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Clean, Tree: "NEW")), Step(baseline: "OLD"));
        Assert.False(decision.Allowed);
        Assert.Contains("request_review", decision.NextStep);
    }

    [Fact]
    public async Task Clean_with_baseline_differing_passes_after_bound_approval()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "1", ReviewVerdict.Approve, null, "NEW");
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Clean, Tree: "NEW"), ledger), Step(baseline: "OLD"));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Clean_tree_equal_to_baseline_is_allowed_even_with_commit()
    {
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Clean, Tree: "B")), Step("abc1234", "B"));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Dirty_equal_to_baseline_still_needs_a_review()
    {
        // Edits made before the step started are in the baseline, but the step's commit would take them in unreviewed.
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Dirty, Tree: "B")), Step(baseline: "B"));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Unknown_fails_closed()
    {
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.Unknown, "broken")), Step());
        Assert.False(decision.Allowed);
        Assert.Contains("broken", decision.Reason);
    }

    [Fact]
    public async Task Throwing_probe_fails_closed()
    {
        var gate = new StepGate(new StepReviewLedger(), new ThrowingProbe(new InvalidOperationException("git exploded")));
        var decision = await Check(gate, Step());
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task NotApplicable_is_allowed()
    {
        var decision = await Check(Gate(new StepChangeProbe(StepChangeState.NotApplicable)), Step("abc1234", "B"));
        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData(StepChangeState.Clean, "T", "T")]
    [InlineData(StepChangeState.Dirty, "T", "T")]
    [InlineData(StepChangeState.NotApplicable, null, null)]
    [InlineData(StepChangeState.Unknown, null, null)]
    public async Task CaptureBaseline_returns_tree_only_for_clean_or_dirty(StepChangeState state, string? tree, string? expected)
    {
        var baseline = await Gate(new StepChangeProbe(state, Tree: tree)).CaptureBaselineAsync(Session, "wd", CancellationToken.None);
        Assert.Equal(expected, baseline);
    }

    [Fact]
    public async Task CaptureBaseline_is_null_when_probe_throws()
    {
        var gate = new StepGate(new StepReviewLedger(), new ThrowingProbe(new InvalidOperationException("x")));
        Assert.Null(await gate.CaptureBaselineAsync(Session, "wd", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureBaseline_is_null_when_gate_disabled()
    {
        var gate = Gate(new StepChangeProbe(StepChangeState.Clean, Tree: "T"), enabled: () => false);
        Assert.Null(await gate.CaptureBaselineAsync(Session, "wd", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureBaseline_propagates_cancellation()
    {
        var gate = new StepGate(new StepReviewLedger(), new ThrowingProbe(new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.CaptureBaselineAsync(Session, "wd", CancellationToken.None));
    }
}
