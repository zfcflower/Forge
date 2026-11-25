using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A <c>spawn_agent</c> hands the task to a child that just did the same work instead of starting a new one: the child's
/// conversation and its provider-side prompt cache are the two things a fresh child would have to rebuild, and they only
/// pay off when the task really is the same work. These pin the selection rules — same kind, the model's own, idle, inside
/// the reuse window, and above the affinity threshold — plus the escape hatches that must never reuse (a model override, an
/// automation child), and the roster a rebuilt host takes over.
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
    public async Task An_automation_child_is_never_picked_by_a_later_spawn()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        // `reuse: false` is how the automation host calls the scheduler: its child is nobody's next task.
        var second = await Spawn(scheduler, "Worker", BadgeTask, reuse: false);

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_reviewer_continues_like_any_other_kind()
    {
        var scheduler = Scheduler(new Clock(), Kind("Reviewer"));
        var review = await Spawn(scheduler, "Reviewer", BadgeTask);

        // request_review spawns through the default path, so the reviewer of the same change set is the same reviewer: its
        // context is the change it has already read, and its own instructions make it judge this package rather than repeat
        // the last verdict.
        var next = await Spawn(scheduler, "Reviewer", BadgeTask);

        Assert.True(next.Resumed);
        Assert.Equal(review.AgentId, next.AgentId);
    }

    [Fact]
    public async Task A_chinese_task_on_the_same_topic_continues_the_idle_child()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", "把运行计数放进标签里，数字要居中");

        // A Chinese task arrives without word separators, so it matches through the terms of its adjacent characters: the
        // same topic worded differently shares several, and the child is continued instead of rebuilt.
        var second = await Spawn(scheduler, "Worker", "继续修标签里数字居中的问题，顺便看运行计数");

        Assert.True(second.Resumed);
        Assert.Equal(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_chinese_task_about_something_else_starts_a_fresh_child()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", "把运行计数放进标签里，数字要居中");

        var second = await Spawn(scheduler, "Worker", "重写工作区树的行高与缩进");

        Assert.False(second.Resumed);
        Assert.NotEqual(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_file_named_in_an_earlier_turn_still_counts()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", BadgeTask);

        // A later run on the same child says nothing about the badge, so the newest exchange is unrelated - but the file it
        // already worked on is still in its history, and that is what a task about that file must find.
        await Spawn(scheduler, "Worker", "Write the release notes for the next version.", resumeId: first.AgentId);

        var third = await Spawn(scheduler, "Worker", "Look at SubAgentView.axaml again");

        Assert.True(third.Resumed);
        Assert.Equal(first.AgentId, third.AgentId);
    }

    [Fact]
    public async Task A_path_that_ends_in_a_separator_is_evidence_not_a_crash()
    {
        var scheduler = Scheduler(new Clock(), Kind("Worker"));
        var first = await Spawn(scheduler, "Worker", "I updated src/Forge.App/Views/SubAgentView.axaml and tests/ as well.");

        // The run that ends in a separator used to index past the trimmed run and throw, which failed the whole spawn.
        var second = await Spawn(scheduler, "Worker", "Look at SubAgentView.axaml again");

        Assert.False(first.IsError, first.Text);
        Assert.False(second.IsError, second.Text);
        Assert.True(second.Resumed);
        Assert.Equal(first.AgentId, second.AgentId);
    }

    [Fact]
    public async Task A_rebuilt_host_keeps_the_children_it_can_still_continue()
    {
        var clock = new Clock();
        var kinds = Kind("Worker");
        var previous = Scheduler(clock, kinds);
        var first = await Spawn(previous, "Worker", BadgeTask);

        // A prompt reload builds a new host for the same session: its scheduler starts from an empty roster, so it takes the
        // living children over rather than handing out a fresh child of a kind that is sitting idle.
        var rebuilt = Scheduler(clock, kinds);
        rebuilt.TakeOverFrom(previous);

        var second = await Spawn(rebuilt, "Worker", BadgeTask);
        Assert.True(second.Resumed);
        Assert.Equal(first.AgentId, second.AgentId);

        // The replaced scheduler no longer answers for that child, and the id it handed out is not handed out again.
        Assert.False(previous.TryGetInstance(first.AgentId, out _));
        var next = await Spawn(rebuilt, "Worker", "Write the release notes for the next version.");
        Assert.NotEqual(first.AgentId, next.AgentId);
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
    public void One_shared_file_is_enough_and_one_shared_term_is_not()
    {
        // The threshold is 2: a file counts 3, a bare identifier 1.
        Assert.Equal(3, SubAgentAffinity.Score(
            "centre the count in SubAgentView.axaml", SubAgentAffinity.Read("SubAgentView.axaml")));
        Assert.Equal(1, SubAgentAffinity.Score("refactor the composer", SubAgentAffinity.Read("the composer split")));
        Assert.Equal(2, SubAgentAffinity.Score("refactor composer split", SubAgentAffinity.Read("composer split")));
        // A file named in the candidate's answer counts like one named in its task: the report is where a child says what it
        // touched.
        Assert.Equal(3, SubAgentAffinity.Score("fix the badge in SubAgentView.axaml", SubAgentAffinity.Read("I changed SubAgentView.axaml.")));
        // A Chinese sentence has no spaces, so its terms are the adjacent character pairs: two tasks on one topic share
        // several and clear the threshold, one shared pair does not.
        Assert.Equal(2, SubAgentAffinity.Score("把徽标居中", SubAgentAffinity.Read("徽标要居中显示")));
        Assert.Equal(1, SubAgentAffinity.Score("修复徽标", SubAgentAffinity.Read("徽标旁边加个提示")));
        // One CJK character alone names nothing.
        Assert.Equal(0, SubAgentAffinity.Score("字", SubAgentAffinity.Read("字")));
        // A verdict line says how a review ended, not what it was about: it never counts as topic evidence.
        Assert.True(SubAgentAffinity.Read("**verdict**: approve").IsEmpty);
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
        ModelSettings? model = null,
        string? resumeId = null) =>
        scheduler.SpawnAsync(
            kind,
            task,
            resumeId: resumeId,
            new ToolContext("session", "wd", 0),
            new Sink(),
            "main",
            CancellationToken.None,
            model: model,
            reuse: reuse);
}
