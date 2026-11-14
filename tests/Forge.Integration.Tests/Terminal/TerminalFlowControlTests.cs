using Xunit;
using Xunit.Abstractions;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Flow control over a real host. While the app keeps acknowledging, megabytes arrive with no
/// loss and no reorder; while it does not, delivery stops at the 1 MiB unacknowledged window and never
/// grows past it.
/// </summary>
public sealed class TerminalFlowControlTests
{
    private const int FloodLines = 20000;
    private const int FillLines = 30000;

    private readonly ITestOutputHelper output;

    public TerminalFlowControlTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Continuous_ack_delivers_megabytes_losslessly_in_order()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);

        string doneMarker;
        if (OperatingSystem.IsWindows())
        {
            await host.WaitForTextAsync(terminalId, "PS", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(terminalId, TerminalFlood.Command(FloodLines), ct);
        }
        else
        {
            await host.SendInputAsync(terminalId, "echo FLOW-READY\r", ct);
            await host.WaitForTextAsync(terminalId, "FLOW-READY", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(terminalId, TerminalFlood.Command(FloodLines), ct);
        }

        doneMarker = TerminalFlood.DoneMarker(FloodLines);

        await host.WaitForTextAsync(terminalId, $"LINE-{FloodLines:0000000}", TimeSpan.FromSeconds(120), ct);
        await host.WaitForTextAsync(terminalId, doneMarker, TimeSpan.FromSeconds(60), ct);

        var frames = host.FramesOf(terminalId);
        Assert.True(host.ByteCountOf(terminalId) > 1_000_000, $"expected megabytes, saw {host.ByteCountOf(terminalId)} bytes.");
        TerminalAsserts.SequenceIsContinuous(frames, terminalId);
        TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FloodLines, doneMarker);
        Assert.Empty(host.Problems());
    }

    [Fact]
    public async Task Unacked_window_stops_delivery_and_ack_resumes()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        host.AutoAck = false;
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);

        if (OperatingSystem.IsWindows())
        {
            await host.WaitForTextAsync(terminalId, "PS", TimeSpan.FromSeconds(30), ct);
        }
        else
        {
            await host.SendInputAsync(terminalId, "echo FLOW-READY\r", ct);
            await host.WaitForTextAsync(terminalId, "FLOW-READY", TimeSpan.FromSeconds(30), ct);
        }

        var acknowledged = host.ByteCountOf(terminalId);
        await host.AckAsync(terminalId, acknowledged, ct);
        var floor = host.ByteCountOf(terminalId);

        await host.SendInputAsync(terminalId, TerminalFlood.Command(FillLines), ct);
        var doneMarker = TerminalFlood.DoneMarker(FillLines);

        // The window fills: delivery reaches ~1 MiB past the floor and then freezes.
        var frozenAt = await WaitForFrozenAsync(host, terminalId, floor, TimeSpan.FromSeconds(90), ct);
        Assert.True(
            frozenAt - floor >= 1_000_000,
            $"delivery froze after only {frozenAt - floor} bytes past the acknowledged floor; the 1 MiB window was never reached.");

        // Acknowledging reopens the window.
        await host.AckAsync(terminalId, host.ByteCountOf(terminalId), ct);
        host.AutoAck = true;

        if (OperatingSystem.IsWindows())
        {
            // Hard assertion: after the window filled, acknowledging must let the rest of the burst through on ConPTY exactly
            // as on POSIX. An earlier version of this test treated "did not resume within 30 s" as a ConPTY limit; experiments
            // (repeated runs, both the real pwsh and the Store alias first on PATH) never reproduced a stall (the slowest
            // run took ~30 s end to end), so the old 30 s poll was most likely just too short for a loaded machine.
            await host.WaitForTextAsync(terminalId, doneMarker, TimeSpan.FromSeconds(120), ct);
            TerminalAsserts.SequenceIsContinuous(host.FramesOf(terminalId), terminalId);
            TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FillLines, doneMarker);
        }
        else
        {
            await host.WaitForTextAsync(terminalId, $"LINE-{FillLines:0000000}", TimeSpan.FromSeconds(90), ct);
            await host.WaitForTextAsync(terminalId, doneMarker, TimeSpan.FromSeconds(60), ct);
            TerminalAsserts.SequenceIsContinuous(host.FramesOf(terminalId), terminalId);
            TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FillLines, doneMarker);
        }

        await host.StopAsync(terminalId, ct);
        await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Polls until the delivered byte count stops growing for 3 s straight, and returns that count.</summary>
    private static async Task<long> WaitForFrozenAsync(
        RawTerminalHost host, string terminalId, long floor, TimeSpan limit, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < limit)
        {
            var first = host.ByteCountOf(terminalId);
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var second = host.ByteCountOf(terminalId);
            if (first == second && first - floor >= 1_000_000)
            {
                return second;
            }
        }

        throw new TimeoutException(
            $"Timed out after {limit.TotalSeconds:0} s waiting for delivery to freeze at the window; last count was {host.ByteCountOf(terminalId)}.");
    }
}
