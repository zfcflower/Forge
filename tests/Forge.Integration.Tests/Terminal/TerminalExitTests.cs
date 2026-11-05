using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Shell exit over a real host: the exit code is reported, and <c>terminal.exit</c> is the
/// id's last message — nothing for that id arrives after it.</summary>
public sealed class TerminalExitTests
{
    [Fact]
    public async Task Shell_exit_reports_its_code_and_exit_is_the_last_message()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, processId, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);

        await host.SendInputAsync(terminalId, "exit 3\r", ct);
        var exitCode = await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct);
        Assert.Equal(3, exitCode);

        var frames = host.FramesOf(terminalId);
        Assert.NotEmpty(frames);
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.Equal(index, frames[index].Seq);
        }

        var events = host.EventsOf(terminalId);
        Assert.NotEmpty(events);
        Assert.StartsWith("exit", events[^1], StringComparison.Ordinal);

        var eventCount = events.Count;
        var frameCount = frames.Count;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        Assert.Equal(eventCount, host.EventsOf(terminalId).Count);
        Assert.Equal(frameCount, host.FramesOf(terminalId).Count);

        await TerminalWait.WaitForDeathAsync(processId, "the exited shell", ct);
        Assert.Empty(host.Problems());
    }
}
