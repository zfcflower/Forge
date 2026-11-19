using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;

namespace Forge.Bench;

/// <summary>
/// The <c>copy</c> and <c>delete</c> tools on a 600-file tree (30 directories of 20 files, 4 KiB each), measured through the
/// tool's own entry point (JSON in, path gate, result text out). Two baselines per tool: the shortest hand-written managed
/// loop (<c>File.Copy</c> per file, <c>Directory.Delete(recursive)</c>), which the tool must not lose to by more than a few
/// per cent, and the platform command an agent would have run through <c>bash</c> before these tools existed
/// (<c>cp -r</c> / <c>xcopy</c>, <c>rm -rf</c> / <c>rmdir /s /q</c>), which the tool must beat. Copy destinations are fresh
/// per call and delete victims are built before the clock starts, so neither side pays for the other's cleanup.
/// </summary>
internal static class TreeScenarios
{
    private const int Directories = 30;
    private const int FilesPerDirectory = 20;
    private const int FileBytes = 4096;
    private const int Warmup = 2;
    private const int Iterations = 15;

    /// <summary>How much slower than the hand-written managed loop the tool may be (the tool adds argument parsing and a path gate).</summary>
    private const double ManagedLimit = 1.15;

    private const int FileCount = Directories * FilesPerDirectory;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Copy(fixtures, "copy.tree", process: false);
        yield return Copy(fixtures, "copy.tree.process", process: true);
        yield return Delete(fixtures, "delete.tree", process: false);
        yield return Delete(fixtures, "delete.tree.process", process: true);
    }

    private static BenchScenario Copy(Fixtures fixtures, string name, bool process)
    {
        return new BenchScenario(name, "copy", Heavy: false, PrepareAsync: _ =>
        {
            var source = BuildTree(fixtures.Workspace.DirectoryAt($"tree/{name}/source"));
            var destinations = fixtures.Workspace.DirectoryAt($"tree/{name}/dst");
            var tool = new CopyTool();
            var context = new ToolContext("bench", fixtures.Workspace.Root);
            var toolCount = 0;
            var baselineCount = 0;

            return Task.FromResult(new BenchCase
            {
                Run = async ct =>
                {
                    var destination = Path.Combine(destinations, $"tool-{Interlocked.Increment(ref toolCount)}");
                    var call = new ToolCall("c", "copy", Arguments(("from", source), ("to", destination)));
                    var result = await tool.InvokeAsync(call, context, ct).ConfigureAwait(false);
                    return result.IsError
                        ? throw new InvalidOperationException(result.Content)
                        : new OpResult(FileCount * FileBytes, FileCount, FileCount);
                },
                Baseline = process
                    ? ct =>
                    {
                        var destination = Path.Combine(destinations, $"base-{Interlocked.Increment(ref baselineCount)}");
                        var (file, arguments) = OperatingSystem.IsWindows()
                            ? ("cmd.exe", new[] { "/c", "xcopy", source, destination, "/E", "/I", "/Q", "/Y" })
                            : ("cp", new[] { "-r", source, destination });
                        return DirectProcess.RunAsync(file, arguments, fixtures.Workspace.Root, ct);
                    }
                : ct =>
                    {
                        var destination = Path.Combine(destinations, $"base-{Interlocked.Increment(ref baselineCount)}");
                        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(file, target);
                        }

                        return Task.FromResult(new OpResult(FileCount * FileBytes, FileCount, FileCount));
                    },
                BaselineName = process ? "platform copy command (cp -r / xcopy) as a process" : "hand-written EnumerateFiles + File.Copy loop",
                Warmup = Warmup,
                Iterations = Iterations,
                ProcessWideAllocation = true,
                Note = $"{FileCount.ToString(CultureInfo.InvariantCulture)} files x {FileBytes.ToString(CultureInfo.InvariantCulture)} bytes, fresh destination per call",
                Check = Verdict(process),
            });
        });
    }

    private static BenchScenario Delete(Fixtures fixtures, string name, bool process)
    {
        return new BenchScenario(name, "copy", Heavy: false, PrepareAsync: _ =>
        {
            var tool = new DeleteTool();
            var context = new ToolContext("bench", fixtures.Workspace.Root);
            var victims = fixtures.Workspace.DirectoryAt($"tree/{name}/victims");
            var toolVictims = new Queue<string>(Pool(victims, "tool"));
            var baselineVictims = new Queue<string>(Pool(victims, "base"));

            return Task.FromResult(new BenchCase
            {
                Run = async ct =>
                {
                    var victim = toolVictims.Dequeue();
                    var call = new ToolCall("c", "delete", Arguments(("path", victim), ("recursive", true)));
                    var result = await tool.InvokeAsync(call, context, ct).ConfigureAwait(false);
                    return result.IsError
                        ? throw new InvalidOperationException(result.Content)
                        : new OpResult(FileCount * FileBytes, FileCount, FileCount);
                },
                Baseline = process
                    ? ct =>
                    {
                        var victim = baselineVictims.Dequeue();
                        var (file, arguments) = OperatingSystem.IsWindows()
                            ? ("cmd.exe", new[] { "/c", "rmdir", "/s", "/q", victim })
                            : ("rm", new[] { "-rf", victim });
                        return DirectProcess.RunAsync(file, arguments, fixtures.Workspace.Root, ct);
                    }
                : ct =>
                    {
                        Directory.Delete(baselineVictims.Dequeue(), recursive: true);
                        return Task.FromResult(new OpResult(FileCount * FileBytes, FileCount, FileCount));
                    },
                BaselineName = process ? "platform delete command (rm -rf / rmdir /s /q) as a process" : "Directory.Delete(recursive)",
                Warmup = Warmup,
                Iterations = Iterations,
                ProcessWideAllocation = true,
                Note = $"{FileCount.ToString(CultureInfo.InvariantCulture)} files, every victim tree built before the clock starts",
                Check = Verdict(process),
            });
        });
    }

    private static IEnumerable<string> Pool(string directory, string label)
    {
        for (var index = 0; index < (Warmup + Iterations) * 2 + 4; index++)
        {
            yield return BuildTree(Path.Combine(directory, $"{label}-{index}"));
        }
    }

    private static Func<Comparison, Verdict?> Verdict(bool process) => comparison =>
    {
        if (comparison.Baseline is not { } baseline)
        {
            return null;
        }

        var ratio = comparison.Implementation.P50Ms / Math.Max(baseline.P50Ms, 0.001);
        var measured = $"p50 {Harness.Milliseconds(comparison.Implementation.P50Ms)} ms vs {Harness.Milliseconds(baseline.P50Ms)} ms (ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)})";
        return process
            ? new Verdict("tool is faster than the process an agent ran through bash", measured, ratio < 1)
            : new Verdict(
                $"tool is within {ManagedLimit.ToString("0.00", CultureInfo.InvariantCulture)}x of the hand-written managed loop",
                measured,
                ratio <= ManagedLimit);
    };

    /// <summary>Tool arguments as JSON, written by hand: the bench is AOT-analysed, so no reflection serializer.</summary>
    private static string Arguments(params (string Name, object Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                if (value is bool flag)
                {
                    writer.WriteBoolean(name, flag);
                }
                else
                {
                    writer.WriteString(name, (string)value);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildTree(string root)
    {
        var payload = new byte[FileBytes];
        Array.Fill(payload, (byte)'x');
        for (var directory = 0; directory < Directories; directory++)
        {
            var path = Path.Combine(root, $"d{directory:D3}");
            Directory.CreateDirectory(path);
            for (var file = 0; file < FilesPerDirectory; file++)
            {
                File.WriteAllBytes(Path.Combine(path, $"f{file:D3}.txt"), payload);
            }
        }

        return root;
    }
}
