using System.Globalization;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The parallel cases: four threads reading four different 1 MiB files, four threads writing four different files, and one
/// to eight <c>echo</c> processes at once through the shared <see cref="ProcessRunner"/> (whose gate is the product
/// default of eight). A call that throws is counted, not rethrown: the scenario's answer is "how long did the batch take
/// and did anything fail", and the row's check fails when a failure happened.
/// </summary>
internal static class ConcurrencyScenarios
{
    private const int BatchSize = 4;
    private const int PayloadBytes = 1 << 20;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Reads(fixtures);
        yield return Writes(fixtures);
        foreach (var workers in (int[])[1, 2, 4, 8])
        {
            yield return Processes(fixtures, workers);
        }
    }

    private static BenchScenario Reads(Fixtures fixtures)
    {
        return new BenchScenario(
            "concurrency.read.4x1MiB",
            "concurrency",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var paths = new string[BatchSize];
                for (var index = 0; index < paths.Length; index++)
                {
                    paths[index] = fixtures.TextFile($"concurrency/read-{index}.txt", PayloadBytes);
                }

                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var failures = 0;
                        string? firstFailure = null;
                        var tasks = new Task[paths.Length];
                        for (var index = 0; index < paths.Length; index++)
                        {
                            var path = paths[index];
                            tasks[index] = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await TextFileReader.ReadAllTextAsync(path, PayloadBytes, cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception exception)
                                    {
                                        Interlocked.Increment(ref failures);
                                        Interlocked.CompareExchange(ref firstFailure, exception.Message, null);
                                    }
                                },
                                cancellationToken);
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        return new OpResult(
                            Bytes: (long)paths.Length * PayloadBytes,
                            Items: paths.Length,
                            Signature: paths.Length - failures,
                            Observation: $"failures={failures}{(firstFailure is null ? string.Empty : $" first={firstFailure}")}");
                    },
                    ProcessWideAllocation = true,
                    Note = "4 tasks, one 1 MiB file each",
                    Check = NoFailures("4 parallel reads of distinct 1 MiB files"),
                });
            });
    }

    private static BenchScenario Writes(Fixtures fixtures)
    {
        return new BenchScenario(
            "concurrency.write.4x1MiB",
            "concurrency",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var paths = new string[BatchSize];
                for (var index = 0; index < paths.Length; index++)
                {
                    paths[index] = fixtures.Workspace.FileAt($"concurrency/write-{index}.txt");
                }

                fixtures.Workspace.DirectoryAt("concurrency");
                var payload = Fixtures.MakeText(PayloadBytes);
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var failures = 0;
                        string? firstFailure = null;
                        var tasks = new Task[paths.Length];
                        for (var index = 0; index < paths.Length; index++)
                        {
                            var path = paths[index];
                            tasks[index] = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await TextFileWriter.WriteAsync(path, payload, cancellationToken).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception exception)
                                    {
                                        Interlocked.Increment(ref failures);
                                        Interlocked.CompareExchange(ref firstFailure, exception.Message, null);
                                    }
                                },
                                cancellationToken);
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        return new OpResult(
                            Bytes: (long)paths.Length * PayloadBytes,
                            Items: paths.Length,
                            Signature: paths.Length - failures,
                            Observation: $"failures={failures}{(firstFailure is null ? string.Empty : $" first={firstFailure}")}");
                    },
                    ProcessWideAllocation = true,
                    Note = "4 tasks, one atomic 1 MiB write each",
                    Check = NoFailures("4 parallel atomic writes of distinct 1 MiB files"),
                });
            });
    }

    private static BenchScenario Processes(Fixtures fixtures, int workers)
    {
        var (fileName, arguments) = EchoCommand();
        return new BenchScenario(
            $"concurrency.process.{workers}",
            "concurrency",
            Heavy: false,
            PrepareAsync: _ => Task.FromResult(new BenchCase
            {
                Run = async cancellationToken =>
                {
                    var request = new ProcessRequest
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = fixtures.Workspace.Root,
                        Timeout = TimeSpan.FromSeconds(120),
                    };

                    var failures = 0;
                    string? firstFailure = null;
                    var tasks = new Task[workers];
                    for (var index = 0; index < tasks.Length; index++)
                    {
                        tasks[index] = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    var result = await fixtures.Runner.ExecuteAsync(request, cancellationToken)
                                        .ConfigureAwait(false);
                                    if (!result.Success)
                                    {
                                        Interlocked.Increment(ref failures);
                                        Interlocked.CompareExchange(
                                            ref firstFailure,
                                            $"exit={result.ExitCode} end={result.EndReason}",
                                            null);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception exception)
                                {
                                    Interlocked.Increment(ref failures);
                                    Interlocked.CompareExchange(ref firstFailure, exception.Message, null);
                                }
                            },
                            cancellationToken);
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    return new OpResult(
                        Items: workers,
                        Signature: workers - failures,
                        Observation: $"failures={failures}{(firstFailure is null ? string.Empty : $" first={firstFailure}")}");
                },
                ProcessWideAllocation = true,
                Note = $"{workers} concurrent processes through the shared runner (gate 8)",
                Check = NoFailures($"{workers.ToString(CultureInfo.InvariantCulture)} concurrent processes complete without a failure"),
            }));
    }

    private static Func<Comparison, Verdict?> NoFailures(string requirement) =>
        comparison => new Verdict(
            requirement,
            $"{comparison.Implementation.Observation ?? "no observation"} over {comparison.Implementation.Iterations} batches",
            comparison.Implementation.Observation?.StartsWith("failures=0", StringComparison.Ordinal) == true);

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
