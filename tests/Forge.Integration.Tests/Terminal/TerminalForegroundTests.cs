using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Foreground detection over a real host: an idle shell is not busy, a shell with a live
/// child is, and an unknown id reports false.</summary>
public sealed class TerminalForegroundTests
{
    [Fact]
    public async Task Foreground_reports_idle_busy_and_unknown()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);

        await host.SendInputAsync(terminalId, "echo FG-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "FG-READY", TimeSpan.FromSeconds(30), ct);
        Assert.False(
            await host.ForegroundAsync(terminalId, ct),
            "an idle shell must report busy=false.");

        if (OperatingSystem.IsWindows())
        {
            await host.SendInputAsync(
                terminalId,
                "Start-Process -FilePath powershell.exe -ArgumentList '-NoExit','-Command','Start-Sleep 30'\r",
                ct);
        }
        else
        {
            // A foreground sleep owns the terminal (its own process group under job control),
            // so tcgetpgrp(master) differs from the shell's group; verified against dash in WSL.
            await host.SendInputAsync(terminalId, "sleep 30\r", ct);
        }

        await TerminalWait.WaitUntilAsync(
            async () => await host.ForegroundAsync(terminalId, ct),
            "the shell with a foreground child to report busy=true",
            TimeSpan.FromSeconds(15),
            ct);

        Assert.False(
            await host.ForegroundAsync("00000000000000000000000000000000", ct),
            "an unknown terminal id must report busy=false.");
    }
}
