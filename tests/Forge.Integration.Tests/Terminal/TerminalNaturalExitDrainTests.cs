using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Integration.Tests.Terminal;

/// <summary>A shell that exits on its own still has its last output delivered, under the same flow control, before
/// <c>terminal.exit</c>; <c>terminal.exit</c> stays the id's final message. An explicit stop during that drain turns it
/// into a drop and is answered promptly.</summary>
public sealed class TerminalNaturalExitDrainTests
{
    private const int WindowBytes = 1_048_576;
    private const int PastWindowBytes = 8_000;

    private readonly ITestOutputHelper output;

    public TerminalNaturalExitDrainTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Last_output_arrives_before_exit_and_exit_is_the_final_message()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var (terminalId, processId, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);

        // The marker is built from a variable so the echoed command line cannot satisfy the check by itself.
        await host.SendInputAsync(terminalId, SetVariable("ARK"), ct);
        await host.SendInputAsync(terminalId, EchoThenExit("TAIL-M", 3), ct);
        var exitCode = await host.WaitForTerminalExitAsync(terminalId, TimeSpan.FromSeconds(30), ct);
        Assert.Equal(3, exitCode);

        Assert.Contains("TAIL-MARK", host.TextOf(terminalId), StringComparison.Ordinal);

        var events = host.EventsOf(terminalId);
        Assert.StartsWith("exit", events[^1], StringComparison.Ordinal);
        Assert.Single(events, line => line.StartsWith("exit", StringComparison.Ordinal));

        // Every output frame, including the one carrying the tail, was received before the exit notification.
        var exitTicks = host.ExitTicks(terminalId);
        Assert.NotNull(exitTicks);
        var frames = host.FramesOf(terminalId);
        Assert.All(frames, frame => Assert.True(frame.Ticks <= exitTicks!.Value, "an output frame arrived after terminal.exit."));
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.Equal(index, frames[index].Seq);
        }

        await TerminalWait.WaitForDeathAsync(processId, "the exited shell", ct);
        Assert.Empty(host.Problems());
    }

    [Fact]
    public async Task Exit_waits_for_acknowledgement_then_tail_then_exit_in_order_without_loss()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var setup = await FillWindowThenExitShellAsync(host, exitCode: 4, ct);

        // Window full and the shell gone: nothing more is delivered and terminal.exit is held back.
        Assert.Null(host.ExitCodeOf(setup.TerminalId));
        Assert.DoesNotContain(host.EventsOf(setup.TerminalId), line => line.StartsWith("exit", StringComparison.Ordinal));
        Assert.DoesNotContain("TAIL-MARK", host.TextOf(setup.TerminalId), StringComparison.Ordinal);

        // Acknowledging releases the tail; the tail comes first, the exit last.
        await host.AckAsync(setup.TerminalId, host.ByteCountOf(setup.TerminalId), ct);
        host.AutoAck = true;
        var exitCode = await host.WaitForTerminalExitAsync(setup.TerminalId, TimeSpan.FromSeconds(60), ct);
        Assert.Equal(4, exitCode);

        var events = host.EventsOf(setup.TerminalId);
        Assert.StartsWith("exit", events[^1], StringComparison.Ordinal);
        Assert.Single(events, line => line.StartsWith("exit", StringComparison.Ordinal));
        var exitTicks = host.ExitTicks(setup.TerminalId)!.Value;
        var frames = host.FramesOf(setup.TerminalId);
        Assert.All(frames, frame => Assert.True(frame.Ticks <= exitTicks, "an output frame arrived after terminal.exit."));

        var text = host.TextOf(setup.TerminalId);
        TerminalAsserts.SequenceIsContinuous(frames, setup.TerminalId);
        TerminalAsserts.FloodIsLosslessInOrder(text, setup.Lines, "TAIL-MARK");
        Assert.True(
            text.LastIndexOf("TAIL-MARK", StringComparison.Ordinal) > text.LastIndexOf($"LINE-{setup.Lines:0000000}", StringComparison.Ordinal),
            "the tail marker must follow the last flood line.");
        Assert.Empty(host.Problems());
    }

    [Fact]
    public async Task Stop_during_the_natural_exit_drain_is_answered_promptly_and_after_exit()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = budget.Token;
        await using var host = await RawTerminalHost.StartAsync(ct);
        var setup = await FillWindowThenExitShellAsync(host, exitCode: 5, ct);
        Assert.Null(host.ExitCodeOf(setup.TerminalId));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var before = DateTime.UtcNow.Ticks;
        await host.StopAsync(setup.TerminalId, ct);
        var answered = DateTime.UtcNow.Ticks;
        stopwatch.Stop();

        // The drain is bounded at 30 s for an app that never acknowledges; an explicit stop must not wait for it.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"terminal.stop took {stopwatch.Elapsed.TotalSeconds:0.0} s; it must cut the natural-exit drain short.");

        var exited = host.ExitTicks(setup.TerminalId);
        Assert.NotNull(exited);
        Assert.True(before <= exited!.Value, "terminal.exit arrived before terminal.stop was even sent.");
        Assert.True(exited.Value <= answered, "terminal.exit must arrive before the terminal.stop answer.");
        Assert.StartsWith("exit", host.EventsOf(setup.TerminalId)[^1], StringComparison.Ordinal);
    }

    private sealed record DrainSetup(string TerminalId, int ProcessId, int Lines);

    /// <summary>Starts a shell, fills the 1 MiB window with a numbered flood without acknowledging, and has the shell print
    /// <c>TAIL-MARK</c> and exit while the window is still full. Returns once delivery is frozen and the shell process is gone.</summary>
    private async Task<DrainSetup> FillWindowThenExitShellAsync(RawTerminalHost host, int exitCode, CancellationToken ct)
    {
        // How many bytes one flood line costs on the wire depends on the platform (the pseudo console re-renders its
        // screen), so it is measured with a short unconstrained run before the real one is sized to overshoot the window
        // by only a few KiB: a shell can finish writing, and exit, only while what it has left fits in the OS buffers.
        var perLine = await MeasureBytesPerLineAsync(host, ct);
        var lines = (int)(((WindowBytes + PastWindowBytes) / perLine) + 1);
        var floodFile = WriteFlood(host.Root, "flood.txt", lines);

        host.AutoAck = false;
        var (terminalId, processId, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
        await host.SendInputAsync(terminalId, SetVariable("ARK"), ct);
        await host.SendInputAsync(terminalId, "echo FLOW-READY\r", ct);
        await host.WaitForTextAsync(terminalId, "FLOW-READY", TimeSpan.FromSeconds(30), ct);
        await host.AckAsync(terminalId, host.ByteCountOf(terminalId), ct);
        var floor = host.ByteCountOf(terminalId);

        var command = OperatingSystem.IsWindows()
            ? $"type \"{floodFile}\" & echo TAIL-M%X%& exit {exitCode}\r"
            : $"cat '{floodFile}'; echo TAIL-M$X; exit {exitCode}\r";
        await host.SendInputAsync(terminalId, command, ct);

        var frozenAt = await WaitForFrozenAsync(host, terminalId, floor, ct);
        output.WriteLine($"delivery froze {frozenAt - floor} bytes past the floor of a {lines}-line flood ({perLine:0.0} bytes per line).");

        // The precondition of every assertion that follows: the shell really has exited while its output is still held back.
        await TerminalWait.WaitForDeathAsync(processId, "the shell that printed the tail and exited", ct);
        return new DrainSetup(terminalId, processId, lines);
    }

    private static string WriteFlood(string directory, string name, int lines)
    {
        var newline = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        var flood = new StringBuilder();
        for (var line = 1; line <= lines; line++)
        {
            flood.Append("LINE-").Append(line.ToString("0000000", CultureInfo.InvariantCulture)).Append('-').Append(TerminalFlood.LineBody).Append(newline);
        }

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, flood.ToString());
        return path;
    }

    /// <summary>Delivered bytes per flood line, measured on a throw-away terminal that is acknowledged continuously.</summary>
    private static async Task<double> MeasureBytesPerLineAsync(RawTerminalHost host, CancellationToken ct)
    {
        const int sample = 2000;
        var file = WriteFlood(host.Root, "calibration.txt", sample);
        var (id, _, _) = await host.StartTerminalAsync(TerminalShells.Fast, ct: ct);
        await host.SendInputAsync(id, "echo CAL-READY\r", ct);
        await host.WaitForTextAsync(id, "CAL-READY", TimeSpan.FromSeconds(30), ct);
        var before = host.ByteCountOf(id);
        var command = OperatingSystem.IsWindows()
            ? $"type \"{file}\" & exit 0\r"
            : $"cat '{file}'; exit 0\r";
        await host.SendInputAsync(id, command, ct);
        await host.WaitForTerminalExitAsync(id, TimeSpan.FromSeconds(60), ct);
        return (host.ByteCountOf(id) - before) / (double)sample;
    }

    private static async Task<long> WaitForFrozenAsync(RawTerminalHost host, string terminalId, long floor, CancellationToken ct)
    {
        long previous = -1;
        var stableSince = DateTime.UtcNow;
        await TerminalWait.WaitUntilAsync(
            () =>
            {
                var now = host.ByteCountOf(terminalId);
                if (now != previous)
                {
                    previous = now;
                    stableSince = DateTime.UtcNow;
                    return false;
                }

                return now - floor >= WindowBytes - 70_000 && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(3);
            },
            "delivery to freeze at the acknowledgement window",
            TimeSpan.FromSeconds(120),
            ct);
        return previous;
    }

    private static string SetVariable(string value) =>
        OperatingSystem.IsWindows() ? $"set X={value}\r" : $"X={value}\r";

    private static string EchoThenExit(string prefix, int exitCode) =>
        OperatingSystem.IsWindows()
            ? $"echo {prefix}%X%& exit {exitCode}\r"
            : $"echo {prefix}$X; exit {exitCode}\r";
}
