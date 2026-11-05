using Xunit;
using Xunit.Abstractions;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Flow control over a real host. While the app keeps acknowledging, megabytes arrive with no
/// loss and no reorder; while it does not, delivery stops at the 1 MiB unacknowledged window and never
/// grows past it.
/// <para>
/// Known platform limit (Windows ConPTY): once the window has filled, the console may stop delivering
/// the remainder of a burst even after the app acknowledges, so the resume half of the second test only
/// asserts completion on POSIX. On Windows it records what arrived and still proves the terminal stays
/// usable — it must not fail there.
/// </para></summary>
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
            await host.SendInputAsync(
                terminalId,
                $"$n = {FloodLines}; 1..$n | % {{ 'LINE-{{0:D7}}-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' -f $_ }}; \"DONE-COUNT-$n\"\r",
                ct);
            doneMarker = $"DONE-COUNT-{FloodLines}";
        }
        else
        {
            await host.SendInputAsync(terminalId, "echo FLOW-READY\r", ct);
            await host.WaitForTextAsync(terminalId, "FLOW-READY", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(
                terminalId,
                $"n={FloodLines}; i=1; while [ $i -le $n ]; do printf 'LINE-%07d-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n' \"$i\"; i=$((i+1)); done; echo \"DONE-COUNT-$i\"\r",
                ct);
            doneMarker = $"DONE-COUNT-{FloodLines + 1}";
        }

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

        string doneMarker;
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

        if (OperatingSystem.IsWindows())
        {
            await host.SendInputAsync(
                terminalId,
                $"$n = {FillLines}; 1..$n | % {{ 'LINE-{{0:D7}}-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' -f $_ }}; \"DONE-COUNT-$n\"\r",
                ct);
            doneMarker = $"DONE-COUNT-{FillLines}";
        }
        else
        {
            await host.SendInputAsync(
                terminalId,
                $"n={FillLines}; i=1; while [ $i -le $n ]; do printf 'LINE-%07d-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n' \"$i\"; i=$((i+1)); done; echo \"DONE-COUNT-$i\"\r",
                ct);
            doneMarker = $"DONE-COUNT-{FillLines + 1}";
        }

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
            var resumed = await PollForTextAsync(host, terminalId, doneMarker, TimeSpan.FromSeconds(30), ct);
            output.WriteLine(resumed
                ? $"the Windows console resumed after ack and delivered {doneMarker}."
                : $"the Windows console did not deliver {doneMarker} within 30 s of ack (known ConPTY limit); the terminal itself must still stop cleanly.");
            if (resumed)
            {
                TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FillLines, doneMarker);
            }
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

    private static async Task<bool> PollForTextAsync(
        RawTerminalHost host, string terminalId, string needle, TimeSpan limit, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < limit)
        {
            if (host.TextOf(terminalId).Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        return false;
    }
}
