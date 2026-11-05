using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Output framing over a real host: <c>seq</c> runs 0,1,2… with no gap across many frames,
/// and the concatenated bytes hold the stream in order.</summary>
public sealed class TerminalOutputTests
{
    private const int FloodLines = 3000;

    [Fact]
    public async Task Output_frames_are_sequenced_from_zero_and_concatenate_in_order()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);

        if (OperatingSystem.IsWindows())
        {
            await host.WaitForTextAsync(terminalId, "PS", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(
                terminalId,
                $"$n = {FloodLines}; 1..$n | % {{ 'LINE-{{0:D7}}-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' -f $_ }}; \"DONE-COUNT-$n\"\r",
                ct);
            await host.WaitForTextAsync(terminalId, $"LINE-{FloodLines:0000000}", TimeSpan.FromSeconds(90), ct);
            await host.WaitForTextAsync(terminalId, $"DONE-COUNT-{FloodLines}", TimeSpan.FromSeconds(30), ct);
            TerminalAsserts.SequenceIsContinuous(host.FramesOf(terminalId), terminalId);
            TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FloodLines, $"DONE-COUNT-{FloodLines}");
        }
        else
        {
            await host.SendInputAsync(terminalId, "echo SEQ-READY\r", ct);
            await host.WaitForTextAsync(terminalId, "SEQ-READY", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(
                terminalId,
                $"n={FloodLines}; i=1; while [ $i -le $n ]; do printf 'LINE-%07d-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n' \"$i\"; i=$((i+1)); done; echo \"DONE-COUNT-$i\"\r",
                ct);
            await host.WaitForTextAsync(terminalId, $"LINE-{FloodLines:0000000}", TimeSpan.FromSeconds(90), ct);
            await host.WaitForTextAsync(terminalId, $"DONE-COUNT-{FloodLines + 1}", TimeSpan.FromSeconds(30), ct);
            TerminalAsserts.SequenceIsContinuous(host.FramesOf(terminalId), terminalId);
            TerminalAsserts.FloodIsLosslessInOrder(host.TextOf(terminalId), FloodLines, $"DONE-COUNT-{FloodLines + 1}");
        }

        Assert.Empty(host.Problems());
    }
}
