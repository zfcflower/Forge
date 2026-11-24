using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A <c>spawn_agent</c> hands the task to a child that just did the same work instead of starting a new one: the child's
/// conversation and its provider-side prompt cache are the two things a fresh child would have to rebuild, and they only
/// pay off when the task really is the same work. These pin the selection rules — same kind, the model's own, idle, inside
/// the reuse window, and above the affinity threshold — plus the escape hatches that must never reuse (a model override, a
/// review, an automation child).
/// </summary>
public sealed class SubAgentReuseTests
{
    private const string BadgeTask =
        "Fix the run count in src/Forge.App/Views/SubAgentView.axaml so the digit is centred in its badge.";

    private sealed class Sink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Emit(AgentEvent value) => Events.Add(value);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_child_that_just_did_the_same_work_is_continued()
    {
        var clock = new Clock();
        var scheduler = Scheduler(clock, Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        clock.Now += TimeSpan.FromMinutes(10);
        var second = await Spawn(scheduler, "Worker", BadgeTask);

        Assert.False(first.IsError, first.Text);
        Assert.True(second.Resumed);
        Assert.Equal(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task An_unrelated_task_gets_a_fresh_child()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        var second = await Spawn(scheduler, "Worker", "Write the release notes for the next version.");

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_child_whose_window_closed_is_history()
    {
        var clock = new Clock();
        var scheduler = Scheduler(clock, Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        // The window is one hour from the end of the run; a minute past it the context is stale and a fresh child is right.
        clock.Now += TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        var second = await Spawn(scheduler, "Worker", BadgeTask);

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_child_that_is_still_running_is_left_alone()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Runner = (instance, message, sink, ct) =>
        {
            instance.Add(message, 1);
            if (entered.TrySetResult())
            {
                // Only the first run is held open; the second spawn must be able to finish while it runs.
                return release.Task;
            }

            return Task.FromResult(new AgentRunResult("done", TokenUsage.Empty, StopReason.EndTurn));
        };

        var running = Spawn(scheduler, "Worker", BadgeTask);
        await entered.Task;

        // The same task, while the first child still holds its run: it cannot be continued, so this spawn starts a new one.
        var second = await Spawn(scheduler, "Worker", BadgeTask);
        Assert.False(second.Resumed);

        release.SetResult(new AgentRunResult("done", TokenUsage.Empty, StopReason.EndTurn));
        var first = await running;
        Assert.False(first.IsError);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_review_or_automation_spawn_never_reuses()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        // `reuse: false` is how request_review and the automation host call the scheduler: a reviewer must not carry its
        // previous verdict into the next review, and an automation's child is nobody's next task.
        var second = await Spawn(scheduler, "Worker", BadgeTask, reuse: false);

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_child_created_by_a_review_is_never_picked_by_a_later_spawn()
    {
        var scheduler = Scheduler(new Clock(), Kind("Reviewer"));
        var review = await Spawn(scheduler, "Reviewer", BadgeTask, reuse: false);

        // The reviewer is inside the window and idle, and its task names the same file - but it is not the conversation's
        // child: a later reviewer spawn must not answer with the verdict of the last review.
        var next = await Spawn(scheduler, "Reviewer", BadgeTask);

        Assert.False(next.Resumed);
        Assert.NotEqual(review.AgentId, next.AgentId);
    }

    [Fact]
    public async Task A_model_override_spawn_never_reuses()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        // A resumed child keeps the model it started with, so an override can only mean a new child.
        var second = await Spawn(scheduler, "Worker", BadgeTask, model: new ModelSettings("other-model"));

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task Another_kind_is_never_picked()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"), Kind("Scout"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        var second = await Spawn(scheduler, "Scout", BadgeTask);

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public void One_shared_file_is_enough_and_one_shared_word_is_not()
    {
        // The threshold is 2: a file counts 3, a bare identifier 1.
        Assert.Equal(3, SubAgentAffinity.Score(
            "centre the count in SubAgentView.axaml", "SubAgentView.axaml", string.Empty));
        Assert.Equal(1, SubAgentAffinity.Score("refactor the composer", "the composer split", string.Empty));
        Assert.Equal(2, SubAgentAffinity.Score("refactor composer split", "composer split", string.Empty));
        // A file named in the candidate's answer counts like one named in its task: the report is where a child says what it
        // touched.
        Assert.Equal(3, SubAgentAffinity.Score("fix the badge in SubAgentView.axaml", string.Empty, "I changed SubAgentView.axaml."));
        // CJK prose is one run of letters, so identical prose counts once - never enough on its own to clear the
        // threshold: a Chinese task is reused through the paths and identifiers it names, not through its sentences.
        Assert.Equal(1, SubAgentAffinity.Score("把徽标里的数字居中", "把徽标里的数字居中", string.Empty));
    }

    private static SubAgentScheduler Scheduler(TimeProvider clock, params AgentDefinition[] kinds)
    {
        var scheduler = new SubAgentScheduler(
            new AgentCatalog(kinds),
            new PromptLayers(string.Empty, null, null),
            new PromptComposer(),
            null,
            new AgentOptions { SubAgentReuseClock = clock, MaxSubAgentDepth = 2 });

        // A stand-in for the loop: it records the exchange the affinity reader looks at, and answers at once.
        scheduler.Runner = (instance, message, sink, ct) =>
        {
            instance.Add(message, 1);
            instance.Add(ChatMessage.Assistant("done."), 2);
            return Task.FromResult(new AgentRunResult("done.", TokenUsage.Empty, StopReason.EndTurn));
        };
        return scheduler;
    }

    private static AgentDefinition Kind(string name) =>
        new(name, "d", "p", new ModelSettings("m"), ["read"]);

    private static Task<SubAgentOutcome> Spawn(
        SubAgentScheduler scheduler,
        string kind,
        string task,
        bool reuse = true,
        ModelSettings? model = null) =>
        scheduler.SpawnAsync(
            kind,
            task,
            resumeId: null,
            new ToolContext("session", "wd", 0),
            new Sink(),
            "main",
            CancellationToken.None,
            model: model,
            reuse: reuse);
}
