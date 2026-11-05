using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Resizing over a real host: the child process observes the new size, in-range edges succeed,
/// and out-of-range sizes are refused with <c>invalid_request</c>.</summary>
public sealed class TerminalResizeTests
{
    [Fact]
    public async Task Resize_is_seen_by_the_child_and_out_of_range_is_invalid_request()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, _, _) = await host.StartTerminalAsync(TerminalShells.Rich(), ct: ct);

        await host.ResizeAsync(terminalId, 40, 12, ct);

        if (OperatingSystem.IsWindows())
        {
            await host.WaitForTextAsync(terminalId, "PS", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(
                terminalId,
                "\"W=$($Host.UI.RawUI.WindowSize.Width);H=$($Host.UI.RawUI.WindowSize.Height)\"\r",
                ct);
            await host.WaitForTextAsync(terminalId, "W=40;H=12", TimeSpan.FromSeconds(30), ct);
        }
        else
        {
            await host.SendInputAsync(terminalId, "echo SIZE-READY\r", ct);
            await host.WaitForTextAsync(terminalId, "SIZE-READY", TimeSpan.FromSeconds(30), ct);
            await host.SendInputAsync(terminalId, "echo \"SIZE-$(stty size)\"\r", ct);
            // stty prints "rows columns".
            await host.WaitForTextAsync(terminalId, "SIZE-12 40", TimeSpan.FromSeconds(30), ct);
        }

        // Edges of the documented range (columns 1–1000, rows 1–500) succeed.
        await host.ResizeAsync(terminalId, 1, 1, ct);
        await host.ResizeAsync(terminalId, 1000, 500, ct);

        // Anything outside is invalid_request, on resize and on start alike.
        foreach (var (columns, rows) in new (int, int)[] { (0, 24), (80, 0), (1001, 24), (80, 501) })
        {
            var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                host.ResizeAsync(terminalId, columns, rows, ct));
            Assert.Equal("invalid_request", refused.Code);
        }

        var startRefused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            host.StartTerminalAsync(TerminalShells.Fast, columns: 0, ct: ct));
        Assert.Equal("invalid_request", startRefused.Code);
    }
}
