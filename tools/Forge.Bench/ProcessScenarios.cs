using System.Globalization;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The <c>bash</c> path: <see cref="ProcessRunner"/> against the shortest direct use of <see cref="System.Diagnostics.Process"/>.
/// A short command (<c>cmd /c echo hi</c>, <c>/bin/echo hi</c>) is dominated by the platform's process creation cost, so its
/// row reports the absolute milliseconds and only asks that the runner's share stay inside the noise band; the long command
/// (<c>dotnet --info</c>) is dominated by real work, so the runner's share can be held to a percentage.
/// </summary>
internal static class ProcessScenarios
{
    /// <summary>The absolute band a short command's extra milliseconds must stay in: process start noise is a millisecond or two.</summary>
    private const double ShortCommandNoiseBandMs = 2.0;

    /// <summary>How much <see cref="ProcessRunner"/> may add to a long command.</summary>
    private const double LongCommandOverheadLimit = 0.05;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Short(fixtures);
        yield return Long(fixtures);
    }

    private static BenchScenario Short(Fixtures fixtures)
    {
        var (fileName, arguments) = EchoCommand();
        var command = $"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}";
        return new BenchScenario(
            "process.short",
            "process",
            Heavy: false,
            PrepareAsync: _ => Task.FromResult(new BenchCase
            {
                Run = ct => MeasureRun(fixtures, fileName, arguments, ct),
                Baseline = ct => DirectProcess.RunAsync(fileName, arguments, fixtures.Workspace.Root, ct),
                BaselineName = "Process.Start + BaseStream read",
                Warmup = Harness.DefaultWarmup,
                Iterations = Harness.DefaultIterations,
                ProcessWideAllocation = true,
                Note = $"startup dominates - read the absolute p50, not the ratio (`{command}`)",
                Check = comparison =>
                {
                    if (comparison.Baseline is not { } baseline || comparison.PairedDeltaP50Ms is not { } delta)
                    {
                        return null;
                    }

                    return new Verdict(
                        "short command: the ProcessRunner offset stays inside the noise band "
                        + $"(|paired delta| <= {ShortCommandNoiseBandMs.ToString("0.0", CultureInfo.InvariantCulture)} ms)",
                        $"paired delta {Signed(delta)} ms ({SignedPercent(delta, baseline.P50Ms)}); "
                        + $"p50 {Harness.Milliseconds(comparison.Implementation.P50Ms)} ms vs {Harness.Milliseconds(baseline.P50Ms)} ms; "
                        + $"{comparison.Implementation.Observation} vs {baseline.Observation}",
                        Math.Abs(delta) <= ShortCommandNoiseBandMs && ExitCodeOf(comparison.Implementation) == 0);
                },
            }));
    }

    private static BenchScenario Long(Fixtures fixtures)
    {
        string[] arguments = ["--info"];
        return new BenchScenario(
            "process.long",
            "process",
            Heavy: true,
            PrepareAsync: _ => Task.FromResult(new BenchCase
            {
                Run = ct => MeasureRun(fixtures, "dotnet", arguments, ct),
                Baseline = ct => DirectProcess.RunAsync("dotnet", arguments, fixtures.Workspace.Root, ct),
                BaselineName = "Process.Start + BaseStream read",
                Warmup = 1,
                Iterations = Harness.DefaultIterations,
                ProcessWideAllocation = true,
                Check = comparison =>
                {
                    if (comparison.Baseline is not { } baseline || comparison.PairedDeltaP50Ms is not { } delta)
                    {
                        return null;
                    }

                    var share = baseline.P50Ms <= 0 ? 0 : delta / baseline.P50Ms;
                    return new Verdict(
                        "long command: ProcessRunner overhead < 5% of the direct Process cost (paired median delta)",
                        $"paired delta {Signed(delta)} ms ({SignedPercent(delta, baseline.P50Ms)}); "
                        + $"p50 {Harness.Milliseconds(comparison.Implementation.P50Ms)} ms vs {Harness.Milliseconds(baseline.P50Ms)} ms; "
                        + $"{comparison.Implementation.Observation} vs {baseline.Observation}",
                        share < LongCommandOverheadLimit && ExitCodeOf(comparison.Implementation) == 0);
                },
            }));
    }

    private static async Task<OpResult> MeasureRun(
        Fixtures fixtures,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await fixtures.Runner.ExecuteAsync(
            new ProcessRequest
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = fixtures.Workspace.Root,
                Timeout = TimeSpan.FromSeconds(120),
            },
            cancellationToken).ConfigureAwait(false);

        return new OpResult(
            Signature: result.StdoutBytes,
            Observation: $"exit={result.ExitCode} stdout={result.StdoutBytes}B stderr={result.StderrBytes}B end={result.EndReason}");
    }

    private static string Signed(double milliseconds) =>
        milliseconds.ToString("+0.000;-0.000", CultureInfo.InvariantCulture);

    private static string SignedPercent(double milliseconds, double baselineMilliseconds) =>
        baselineMilliseconds <= 0 ? "n/a" : (milliseconds / baselineMilliseconds).ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture);

    /// <summary>Reads the exit code the observation starts with; <c>-1</c> when the row has no exit code.</summary>
    private static int ExitCodeOf(Measurement measurement)
    {
        const string Prefix = "exit=";
        if (measurement.Observation is not { } text || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return -1;
        }

        var end = text.IndexOf(' ', Prefix.Length);
        var span = end < 0 ? text.AsSpan(Prefix.Length) : text.AsSpan(Prefix.Length, end - Prefix.Length);
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode) ? exitCode : -1;
    }

    /// <summary>The short command of the platform: a shell echo, which is what a trivial <c>bash</c> call costs.</summary>
    private static (string FileName, string[] Arguments) EchoCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(commandProcessor) || !File.Exists(commandProcessor))
            {
                commandProcessor = "cmd.exe";
            }

            return (commandProcessor, ["/d", "/c", "echo hi"]);
        }

        return ("/bin/echo", ["hi"]);
    }
}
