using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins <see cref="AgentInstance.Interject"/>: delivered at the next tool-batch boundary, nothing skipped, leftovers kept.</summary>
public sealed class RootInterjectionTests
{
    private static string Workspace => Path.GetTempPath();

    [Fact]
    public async Task An_interjection_lands_after_the_running_tool_batch_without_dropping_calls()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = 0;
        var tools = new List<ITool>
        {
            new FakeTool("slow", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new ToolResult("slow ok");
            }),
            new FakeTool("fast", ToolSideEffect.ReadOnly, (_, _, _) =>
            {
                Interlocked.Increment(ref secondRan);
                return new ValueTask<ToolResult>(new ToolResult("fast ok"));
            }),
        };
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("c1", "slow", "{}"), ("c2", "fast", "{}"))
            : Turn.Say("saw: " + Turn.LastUserText(request)));
        var host = AgentTestHost.Create(provider, tools);
        var root = host.CreateRoot("session-interject-" + Guid.NewGuid().ToString("n"), Workspace);

        var run = Task.Run(() => AgentTestHost.CollectAsync(host.RunAsync(root, "go")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        root.Interject("also check the tests");
        Assert.True(root.HasInterjections);
        release.TrySetResult();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var interjected = Assert.Single(events.OfType<AgentInterjected>());
        Assert.Equal("root", interjected.AgentId);
        Assert.Equal("also check the tests", interjected.Text);
        Assert.Equal(1, Volatile.Read(ref secondRan));
        Assert.False(root.HasInterjections);

        var second = provider.Requests[1];
        Assert.Equal("also check the tests", Turn.LastUserText(second));
        Assert.Equal(MessageRole.Tool, second.Messages[^2].Role);
        Assert.Equal(2, second.Messages[^2].Content.OfType<ToolResultBlock>().Count());
        Assert.Equal("saw: also check the tests", Turn.Text(root.History[^1]));
    }

    [Fact]
    public async Task Several_interjections_become_one_user_message_in_order()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new List<ITool>
        {
            new FakeTool("slow", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new ToolResult("ok");
            }),
        };
        var provider = new FakeProvider((_, index) => index == 0 ? Turn.Call(("c1", "slow", "{}")) : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, tools);
        var root = host.CreateRoot("session-interject-" + Guid.NewGuid().ToString("n"), Workspace);

        var run = Task.Run(() => AgentTestHost.CollectAsync(host.RunAsync(root, "go")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        root.Interject("first");
        root.Interject("second");
        release.TrySetResult();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(["first", "second"], events.OfType<AgentInterjected>().Select(static e => e.Text));
        Assert.Equal("first\n\nsecond", Turn.LastUserText(provider.Requests[1]));
    }

    [Fact]
    public async Task A_run_without_another_tool_batch_leaves_the_interjection_for_the_host()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("done"));
        var host = AgentTestHost.Create(provider);
        var root = host.CreateRoot("session-interject-" + Guid.NewGuid().ToString("n"), Workspace);

        root.Interject("late");
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentInterjected>());
        Assert.Equal(["late"], root.TakeInterjections());
        Assert.Empty(root.TakeInterjections());
        Assert.False(root.HasInterjections);
    }

    [Fact]
    public void Interject_rejects_blank_text()
    {
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")));
        var root = host.CreateRoot("session-interject-blank", Workspace);

        Assert.ThrowsAny<ArgumentException>(() => root.Interject("  "));
        Assert.False(root.HasInterjections);
    }
}
