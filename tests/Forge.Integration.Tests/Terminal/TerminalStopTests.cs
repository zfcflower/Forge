using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Stopping over a real host: the whole tree (shell and grandchild) disappears,
/// <c>terminal.exit</c> reaches the app before the stop answer, and a second stop is idempotent.
/// POSIX shells run with job control on (the host's fixed defect used to leave such jobs behind),
/// so the background job below owns a process group of its own inside the shell's session.</summary>
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
            // Default interactive shell: job control (monitor mode) is on, so the background sleep
            // gets a process group of its own inside the shell's session (verified against dash in WSL).
            // The host's POSIX tree kill sweeps the whole session, which is what takes it; kill(-pgid)
            // of the shell's group alone would leave it running with the slave held open.
            arguments = null;
            spawnCommand = "sleep 60 & echo $! > '" + Marker(host) + "'\r";
        }

        var (terminalId, shellPid, _) = await host.StartTerminalAsync(shell, arguments, ct: ct);
        await host.SendInputAsync(terminalId, "echo STOP-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "STOP-READY", TimeSpan.FromSeconds(30), ct);

        await host.SendInputAsync(terminalId, spawnCommand, ct);
        var grandchildPid = await WaitForMarkerPidAsync(host, "grandchild.pid", TimeSpan.FromSeconds(30), ct);
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

    /// <summary>A background job under job control owns a process group of its own inside the shell's
    /// session, so only a session-wide sweep takes it: killing the shell's group alone leaves it running
    /// (and holding the slave, so the master never sees EOF). This test pins that sweep with a nested
    /// tree — a background <c>sh -c</c> job plus the <c>sleep</c> it spawns — and checks the shell and the
    /// job both disappear, <c>terminal.exit</c> precedes the stop answer, and the host reports no cleanup
    /// failure. On Windows the same assertions run over the Job Object containment, unchanged.</summary>
    [Fact]
    public async Task Stop_kills_a_job_control_background_job_with_its_own_process_group()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        string shell;
        string[]? arguments;
        string spawnCommand;
        const string markerFile = "job.pid";
        if (OperatingSystem.IsWindows())
        {
            shell = TerminalShells.Rich();
            arguments = null;
            // The child inherits the shell's private Job Object, so killing the job kills it too.
            spawnCommand =
                "$p = Start-Process -FilePath powershell.exe -ArgumentList '-NoExit','-Command','Start-Sleep 60' -PassThru; " +
                "$p.Id | Out-File -Encoding ascii '" + JobMarker(host) + "'\r";
        }
        else
        {
            shell = "/bin/sh";
            // Default interactive shell: job control is on, so this background job (and the sleep it
            // spawns) lives in a process group of its own inside the shell's session.
            arguments = null;
            spawnCommand = "sh -c 'echo $$ > \"" + JobMarker(host) + "\"; sleep 300' &\r";
        }

        var (terminalId, shellPid, _) = await host.StartTerminalAsync(shell, arguments, ct: ct);
        await host.SendInputAsync(terminalId, "echo JOB-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "JOB-READY", TimeSpan.FromSeconds(30), ct);

        await host.SendInputAsync(terminalId, spawnCommand, ct);
        var jobPid = await WaitForMarkerPidAsync(host, markerFile, TimeSpan.FromSeconds(30), ct);
        Assert.NotEqual(shellPid, jobPid);
        Assert.True(E2eFixture.IsRunning(shellPid), $"the shell (pid {shellPid}) is not running.");
        Assert.True(E2eFixture.IsRunning(jobPid), $"the background job (pid {jobPid}) is not running.");

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
        await TerminalWait.WaitForDeathAsync(jobPid, "the background job of the stopped shell", ct);

        var failures = host.HostOutput
            .Where(line =>
                line.Contains("terminal", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("did not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("still running", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.Empty(failures);
    }

    private static string JobMarker(RawTerminalHost host) =>
        Path.Combine(host.Root, "job.pid").Replace('\\', '/');

    private static async Task<int> WaitForMarkerPidAsync(RawTerminalHost host, string fileName, TimeSpan limit, CancellationToken ct)
    {
        var marker = Path.Combine(host.Root, fileName);
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
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for the pid file '{marker}'.");
            }
        }
    }
}
