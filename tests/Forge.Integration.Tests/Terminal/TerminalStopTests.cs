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

        // Windows: the child inherits the shell's private Job Object, so killing the job kills it too.
        // POSIX: the default interactive shell runs with job control on, so the background sleep
        // gets a process group of its own inside the shell's session (verified against dash in WSL).
        // The host's POSIX tree kill sweeps the whole session, which is what takes it; kill(-pgid)
        // of the shell's group alone would leave it running with the slave held open.
        var shell = OperatingSystem.IsWindows() ? TerminalShells.Rich() : "/bin/sh";
        var spawnCommand = TerminalSleeper.SpawnCommand(Marker(host));

        var (terminalId, shellPid, _) = await host.StartTerminalAsync(shell, ct: ct);
        await host.SendInputAsync(terminalId, "echo STOP-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "STOP-READY", TimeSpan.FromSeconds(30), ct);

        await host.SendInputAsync(terminalId, spawnCommand, ct);
        var grandchildPid = await TerminalWait.WaitForPidFileAsync(host.Root, "grandchild.pid", TimeSpan.FromSeconds(30), ct);
        Assert.True(E2eFixture.IsRunning(shellPid), $"the shell (pid {shellPid}) is not running.");
        Assert.True(E2eFixture.IsRunning(grandchildPid), $"the grandchild (pid {grandchildPid}) is not running.");

        await StopAndAssertExitOrderAsync(host, terminalId, ct);

        await TerminalWait.WaitForDeathAsync(shellPid, "the stopped shell", ct);
        await TerminalWait.WaitForDeathAsync(grandchildPid, "the grandchild of the stopped shell", ct);

        // Idempotent: stopping again succeeds and sends no second exit.
        await host.StopAsync(terminalId, ct);
        Assert.Single(host.EventsOf(terminalId), line => line.StartsWith("exit", StringComparison.Ordinal));
    }

    private static string Marker(RawTerminalHost host) =>
        Path.Combine(host.Root, "grandchild.pid");

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
        string spawnCommand;
        const string markerFile = "job.pid";
        if (OperatingSystem.IsWindows())
        {
            // The child inherits the shell's private Job Object, so killing the job kills it too.
            shell = TerminalShells.Rich();
            spawnCommand = TerminalSleeper.SpawnCommand(JobMarker(host));
        }
        else
        {
            shell = "/bin/sh";
            // Default interactive shell: job control is on, so this background job (and the sleep it
            // spawns) lives in a process group of its own inside the shell's session.
            spawnCommand = "sh -c 'echo $$ > \"" + JobMarker(host) + "\"; sleep 300' &\r";
        }

        var (terminalId, shellPid, _) = await host.StartTerminalAsync(shell, ct: ct);
        await host.SendInputAsync(terminalId, "echo JOB-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "JOB-READY", TimeSpan.FromSeconds(30), ct);

        await host.SendInputAsync(terminalId, spawnCommand, ct);
        var jobPid = await TerminalWait.WaitForPidFileAsync(host.Root, markerFile, TimeSpan.FromSeconds(30), ct);
        Assert.NotEqual(shellPid, jobPid);
        Assert.True(E2eFixture.IsRunning(shellPid), $"the shell (pid {shellPid}) is not running.");
        Assert.True(E2eFixture.IsRunning(jobPid), $"the background job (pid {jobPid}) is not running.");

        await StopAndAssertExitOrderAsync(host, terminalId, ct);

        await TerminalWait.WaitForDeathAsync(shellPid, "the stopped shell", ct);
        await TerminalWait.WaitForDeathAsync(jobPid, "the background job of the stopped shell", ct);

        TerminalAsserts.NoCleanupFailures(host);
    }

    private static string JobMarker(RawTerminalHost host) =>
        Path.Combine(host.Root, "job.pid").Replace('\\', '/');

    /// <summary>Stops the terminal and pins the SPEC ordering: <c>terminal.exit</c> is sent after the stop
    /// was sent but before the stop is answered.</summary>
    private static async Task StopAndAssertExitOrderAsync(RawTerminalHost host, string terminalId, CancellationToken ct)
    {
        Assert.Null(host.ExitTicks(terminalId));
        var before = DateTime.UtcNow.Ticks;
        await host.StopAsync(terminalId, ct);
        var answered = DateTime.UtcNow.Ticks;

        await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct);
        var exited = host.ExitTicks(terminalId);
        Assert.NotNull(exited);
        Assert.True(before <= exited!.Value, "terminal.exit arrived before terminal.stop was even sent.");
        Assert.True(exited.Value <= answered, "terminal.exit must arrive before the terminal.stop answer.");
    }
}
