using Forge.Abstractions;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A sub-agent's turn divider must say what ran it, exactly like the root's: the model is stamped through the one hook every
/// card-creation path shares (<see cref="TranscriptViewModel.StampTurnModel"/> in <see cref="TranscriptViewModel.BeginTurn"/>),
/// because a child's card is begun lazily - by the flush of the first streamed delta, by a tool row - and a stamp that only
/// sat on one of those paths left the tooltip without a model name while the root's dividers always had one. The turn
/// boundary is <see cref="TranscriptViewModel.EndIsolatedTurn"/>: the next turn's content must begin a fresh card, not
/// append to the closed one.
/// </summary>
[Collection("forge-app-ui")]
public class SubAgentDividerTests
{
    private static TranscriptViewModel SubAgentTranscript()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: true);
        transcript.StampTurnModel = card => card.NoteModel(null, "child-model");
        return transcript;
    }

    [Fact]
    public void Every_lazily_begun_turn_names_the_model_that_ran_it()
    {
        var transcript = SubAgentTranscript();

        // The first card is begun by the flush of a streamed delta - the path a stamp on any single call site misses.
        transcript.QueueDelta("answer");
        transcript.FlushPending();
        var first = transcript.Current;
        Assert.NotNull(first);
        first!.AddUsage(new TokenUsage(10, 5), StopReason.EndTurn);
        transcript.EndIsolatedTurn(TimeSpan.FromSeconds(2), null);
        Assert.Contains("child-model", first.DividerRow.Tooltip);
        Assert.Contains("↑10 ↓5", first.DividerRow.Line);
        Assert.Contains("工具 0", first.DividerRow.Line);

        // The second card is begun by the same lazy path once the first closed, and the stamp rides with it too.
        transcript.QueueDelta("follow-up");
        transcript.FlushPending();
        var second = transcript.Current;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        second!.AddUsage(new TokenUsage(3, 2), StopReason.EndTurn);
        transcript.EndIsolatedTurn(TimeSpan.FromSeconds(1), null);
        Assert.Contains("child-model", second.DividerRow.Tooltip);
        Assert.Contains("↑3 ↓2", second.DividerRow.Line);
    }

    [Fact]
    public void The_tab_reads_its_own_totals_through_the_same_instruments()
    {
        var agent = new SubAgentViewModel(
            "session", "agent", "child", "Scout", "do it", "child-model", stopRequested: null,
            sources: EmptySessionInstrumentSources.Instance);

        // Nothing ran yet: the chip keeps its shape with zeros, the way the session's strip does.
        Assert.Equal("↑0 ↓0 · RC 0.0%", agent.Instruments.UsageChipText);

        // One turn with 100 in / 20 out, 90 of the input served from the cache, routed as the session routes it: the
        // same chip text the strip's own reading shows for the same numbers.
        agent.Instruments.AddTurn("agent", new TokenUsage(100, 20, 90, 0, 0));
        Assert.Equal("↑100 ↓20 · RC 90.0%", agent.Instruments.UsageChipText);

        // Another agent's events are not this tab's: the reading does not move.
        agent.Instruments.AddTurn("other", new TokenUsage(500, 500, 0, 0, 0));
        Assert.Equal("↑100 ↓20 · RC 90.0%", agent.Instruments.UsageChipText);

        // A replayed tab seeds the same reading from the history the replay summed.
        agent.Instruments.SeedHistory(new TokenUsage(5, 5, 0, 0, 0));
        Assert.Equal("↑105 ↓25 · RC 85.7%", agent.Instruments.UsageChipText);
    }

    [Fact]
    public void The_tab_is_titled_by_the_speakable_id_and_the_stored_row_by_its_kind()
    {
        // A live child's id already says what it is (Core's kind-prefixed counter): the tab and the breadcrumb show it
        // alone, and the task rides on the hover instead of the title.
        var live = new SubAgentViewModel("session", "Worker-3", "child", "Worker", "go fix the failing test", "m", null);
        Assert.Equal("▸ Worker-3", live.TabTitle);
        Assert.Equal("Worker-3", live.BreadcrumbName);

        // A kind Core had to sanitize lost its name in the id, so the kind rides along in the kind·id form.
        var sanitized = new SubAgentViewModel("session", "---3", "child", "调研员", "调研一下", "m", null);
        Assert.Equal("▸ 调研员 · ---3", sanitized.TabTitle);

        // A stored row's live id died with the process that issued it and can never be resumed: the kind alone.
        var stored = SubAgentViewModel.FromStored(
            "session",
            new SessionInfo("0797eb65a875498ca08a16aa10ee5aa8", @"C:\w", "t", DateTimeOffset.UnixEpoch)
            {
                AgentKind = "Worker",
            },
            "root",
            SubAgentStatus.Completed);
        Assert.Equal("○ Worker", stored.TabTitle);
        Assert.Equal("Worker", stored.BreadcrumbName);
    }

    [Fact]
    public void A_runs_end_forgets_the_card_and_a_resume_reopens_the_stop_gate()
    {
        var agent = new SubAgentViewModel(
            "session", "Worker-3", "child", "Worker", "go", "child-model", static _ => { });

        // A stopped run's card closes with the run and is forgotten: the next run's content begins a fresh card
        // instead of piling into the one that carries the stop's footnote.
        agent.MarkRunning(DateTimeOffset.Now);
        agent.Transcript.QueueDelta("half an answer");
        agent.Transcript.FlushPending();
        Assert.NotNull(agent.Transcript.Current);
        agent.Finish(SubAgentStatus.Stopped, TimeSpan.FromSeconds(5), null);
        Assert.Null(agent.Transcript.Current);

        // A stop that was requested closed the gate for that run's life; the next task reopens it.
        agent.MarkRunning(DateTimeOffset.Now);
        Assert.True(agent.CanStop);
        agent.StopCommand.Execute(null);
        Assert.False(agent.CanStop);
        agent.MarkResumed();
        agent.MarkRunning(DateTimeOffset.Now);
        Assert.True(agent.CanStop);
    }

    [Fact]
    public void The_childs_instruments_are_built_on_first_use_not_at_restore()
    {
        var stored = SubAgentViewModel.FromStored(
            "session",
            new SessionInfo("0797eb65a875498ca08a16aa10ee5aa8", @"C:\w", "t", DateTimeOffset.UnixEpoch)
            {
                AgentKind = "Worker",
            },
            "root",
            SubAgentStatus.Completed);

        // A restored history row pays nothing until one of its readouts is actually read.
        Assert.False(stored.InstrumentsBuilt);
        Assert.Equal("Worker", stored.BreadcrumbName);
        Assert.False(stored.InstrumentsBuilt);
        _ = stored.Instruments;
        Assert.True(stored.InstrumentsBuilt);
    }

    /// <summary>
    /// A reused child keeps one instance counter across the runs its parent hands it, and every run's own numbers are
    /// measured against the baseline the run began at: a completed run's terminal event carries exactly what its turns
    /// already reported (nothing left over), while a run that failed mid-turn still owes its partial spend. A fresh
    /// counter per run would re-count the whole cumulative as the resumed run's own tokens.
    /// </summary>
    [Fact]
    public void A_resumed_run_reports_only_its_own_tokens_and_a_failed_run_keeps_its_partial_spend()
    {
        // Run 1 completed: the instance counter moved to (100, 20) and the run's terminal event carries the same shape.
        var afterRunOne = new TokenUsage(100, 20);
        Assert.Equal(
            new TokenUsage(0, 0),
            SessionWorkspaceViewModel.UnreportedRunUsage(new TokenUsage(100, 20), afterRunOne, TokenUsage.Empty));

        // Run 2 (the same child, new task) also completed: measured against run 1's baseline, not against zero.
        var afterRunTwo = new TokenUsage(140, 30);
        Assert.Equal(
            new TokenUsage(0, 0),
            SessionWorkspaceViewModel.UnreportedRunUsage(new TokenUsage(40, 10), afterRunTwo, afterRunOne));

        // Run 3 failed mid-turn: no turn event ever carried the 25 tokens it did spend before failing, and the terminal
        // event still reports them - the counter never moved, so the whole run usage is unreported.
        Assert.Equal(
            new TokenUsage(25, 0),
            SessionWorkspaceViewModel.UnreportedRunUsage(new TokenUsage(25, 0), afterRunTwo, afterRunTwo));
    }
}
