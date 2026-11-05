using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Terminal start and the platform default shell, driven over a raw peer against a real host:
/// a shell starts, its answer names a real executable, and input is echoed back.</summary>
public sealed class TerminalStartTests
{
    private static readonly TimeSpan OutputLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Start_runs_a_shell_and_echoes_input()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        var (terminalId, processId, shell) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);

        Assert.False(string.IsNullOrEmpty(terminalId), "terminal.start answered without a terminal id.");
        Assert.True(processId > 0, $"terminal.start answered processId {processId}.");
        Assert.False(string.IsNullOrEmpty(shell), "terminal.start answered without a shell path.");
        Assert.True(File.Exists(shell), $"terminal.start resolved the shell to '{shell}', which names no file.");
        Assert.True(E2eFixture.IsRunning(processId), $"the shell (pid {processId}) is not running.");

        await host.SendInputAsync(terminalId, "echo forge-probe\r", ct);
        await host.WaitForTextAsync(terminalId, "forge-probe", OutputLimit, ct);

        Assert.NotEmpty(host.FramesOf(terminalId));
        Assert.Empty(host.Problems());
    }

    [Fact]
    public async Task Start_without_a_shell_uses_the_platform_default()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        // The shell member is omitted entirely (not sent as null), so the host applies its default resolution.
        var (terminalId, processId, shell) = await host.StartTerminalAsync(shell: null, ct: ct);

        Assert.False(string.IsNullOrEmpty(terminalId), "terminal.start answered without a terminal id.");
        Assert.True(processId > 0, $"terminal.start answered processId {processId}.");
        Assert.False(string.IsNullOrEmpty(shell), "the host resolved no default shell.");
        Assert.True(File.Exists(shell), $"the default shell '{shell}' names no file.");

        await host.SendInputAsync(terminalId, "echo forge-default-shell\r", ct);
        await host.WaitForTextAsync(terminalId, "forge-default-shell", OutputLimit, ct);
    }

    [Fact]
    public async Task Start_with_a_missing_executable_fails_with_terminal_start_failed()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        var bare = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            host.StartTerminalAsync("forge-definitely-not-a-shell-xyz123", ct: ct));
        Assert.Equal("terminal_start_failed", bare.Code);

        var missingPath = Path.Combine(host.Root, "no-such-shell.exe");
        var pathed = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            host.StartTerminalAsync(missingPath, ct: ct));
        Assert.Equal("terminal_start_failed", pathed.Code);
    }
}
