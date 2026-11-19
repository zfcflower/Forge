using Forge.App.Agents;
using Xunit;

namespace Forge.App.Tests;

// Which paths count as a prompt source. The signalling side (a real FileSystemWatcher: a folder created after Arm, one
// signal per Arm, Dispose not waiting on a running handler) has no test host here — it needs an injection point for
// change events, and a test that waits on the OS notifier's own timing is not a test.
public sealed class PromptSourceWatcherTests
{
    private const string Root = "/work/.forge";

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
        Assert.Equal(expected, PromptSourceWatcher.Relevant(Root, Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))));
    }
}
