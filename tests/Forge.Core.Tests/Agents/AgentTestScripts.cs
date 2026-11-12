using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>The two-turn provider scripts and the run-to-completion assertion the agent tests share.</summary>
internal static class AgentTestScripts
{
    /// <summary>A provider that answers the first turn with one tool call (<paramref name="toolName"/>,
    /// <paramref name="callId"/>, <paramref name="arguments"/>) and every later turn with <paramref name="finalText"/>.</summary>
    public static FakeProvider OneToolCallThenSay(
        string toolName,
        string finalText = "done",
        string callId = "call-1",
        string arguments = "{}") =>
        new((_, index) => index == 0 ? Turn.Call((callId, toolName, arguments)) : Turn.Say(finalText));

    /// <summary>Runs <paramref name="prompt"/> on <paramref name="root"/> to completion — bounded by
    /// <paramref name="timeoutSeconds"/> so a hang fails instead of blocking — and asserts the run reported no
    /// <see cref="AgentFailed"/>; returns every event the run emitted.</summary>
    public static async Task<List<AgentEvent>> RunToCompletionAsync(
        this AgentHost host,
        AgentInstance root,
        string prompt = "go",
        int timeoutSeconds = 15)
    {
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, prompt))
            .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        Assert.Empty(events.OfType<AgentFailed>());
        return events;
    }
}
