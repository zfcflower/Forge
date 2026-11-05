using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>The per-host terminal cap over a real host: 32 terminals start, the 33rd is refused with
/// <c>terminal_limit</c>, and stopping one frees its slot.</summary>
public sealed class TerminalLimitTests
{
    [Fact]
    public async Task Thirty_two_terminals_start_the_thirty_third_is_limited_and_a_slot_is_reusable()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        var ids = new List<string>(32);
        for (var index = 0; index < 32; index++)
        {
            var (terminalId, processId, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
            Assert.True(processId > 0, $"terminal {index} answered processId {processId}.");
            ids.Add(terminalId);
        }

        Assert.Equal(32, ids.Distinct(StringComparer.Ordinal).Count());

        var limited = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            host.StartTerminalAsync(TerminalShells.Fast, ct: ct));
        Assert.Equal("terminal_limit", limited.Code);

        await host.StopAsync(ids[0], ct);
        var (replacement, replacementPid, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
        Assert.True(replacementPid > 0, $"the replacement terminal answered processId {replacementPid}.");
        Assert.DoesNotContain(replacement, ids);
    }
}
