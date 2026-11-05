using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Stopping over a real host: the whole tree (shell and grandchild) disappears,
/// <c>terminal.exit</c> reaches the app before the stop answer, and a second stop is idempotent.</summary>
public sealed class TerminalStopTests
{
    [Fact]
    public async Task Stop_kills_the_tree_reports_exit_before_answering_and_is_idempotent()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        string shell;
        string[]? arguments;
        string spawnCommand;
        if (OperatingSystem.IsWindows())
        {
            shell = TerminalShells.Rich();
            arguments = null;
            // The child inherits the shell's private Job Object, so killing the job kills it too.
            spawnCommand =
                "$p = Start-Process -FilePath powershell.exe -ArgumentList '-NoExit','-Command','Start-Sleep 60' -PassThru; " +
                "$p.Id | Out-File -Encoding ascii '" + Marker(host) + "'\r";
        }
        else
        {
            shell = "/bin/sh";
            // +m disables job control, so the background sleep stays in the shell's process group and
            // kill(-pgid) — the host's POSIX tree kill — reaches it. With job control on, dash would
            // place it in its own group and it would survive; both shapes were probed in WSL.
            arguments = ["+m"];
            spawnCommand = "sleep 60 & echo $! > '" + Marker(host) + "'\r";
        }

        var (terminalId, shellPid, _) = await host.StartTerminalAsync(shell, arguments, ct: ct);
        await host.SendInputAsync(terminalId, "echo STOP-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "STOP-READY", TimeSpan.FromSeconds(30), ct);

        await host.SendInputAsync(terminalId, spawnCommand, ct);
        var grandchildPid = await WaitForMarkerPidAsync(host, TimeSpan.FromSeconds(30), ct);
        Assert.True(E2eFixture.IsRunning(shellPid), $"the shell (pid {shellPid}) is not running.");
        Assert.True(E2eFixture.IsRunning(grandchildPid), $"the grandchild (pid {grandchildPid}) is not running.");

        Assert.Null(host.ExitTicks(terminalId));
        var before = DateTime.UtcNow.Ticks;
        await host.StopAsync(terminalId, ct);
        var answered = DateTime.UtcNow.Ticks;

        await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct);
        var exited = host.ExitTicks(terminalId);
        Assert.NotNull(exited);
        // SPEC order: terminal.exit is sent before terminal.stop is answered.
        Assert.True(before <= exited!.Value, "terminal.exit arrived before terminal.stop was even sent.");
        Assert.True(exited.Value <= answered, "terminal.exit must arrive before the terminal.stop answer.");

        await TerminalWait.WaitForDeathAsync(shellPid, "the stopped shell", ct);
        await TerminalWait.WaitForDeathAsync(grandchildPid, "the grandchild of the stopped shell", ct);

        // Idempotent: stopping again succeeds and sends no second exit.
        await host.StopAsync(terminalId, ct);
        Assert.Single(host.EventsOf(terminalId), line => line.StartsWith("exit", StringComparison.Ordinal));
    }

    private static string Marker(RawTerminalHost host) =>
        Path.Combine(host.Root, "grandchild.pid").Replace('\\', '/');

    private static async Task<int> WaitForMarkerPidAsync(RawTerminalHost host, TimeSpan limit, CancellationToken ct)
    {
        var marker = Path.Combine(host.Root, "grandchild.pid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            if (File.Exists(marker))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(marker).Trim(), out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The shell still holds the file open for writing; keep polling.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for the grandchild pid file '{marker}'.");
            }
        }
    }
}
