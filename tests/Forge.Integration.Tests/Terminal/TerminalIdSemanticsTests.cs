using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Id semantics over a real host: unknown ids and exited ids answer resize with success,
/// report no foreground process, tolerate stop, and silently drop input — never an error.</summary>
public sealed class TerminalIdSemanticsTests
{
    private const string UnknownId = "00000000000000000000000000000000";

    [Fact]
    public async Task Unknown_and_exited_ids_are_success_false_and_silently_ignored()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);

        // Unknown ids: resize and stop succeed, foreground is false, input/ack are dropped without a reply.
        await host.ResizeAsync(UnknownId, 80, 24, ct);
        Assert.False(await host.ForegroundAsync(UnknownId, ct), "an unknown id must report busy=false.");
        await host.StopAsync(UnknownId, ct);
        await host.SendInputAsync(UnknownId, "echo ignored\r", ct);
        await host.AckAsync(UnknownId, 1024, ct);

        // An exited id behaves the same way.
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
        await host.SendInputAsync(terminalId, "exit 3\r", ct);
        Assert.Equal(3, await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct));

        await host.ResizeAsync(terminalId, 80, 24, ct);
        Assert.False(await host.ForegroundAsync(terminalId, ct), "an exited terminal must report busy=false.");
        await host.StopAsync(terminalId, ct);

        var framesAfterExit = host.FramesOf(terminalId).Count;
        await host.SendInputAsync(terminalId, "echo after-exit\r", ct);
        await host.AckAsync(terminalId, 1_000_000, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        Assert.Equal(framesAfterExit, host.FramesOf(terminalId).Count);
        Assert.Equal(3, host.ExitCodeOf(terminalId));

        Assert.Empty(host.Problems());
    }
}
