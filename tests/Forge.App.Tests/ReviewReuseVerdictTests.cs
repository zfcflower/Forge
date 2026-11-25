using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A review that continues an earlier one still has to earn its own verdict. <c>request_review</c> reads the verdict line out
/// of the text this run produced, so a continued Reviewer that says nothing about a verdict this time leaves the review
/// unstructured - never the approval its earlier run gave. That is the whole safety argument for letting reviewers into the
/// reuse pool, where the earlier verdict sits in the continued conversation.
/// </summary>
public sealed class ReviewReuseVerdictTests
{
    [Fact]
    public async Task A_continued_reviewer_cannot_carry_its_previous_verdict()
    {
        var replies = new Queue<string>(["verdict: approve", "Read the new diff again; nothing else stands out."]);
        var scheduler = Scheduler(replies);
        var conclusions = new Conclusions();
        var tool = new RequestReviewTool(scheduler, new ChangeSets(), conclusions: conclusions);

        var pool = new AgentSlotPool(1);
        using var permit = pool.TryAcquire();
        Assert.NotNull(permit);
        using (AgentScope.Enter(new Sink(), "main", permit!))
        {
            var context = new ToolContext("session", "wd") { AgentId = "main", AgentName = "main" };
            var arguments = """{"intent":"Check the badge fix in SubAgentView.axaml."}""";
            await tool.InvokeAsync(new ToolCall("1", RequestReviewTool.Name, arguments), context, CancellationToken.None);
            await tool.InvokeAsync(new ToolCall("2", RequestReviewTool.Name, arguments), context, CancellationToken.None);
        }

        Assert.Equal(2, conclusions.Recorded.Count);
        Assert.Equal(ReviewConclusionOutcome.Approve, conclusions.Recorded[0].Outcome);
        Assert.Equal(ReviewConclusionOutcome.Unstructured, conclusions.Recorded[1].Outcome);

        // The second review was handed to the reviewer the first one used: that continuation is what this test is about.
        Assert.Single(scheduler.Instances);
    }

    private static SubAgentScheduler Scheduler(Queue<string> replies)
    {
        var scheduler = new SubAgentScheduler(
            new AgentCatalog([new AgentDefinition("Reviewer", "d", "p", new ModelSettings("m"), ["read"])]),
            new PromptLayers(string.Empty, null, null),
            new PromptComposer(),
            null,
            new AgentOptions { MaxSubAgentDepth = 2 });

        // A stand-in for the loop: it records the exchange and answers with the next scripted reply.
        scheduler.Runner = (instance, message, sink, ct) =>
        {
            instance.Add(message, 1);
            var reply = replies.Dequeue();
            instance.Add(ChatMessage.Assistant(reply), 2);
            return Task.FromResult(new AgentRunResult(reply, TokenUsage.Empty, StopReason.EndTurn));
        };
        return scheduler;
    }

    private sealed class Sink : IAgentEventSink
    {
        public void Emit(AgentEvent value)
        {
        }
    }

    /// <summary>A change set Core would get from the app's git layer; identical for both calls, so the second review reuses.</summary>
    private sealed class ChangeSets : IReviewChangeSetSource
    {
        public Task<ReviewChangeSetResult> BuildAsync(ReviewChangeSetRequest request, CancellationToken ct) =>
            Task.FromResult(ReviewChangeSetResult.Ok(new ReviewChangeSet
            {
                WorkingDirectory = request.WorkingDirectory,
                Branch = "main",
                HeadCommit = "1111111111111111111111111111111111111111",
                Base = "2222222222222222222222222222222222222222",
                Files = [new ReviewChangedFile("src/Forge.App/Views/SubAgentView.axaml", "modified", false)],
                DiffText = """
                    --- a/src/Forge.App/Views/SubAgentView.axaml
                    +++ b/src/Forge.App/Views/SubAgentView.axaml
                    @@ -1 +1 @@
                    -<TextBlock Text="7"/>
                    +<TextBlock Text="7" TextAlignment="Center"/>
                    """,
            }));
    }

    private sealed class Conclusions : IReviewConclusionStore
    {
        public List<NewReviewConclusion> Recorded { get; } = [];

        public Task<ReviewConclusion> AddAsync(NewReviewConclusion conclusion, CancellationToken ct = default)
        {
            Recorded.Add(conclusion);
            return Task.FromResult(new ReviewConclusion(
                Recorded.Count,
                conclusion.WorkspacePath,
                conclusion.SessionId,
                conclusion.StepId,
                conclusion.Base,
                conclusion.HeadCommit,
                conclusion.Outcome,
                conclusion.Text,
                DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<ReviewConclusion>> ListAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReviewConclusion>>([]);
    }
}
