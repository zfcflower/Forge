using System.Globalization;
using System.Runtime.InteropServices;

namespace Forge.Bench;

/// <summary>One measured row: the scenario, its prepared case, and the implementation/baseline comparison.</summary>
internal sealed record ResultRow(BenchScenario Scenario, BenchCase Case, Comparison Comparison);

/// <summary>
/// The fixed-width report. Every table is printed column by column with a fixed width so a reader (and a grep) can compare
/// rows across runs even when a name or a unit changes length.
/// </summary>
internal static class Report
{
    private const int NameWidth = 30;

    public static void PrintHeader(BenchWorkspace workspace, BenchOptions options)
    {
        Console.WriteLine("=== Forge.Bench - file, search and process layer (no BenchmarkDotNet, no NuGet package) ===");
        Console.WriteLine($"runtime     : {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        Console.WriteLine(
            $"machine     : {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}, "
            + $"{Environment.ProcessorCount} logical cores, invariant globalization");
        Console.WriteLine($"scratch dir : {workspace.Root}");
        Console.WriteLine(
            "mode        : "
            + (options.Quick ? "--quick " : "full ")
            + (options.Filter is { } filter ? $"filter \"{filter}\"" : "all scenarios"));
        Console.WriteLine(
            "method      : warmup 3 then 20 timed calls per row (a row that says n= overrides it); the implementation call\n"
            + "              and its baseline call are interleaved inside that loop, and the comparison also reports the\n"
            + "              median of the per-iteration differences (paired delta), which is the number to read when the\n"
            + "              difference is small against the noise of one call. Latency comes from Stopwatch.GetTimestamp\n"
            + "              around one call, no inner loop; percentiles are nearest rank on the sorted samples\n"
            + "              (index = ceil(p*n)-1, no interpolation; with n=20 p99 is the slowest sample and can still carry\n"
            + "              JIT tiering or a GC from an earlier scenario); throughput comes from p50.\n"
            + "              Allocation is the GC.GetAllocatedBytesForCurrentThread delta per call for the synchronous rows,\n"
            + "              and the GC.GetTotalAllocatedBytes(precise: true) delta for a row marked alloc=process-wide (an\n"
            + "              awaited call resumes on an IO thread, so the per-thread counter would miss most of its\n"
            + "              allocation); it is measured in a short second pass so the counter never sits inside a timed\n"
            + "              window. GC counts are read around every timed call. Peak memory is the Environment.WorkingSet\n"
            + "              delta sampled during the timing loop (it includes JIT and runtime pages - read the order of\n"
            + "              magnitude only).");
        Console.WriteLine();
    }

    public static void PrintTableHeader()
    {
        Console.WriteLine(
            Pad("scenario") + Pad("n", 4) + Pad("p50 ms", 11) + Pad("p95 ms", 11) + Pad("p99 ms", 11)
            + Pad("throughput", 14) + Pad("alloc B/op", 13) + Pad("GC0/1/2", 10) + Pad("peak dMB", 9) + "note");
        Console.WriteLine(new string('-', 132));
    }

    public static void PrintRow(ResultRow row)
    {
        var implementation = row.Comparison.Implementation;
        var note = new List<string>(3);
        if (!string.IsNullOrEmpty(implementation.Observation))
        {
            note.Add(implementation.Observation);
        }

        if (!string.IsNullOrEmpty(row.Case.Note))
        {
            note.Add(row.Case.Note!);
        }

        if (implementation.ProcessWideAllocation)
        {
            note.Add("alloc=process-wide");
        }

        Console.WriteLine(
            Pad(row.Scenario.Name)
            + Pad(implementation.Iterations.ToString(CultureInfo.InvariantCulture), 4)
            + Pad(Harness.Milliseconds(implementation.P50Ms), 11)
            + Pad(Harness.Milliseconds(implementation.P95Ms), 11)
            + Pad(Harness.Milliseconds(implementation.P99Ms), 11)
            + Pad(implementation.ThroughputText, 14)
            + Pad(implementation.AllocatedBytesPerOp.ToString("N0", CultureInfo.InvariantCulture), 13)
            + Pad(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{implementation.Gen0}/{implementation.Gen1}/{implementation.Gen2}"),
                10)
            + Pad(implementation.WorkingSetDeltaMb.ToString("0.0", CultureInfo.InvariantCulture), 9)
            + string.Join("; ", note));
    }

    public static void PrintFailure(string name, Exception exception)
    {
        Console.WriteLine(
            Pad(name)
            + Pad("!", 4)
            + "FAILED: "
            + $"{exception.GetType().Name}: {FirstLine(exception.Message)}");
    }

    public static void PrintBaselines(IReadOnlyList<ResultRow> rows)
    {
        var compared = rows.Where(row => row.Comparison.Baseline is not null).ToList();
        Console.WriteLine();
        Console.WriteLine("=== Baseline comparison (both halves interleaved in the same loop, same run) ===");
        if (compared.Count == 0)
        {
            Console.WriteLine("(no scenario in this selection has a baseline)");
            return;
        }

        Console.WriteLine(
            Pad("scenario") + Pad("impl p50 ms", 13) + Pad("base p50 ms", 13) + Pad("impl/base", 11)
            + Pad("paired delta", 22) + "baseline");
        Console.WriteLine(new string('-', 122));
        foreach (var row in compared)
        {
            var comparison = row.Comparison;
            Console.WriteLine(
                Pad(row.Scenario.Name)
                + Pad(Harness.Milliseconds(comparison.Implementation.P50Ms), 13)
                + Pad(Harness.Milliseconds(comparison.Baseline!.P50Ms), 13)
                + Pad(
                    comparison.Ratio is { } ratio ? ratio.ToString("0.00x", CultureInfo.InvariantCulture) : "n/a",
                    11)
                + Pad(PairedText(comparison), 22)
                + (row.Case.BaselineName ?? "(unnamed)"));
        }
    }

    /// <summary>Prints every row's acceptance check and returns them, one per checked row.</summary>
    public static IReadOnlyList<(string Scenario, Verdict Verdict)> PrintVerdicts(IReadOnlyList<ResultRow> rows)
    {
        var verdicts = new List<(string Scenario, Verdict Verdict)>();
        foreach (var row in rows)
        {
            if (row.Case.Check?.Invoke(row.Comparison) is not { } verdict)
            {
                continue;
            }

            verdicts.Add((row.Scenario.Name, verdict));
        }

        Console.WriteLine();
        Console.WriteLine("=== Acceptance ===");
        if (verdicts.Count == 0)
        {
            Console.WriteLine("(no acceptance check in this selection)");
            return verdicts;
        }

        foreach (var (scenario, verdict) in verdicts)
        {
            Console.WriteLine($"[{(verdict.Passed ? "PASS" : "MISS")}] {verdict.Target}");
            Console.WriteLine($"       {scenario}: {verdict.Measured}");
        }

        var passed = verdicts.Count(entry => entry.Verdict.Passed);
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{passed} of {verdicts.Count} acceptance checks passed"));
        return verdicts;
    }

    public static void PrintFooter(
        IReadOnlyList<string> failures,
        IReadOnlyList<string> skipped,
        IReadOnlyList<(string Scenario, Verdict Verdict)> verdicts)
    {
        Console.WriteLine();
        Console.WriteLine("=== Result ===");
        if (skipped.Count > 0)
        {
            Console.WriteLine($"skipped: {string.Join(", ", skipped)}");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine($"scenario failures ({failures.Count}):");
            foreach (var failure in failures)
            {
                Console.WriteLine($"  - {failure}");
            }
        }
        else
        {
            Console.WriteLine("scenario failures: none");
        }

        var missed = verdicts.Where(entry => !entry.Verdict.Passed).ToList();
        if (missed.Count > 0)
        {
            Console.WriteLine($"missed targets ({missed.Count}):");
            foreach (var (scenario, verdict) in missed)
            {
                Console.WriteLine($"  - {scenario}: {verdict.Target} ({verdict.Measured})");
            }
        }
    }

    /// <summary>The paired delta in the comparison table: absolute milliseconds and the share of the baseline median.</summary>
    public static string PairedText(Comparison comparison) =>
        comparison.PairedDeltaP50Ms is not { } delta
            ? "n/a"
            : $"{delta.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)} ms "
                + $"({(comparison.PairedDeltaShare is { } share ? share.ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture) : "n/a")})";

    private static string Pad(string value, int width = NameWidth) =>
        value.Length >= width ? value + " " : value.PadRight(width);

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }
}
