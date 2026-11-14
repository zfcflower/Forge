namespace Forge.Bench;

/// <summary>
/// Benchmark driver for the file, search and process layer. It has no package and no framework: each scenario prepares its
/// fixtures, runs one warmup, then times a fixed number of calls and reports latency percentiles, throughput, allocation,
/// GC counts and peak memory. It also re-launches itself as the child process the output scenarios stream from.
/// <para>Usage: <c>dotnet run -c Release --project tools/Forge.Bench [-- [filter] [--quick]]</c>. Exit codes: 0 = every
/// scenario ran and every acceptance check passed, 1 = a scenario threw or a check missed, 2 = a bad argument, 130 =
/// cancelled.</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The output scenarios start this same binary with --emit; that child prints bytes and nothing else.
        if (ChildEmitter.IsRequested(args))
        {
            return ChildEmitter.Run(args);
        }

        if (!BenchOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(BenchOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(BenchOptions.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var cancellationToken = cancellation.Token;
        using var workspace = new BenchWorkspace();
        var fixtures = new Fixtures(workspace);
        Report.PrintHeader(workspace, options);

        var rows = new List<ResultRow>();
        var failures = new List<string>();
        var skipped = new List<string>();

        try
        {
            var selected = Select(fixtures, options, skipped);
            if (selected.Count == 0)
            {
                Console.Error.WriteLine(
                    options.Filter is { } filter
                        ? $"no scenario matches '{filter}'"
                        : "no scenario is enabled by --quick");
                return 2;
            }

            Report.PrintTableHeader();
            var currentFamily = string.Empty;
            foreach (var scenario in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(currentFamily, scenario.Command, StringComparison.Ordinal))
                {
                    if (currentFamily.Length > 0)
                    {
                        Console.WriteLine();
                    }

                    currentFamily = scenario.Command;
                }

                try
                {
                    var benchCase = await scenario.PrepareAsync(cancellationToken).ConfigureAwait(false);
                    var comparison = await Harness.RunAsync(benchCase, cancellationToken).ConfigureAwait(false);
                    var row = new ResultRow(scenario, benchCase, comparison);
                    rows.Add(row);
                    Report.PrintRow(row);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A broken scenario is reported, not swallowed: the run continues so the rest of the picture is
                    // still measured, and the exit code stays non-zero.
                    failures.Add($"{scenario.Name}: {exception.GetType().Name}: {exception.Message}");
                    Report.PrintFailure(scenario.Name, exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine();
            Console.WriteLine("cancelled");
            return 130;
        }

        Report.PrintBaselines(rows);
        var verdicts = Report.PrintVerdicts(rows);
        Report.PrintFooter(failures, skipped, verdicts);
        return failures.Count == 0 && verdicts.All(entry => entry.Verdict.Passed) ? 0 : 1;
    }

    /// <summary>
    /// Every scenario, in the order the report shows them: read, write, copy, walk, grep, process, output, concurrency,
    /// ratelimit.
    /// </summary>
    private static IEnumerable<BenchScenario> Registry(Fixtures fixtures)
    {
        foreach (var scenario in ReadScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in WriteScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in CopyScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in WalkScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in GrepScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in ProcessScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in OutputScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in ConcurrencyScenarios.Create(fixtures))
        {
            yield return scenario;
        }

        foreach (var scenario in RateLimitScenarios.Create(fixtures))
        {
            yield return scenario;
        }
    }

    /// <summary>Applies the filter and <c>--quick</c>, recording what was left out so the report can say so.</summary>
    private static List<BenchScenario> Select(Fixtures fixtures, BenchOptions options, List<string> skipped)
    {
        var selected = new List<BenchScenario>();
        foreach (var scenario in Registry(fixtures))
        {
            if (options.Filter is { } filter && !scenario.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{scenario.Name} (filter)");
                continue;
            }

            if (options.Quick && scenario.Heavy)
            {
                skipped.Add($"{scenario.Name} (--quick)");
                continue;
            }

            selected.Add(scenario);
        }

        return selected;
    }
}

/// <summary>The command line: an optional substring filter over scenario names and <c>--quick</c> for the light rows only.</summary>
internal sealed record BenchOptions(string? Filter, bool Quick, bool ShowHelp)
{
    public const string Usage =
        """
        forge-bench [filter] [--quick]

          filter   run only the scenarios whose name contains this substring (matched case-insensitively); the name
                   carries its family and size, e.g. `read`, `read.window100lines`, `100MiB`, `concurrency`, `grep`
          --quick  skip the heavy rows: the 100 MiB payloads, the 100k-file tree and `dotnet --info` over 20 runs
          -h, --help  this text

        Every row prints: n (timed calls), p50/p95/p99 latency, throughput, allocated bytes per call, GC0/1/2 counts and
        the working-set delta. A baseline table follows, then one acceptance line per checked row.

        Examples:
          dotnet run -c Release --project tools/Forge.Bench
          dotnet run -c Release --project tools/Forge.Bench -- read
          dotnet run -c Release --project tools/Forge.Bench -- --quick
        """;

    public static bool TryParse(string[] args, out BenchOptions options, out string error)
    {
        options = new BenchOptions(null, Quick: false, ShowHelp: false);
        error = string.Empty;
        string? filter = null;
        var quick = false;
        var help = false;

        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--quick":
                    quick = true;
                    break;
                case "-h" or "--help" or "-?":
                    help = true;
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        error = $"unknown switch '{argument}'";
                        return false;
                    }

                    if (filter is not null)
                    {
                        error = $"only one filter is accepted; got '{filter}' and '{argument}'";
                        return false;
                    }

                    filter = argument;
                    break;
            }
        }

        options = new BenchOptions(filter, quick, help);
        return true;
    }
}
