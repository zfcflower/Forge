using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Host shutdown over a real host: <c>host.stop</c> kills every terminal's tree, the host exits
/// on its own with code 0, and its output carries no cleanup failure. Exit code 0 is the SPEC's
/// verifiable-cleanup signal: a non-empty cleanup report would end the host non-zero instead.</summary>
public sealed class TerminalShutdownTests
{
    [Fact]
    public async Task Host_stop_kills_all_terminals_and_exits_zero_without_cleanup_failures()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        var (_, firstPid, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
        int grandchildPid;
        if (OperatingSystem.IsWindows())
        {
            var (secondId, secondPid, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);
            await host.WaitForTextAsync(secondId, "PS", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(
                secondId,
                TerminalSleeper.SpawnCommand(Path.Combine(host.Root, "shutdown-child.pid")),
                ct);
            grandchildPid = await TerminalWait.WaitForPidFileAsync(host.Root, "shutdown-child.pid", TimeSpan.FromSeconds(30), ct);
            Assert.True(E2eFixture.IsRunning(secondPid), $"the second shell (pid {secondPid}) is not running.");
        }
        else
        {
            // Default interactive shell: job control is on, so the background sleep owns a process
            // group of its own inside the shell's session; host.stop must sweep the session to take it.
            var (secondId, secondPid, _) = await host.StartTerminalAsync("/bin/sh", ct: ct);
            await host.SendInputAsync(
                secondId,
                TerminalSleeper.SpawnCommand(Path.Combine(host.Root, "shutdown-child.pid")),
                ct);
            grandchildPid = await TerminalWait.WaitForPidFileAsync(host.Root, "shutdown-child.pid", TimeSpan.FromSeconds(30), ct);
            Assert.True(E2eFixture.IsRunning(secondPid), $"the second shell (pid {secondPid}) is not running.");
        }

        Assert.True(E2eFixture.IsRunning(firstPid), $"the first shell (pid {firstPid}) is not running.");
        Assert.True(E2eFixture.IsRunning(grandchildPid), $"the grandchild (pid {grandchildPid}) is not running.");

        try
        {
            await host.StopHostAsync(ct);
        }
        catch (IpcConnectionClosedException)
        {
            // The host may close its pipe before answering host.stop; the exit is what this test asserts.
        }

        var exitCode = await host.WaitForExitAsync(ct);
        Assert.Equal(0, exitCode);

        await TerminalWait.WaitForDeathAsync(firstPid, "the first shell of the stopped host", ct);
        await TerminalWait.WaitForDeathAsync(grandchildPid, "the grandchild of the stopped host", ct);

        TerminalAsserts.NoCleanupFailures(host);
    }
}
