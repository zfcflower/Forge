using System.Globalization;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The bounded-capture path: a child that writes 100 KiB, 10 MiB and 100 MiB to standard output, read by
/// <see cref="ProcessRunner"/> with the product's 512 KiB capture cap. The point of the row is not the ratio but the
/// memory: the runner keeps the first 512 KiB, counts the rest and drops it while still reading (a child that fills its
/// pipe must not deadlock), so the parent's working set and allocation must not grow with the child's output.
/// </summary>
internal static class OutputScenarios
{
    /// <summary>The <c>bash</c> default: 512 KiB captured per stream, everything else counted and dropped.</summary>
    private const int CaptureBytes = 512 * 1024;

    /// <summary>How much the working set may grow while 100 MiB streams through the runner.</summary>
    private const double MemoryLimitMb = 20.0;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Output(fixtures, "100KiB", 100 << 10, warmup: 3, iterations: 20);
        yield return Output(fixtures, "10MiB", 10 << 20, warmup: 2, iterations: 10);
        yield return Output(fixtures, "100MiB", 100 << 20, warmup: 1, iterations: 5);
    }

    private static string ChildText(string fileName, string[] lead) =>
        lead.Length == 0
            ? $"{Path.GetFileName(fileName)} --emit <n>"
            : $"{Path.GetFileName(fileName)} {Path.GetFileName(lead[0])} --emit <n>";

    private static BenchScenario Output(Fixtures fixtures, string label, long bytes, int warmup, int iterations)
    {
        return new BenchScenario(
            $"output.{label}",
            "output",
            Heavy: bytes > 10 << 20,
            PrepareAsync: _ =>
            {
                var (fileName, lead) = ChildEmitter.SelfCommand();
                string[] arguments = [.. lead, ChildEmitter.Switch, bytes.ToString(CultureInfo.InvariantCulture)];
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var result = await fixtures.Runner.ExecuteAsync(
                            new ProcessRequest
                            {
                                FileName = fileName,
                                Arguments = arguments,
                                WorkingDirectory = fixtures.Workspace.Root,
                                Timeout = TimeSpan.FromSeconds(120),
                                MaxStdoutBytes = CaptureBytes,
                                MaxStderrBytes = CaptureBytes,
                            },
                            cancellationToken).ConfigureAwait(false);
                        return new OpResult(
                            Bytes: result.StdoutBytes,
                            Signature: result.StdoutBytes,
                            Observation:
                                $"exit={result.ExitCode} stdout={result.StdoutBytes}B captured={result.Stdout.Length}B truncated={result.StdoutTruncated}");
                    },
                    Baseline = async cancellationToken =>
                    {
                        var drained = await DirectProcess
                            .RunAsync(fileName, arguments, fixtures.Workspace.Root, cancellationToken)
                            .ConfigureAwait(false);
                        return drained with { Bytes = drained.Signature };
                    },
                    BaselineName = "Process.Start + unbounded drain",
                    Warmup = warmup,
                    Iterations = iterations,
                    ProcessWideAllocation = true,
                    WatchMemory = true,
                    Note = $"child: {ChildText(fileName, lead)}; capture cap {CaptureBytes / 1024} KiB per stream; n={iterations}"
                        + (bytes <= 100 << 10 ? "; the runtime start of the child dominates this row" : string.Empty),
                    Check = comparison => new Verdict(
                        $"peak working set grows by less than {MemoryLimitMb.ToString("0", CultureInfo.InvariantCulture)} MiB "
                        + "for 100 MiB of child output (memory independent of output size)",
                        $"delta {comparison.Implementation.WorkingSetDeltaMb.ToString("0.0", CultureInfo.InvariantCulture)} MiB "
                        + $"(process working set at the peak {comparison.Implementation.PeakWorkingSetMb.ToString("0.0", CultureInfo.InvariantCulture)} MiB, "
                        + "shared with the big-file read row earlier in the run; "
                        + $"samples on 5 ms timer); stdout counted {comparison.Implementation.Signature}B of {bytes}B"
                        + (comparison.Baseline is null
                            ? string.Empty
                            : $"; direct drain delta {comparison.Baseline.WorkingSetDeltaMb.ToString("0.0", CultureInfo.InvariantCulture)} MiB"),
                        comparison.Implementation.WorkingSetDeltaMb < MemoryLimitMb
                        && comparison.Implementation.Signature == bytes),
                });
            });
    }
}
