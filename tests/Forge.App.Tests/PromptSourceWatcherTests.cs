using Forge.App.Agents;
using Xunit;

namespace Forge.App.Tests;

public sealed class PromptSourceWatcherTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-watch-" + Guid.NewGuid().ToString("n"));

    public PromptSourceWatcherTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("agents/scout.md", true)]
    [InlineData("commands/x.md", true)]
    [InlineData("prompts/a/b.md", true)]
    [InlineData("skills/s/helper.py", true)]
    [InlineData("agents/scout.bak", false)]
    [InlineData("agents", true)]
    [InlineData("skills", true)]
    [InlineData("AGENTS.md", true)]
    [InlineData("logs/today.md", false)]
    [InlineData("sessions/x.db", false)]
    public void Relevant_accepts_prompt_sources_and_whole_folder_events(string relative, bool expected)
    {
        var root = Path.Combine(dir, ".forge");
        Assert.Equal(expected, PromptSourceWatcher.Relevant(root, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static async Task<bool> SignalledAsync(Task signalled) => await Task.WhenAny(signalled, Task.Delay(TimeSpan.FromSeconds(15))) == signalled;

    [Fact]
    public async Task A_folder_created_after_the_watch_started_is_noticed()
    {
        var workspace = Path.Combine(dir, "ws");
        Directory.CreateDirectory(workspace);
        using var watcher = new PromptSourceWatcher(null, workspace);
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += () => signalled.TrySetResult();
        watcher.Arm();

        Directory.CreateDirectory(Path.Combine(workspace, ".forge", "agents"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".forge", "agents", "x.md"), "text");

        Assert.True(await SignalledAsync(signalled.Task));
    }

    [Fact]
    public async Task A_change_inside_an_existing_folder_signals_once_per_arm()
    {
        var workspace = Path.Combine(dir, "ws2");
        var agents = Path.Combine(workspace, ".forge", "agents");
        Directory.CreateDirectory(agents);
        using var watcher = new PromptSourceWatcher(null, workspace);
        var count = 0;
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += () =>
        {
            Interlocked.Increment(ref count);
            signalled.TrySetResult();
        };
        watcher.Arm();

        await File.WriteAllTextAsync(Path.Combine(agents, "a.md"), "one");
        await File.WriteAllTextAsync(Path.Combine(agents, "a.md"), "two");
        Assert.True(await SignalledAsync(signalled.Task));
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref count));
    }
}
