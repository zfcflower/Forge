using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;

namespace Forge.Bench;

/// <summary>
/// What the <c>write</c> overwrite check costs the two hot tools (see forge-io "`write` 覆盖检查"). Both halves are the
/// real <see cref="ReadTool"/>/<see cref="WriteTool"/> behind a real <see cref="ToolContext"/>, on the same file: the
/// implementation runs with an <see cref="IFileVersionLedger"/> attached, the baseline runs the very same call with
/// <c>FileVersions = null</c> - the configuration a script or a plugin bridge gets, and the only difference between the two
/// halves.
/// <para>One timed sample is a run of calls on the same side (<see cref="ReadCalls"/>, <see cref="WriteCalls"/>), not one
/// call: an atomic write costs milliseconds, so a single call cannot resolve a check that costs microseconds, and the
/// harness interleaves the two halves, which puts a fixed "first of the pair" advantage on whichever side runs first (a
/// ledger-less comparison of the write row measured 0.79x before the calls were batched). A run of calls per sample
/// divides that advantage by the run length and leaves the medians comparable.</para>
/// <para>The <c>ledger.scale</c> row is the price of "no cap, no eviction": one ledger over three hundred files read
/// through the tool, plus what the table costs for ten thousand workspace-shaped keys - the entry count and bytes per
/// entry the decision needs.</para>
/// </summary>
internal static class LedgerScenarios
{
    /// <summary>A small source file: 64 lines of 64 bytes, the size most <c>read</c> calls see.</summary>
    private const int SmallBytes = 4 << 10;

    /// <summary>A file whose read is dominated by bytes, so a fixed per-call cost shows as the share it really is.</summary>
    private const int LargeBytes = 1 << 20;

    /// <summary>Read calls one sample, so the per-call cost of one stat is not read through the noise of a single call.</summary>
    private const int ReadCalls = 20;

    private const int LargeReadCalls = 10;

    /// <summary>Overwrite calls one sample; the atomic write's millisecond noise shrinks by the same factor.</summary>
    private const int WriteCalls = 20;

    /// <summary>Files the scale row reads through the tool to fill one ledger, and the keys its table is sized for.</summary>
    private const int ScaleFiles = 300;

    private const int ScaleFileBytes = 1 << 10;

    private const int ScaleKeys = 10_000;

    /// <summary>How much slower than the same call without a ledger a read may get; the limits are explained on <see cref="WriteLimit"/>.</summary>
    private const double ReadLimit = 1.30;

    /// <summary>
    /// How much slower an overwrite may get. Both limits are loose on purpose: the noise floor measured on this machine (both
    /// halves run the same no-ledger call) is about 1% on the read rows and 3% on the write row, and a regression that read
    /// the file instead of stat'ing it would miss by far more than either limit.
    /// </summary>
    private const double WriteLimit = 1.25;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Read(fixtures, "4KiB", SmallBytes, ReadCalls, iterations: 100);
        yield return Read(fixtures, "1MiB", LargeBytes, LargeReadCalls, iterations: 40);
        yield return Write(fixtures, "4KiB", SmallBytes);
        yield return Scale(fixtures);
    }

    /// <summary><c>read</c> against <c>read</c> with a ledger: the call stats the file and records the version before it reads.</summary>
    private static BenchScenario Read(Fixtures fixtures, string label, int byteCount, int calls, int iterations)
    {
        return new BenchScenario(
            $"ledger.read.{label}",
            "ledger",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var path = fixtures.TextFile($"ledger/read-{label}.txt", byteCount);
                var read = new ReadTool();
                var arguments = Arguments(("path", path));
                var withLedger = new ToolContext("bench", fixtures.Workspace.Root)
                {
                    FileVersions = new BenchFileVersionLedger(),
                };
                var withoutLedger = new ToolContext("bench", fixtures.Workspace.Root);

                Func<ToolContext, CancellationToken, Task<OpResult>> readFile = async (context, cancellationToken) =>
                {
                    var chars = 0;
                    for (var call = 0; call < calls; call++)
                    {
                        var result = await InvokeAsync(read, "read", arguments, context, cancellationToken)
                            .ConfigureAwait(false);
                        chars = result.Text.Length;
                    }

                    // Items, not bytes: a window call is one tool call, and the same text came back from both halves.
                    return new OpResult(Items: calls, Signature: chars, Observation: $"chars={chars} x{calls}");
                };

                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken => readFile(withLedger, cancellationToken),
                    Baseline = cancellationToken => readFile(withoutLedger, cancellationToken),
                    BaselineName = "read tool, FileVersions = null",
                    ProcessWideAllocation = true,
                    Iterations = iterations,
                    Note = $"one sample = {calls} read calls; ledger half stats the file and writes one record per call",
                    Check = Limit(ReadLimit, "read with a ledger", sameAnswer: true),
                });
            });
    }

    /// <summary>
    /// <c>write</c> over a file that already exists: the ledger half adds the stat, the lookup and the record that follows the
    /// write. Each half owns its own path - the other half's write would move the version the ledger half recorded.
    /// </summary>
    private static BenchScenario Write(Fixtures fixtures, string label, int byteCount)
    {
        return new BenchScenario(
            $"ledger.write.{label}",
            "ledger",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var content = Fixtures.MakeText(byteCount);
                var directory = fixtures.Workspace.DirectoryAt("ledger");
                var toolPath = Path.Combine(directory, $"write-tool-{label}.txt");
                var baselinePath = Path.Combine(directory, $"write-base-{label}.txt");
                var toolArguments = Arguments(("path", toolPath), ("content", content));
                var baselineArguments = Arguments(("path", baselinePath), ("content", content));

                // Both files exist before the clock starts, so every sample is the overwrite the check guards.
                File.WriteAllText(toolPath, content);
                File.WriteAllText(baselinePath, content);

                var write = new WriteTool();
                var withLedger = new ToolContext("bench", fixtures.Workspace.Root)
                {
                    FileVersions = new BenchFileVersionLedger(),
                };
                var withoutLedger = new ToolContext("bench", fixtures.Workspace.Root);

                // The ledger has to hold the version this agent read, or the first overwrite is refused instead of checked.
                await InvokeAsync(new ReadTool(), "read", Arguments(("path", toolPath)), withLedger, cancellationToken)
                    .ConfigureAwait(false);

                Func<ToolContext, string, CancellationToken, Task<OpResult>> overwrite =
                    async (context, arguments, token) =>
                    {
                        var text = string.Empty;
                        for (var call = 0; call < WriteCalls; call++)
                        {
                            var result = await InvokeAsync(write, "write", arguments, context, token).ConfigureAwait(false);
                            text = result.Text;
                        }

                        return new OpResult(
                            Bytes: (long)byteCount * WriteCalls,
                            Items: WriteCalls,
                            Signature: byteCount,
                            Observation: $"{text} x{WriteCalls}");
                    };

                return new BenchCase
                {
                    Run = cancellationToken => overwrite(withLedger, toolArguments, cancellationToken),
                    Baseline = cancellationToken => overwrite(withoutLedger, baselineArguments, cancellationToken),
                    BaselineName = "write tool, FileVersions = null",
                    ProcessWideAllocation = true,
                    Iterations = 40,
                    Note = $"one sample = {WriteCalls} overwrite calls (temp file + fsync + rename); "
                        + "ledger half stats, looks the record up and records the new version",
                    Check = Limit(WriteLimit, "write over an existing file with a ledger", sameAnswer: true),
                };
            });
    }

    /// <summary>
    /// The size of the table the "no cap, no eviction" decision pays for: three hundred files read through the tool fill one
    /// ledger (the entry count proves the tool records one per file), and the same dictionary is then filled with ten
    /// thousand workspace-shaped keys to price an entry.
    /// </summary>
    private static BenchScenario Scale(Fixtures fixtures)
    {
        return new BenchScenario(
            "ledger.scale.10000",
            "ledger",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var directory = fixtures.Workspace.DirectoryAt("ledger/scale");
                var content = Fixtures.MakeText(ScaleFileBytes);
                var files = new string[ScaleFiles];
                for (var index = 0; index < files.Length; index++)
                {
                    var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"f{index:D5}.txt"));
                    File.WriteAllText(path, content);
                    files[index] = path;
                }

                // Keys of the shape a session really records: the same workspace, ten thousand files deep.
                var keys = new string[ScaleKeys];
                for (var index = 0; index < keys.Length; index++)
                {
                    keys[index] = Path.Combine(
                        fixtures.Workspace.Root,
                        "repo-10k",
                        "pkg000",
                        string.Create(CultureInfo.InvariantCulture, $"src{index:D4}.cs"));
                }

                var calls = new string[files.Length];
                for (var index = 0; index < files.Length; index++)
                {
                    calls[index] = Arguments(("path", files[index]));
                }

                var read = new ReadTool();
                var root = fixtures.Workspace.Root;
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var ledger = new BenchFileVersionLedger();
                        var context = new ToolContext("bench", root) { FileVersions = ledger };
                        foreach (var arguments in calls)
                        {
                            await InvokeAsync(read, "read", arguments, context, cancellationToken).ConfigureAwait(false);
                        }

                        var table = TableBytes(keys);
                        var keyBytes = 0L;
                        foreach (var key in keys)
                        {
                            keyBytes += StringBytes(key);
                        }

                        var perEntry = (table + keyBytes) / keys.Length;
                        return new OpResult(
                            (long)files.Length * ScaleFileBytes,
                            files.Length,
                            ledger.Count,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"entries={ledger.Count} after {files.Length} reads; {keys.Length} keys: table {table / keys.Length}B"
                                + $" + key {keyBytes / keys.Length}B ({keys[0].Length} chars) = {perEntry}B/entry,"
                                + $" {(table + keyBytes) / 1024}KiB total"));
                    },
                    ProcessWideAllocation = true,
                    Warmup = 1,
                    Iterations = 1,
                    Note = "n=1: 300 tool reads fill the ledger, then one entry is priced on 10,000 workspace keys "
                        + "(table sized up front, so the number is what stays alive, not the churn of growing it)",
                    Check = comparison => new Verdict(
                        "the ledger holds one entry per distinct file read",
                        comparison.Implementation.Observation ?? "(no observation)",
                        comparison.Implementation.Signature == ScaleFiles),
                });
            });
    }

    /// <summary>One tool call; a tool error fails the scenario instead of being timed.</summary>
    private static async Task<ToolResult> InvokeAsync(
        ITool tool,
        string name,
        string arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var result = await tool.InvokeAsync(new ToolCall("c", name, arguments), context, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError ? throw new InvalidOperationException(result.Content) : result;
    }

    /// <summary>
    /// What the ledger's table costs for <paramref name="keys"/>: the same <see cref="Dictionary{TKey,TValue}"/> and comparer
    /// <see cref="BenchFileVersionLedger"/> uses, filled on this thread so the per-thread counter is exact. The keys arrive
    /// already allocated, so nothing but the table is counted. It is sized up front: that is the footprint the table keeps,
    /// where a table that grew from empty also allocates about as much again in arrays it drops (not retained).
    /// </summary>
    private static long TableBytes(IReadOnlyList<string> keys)
    {
        var table = new Dictionary<string, FileVersion>(BenchFileVersionLedger.KeyComparer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        table.EnsureCapacity(keys.Count);
        foreach (var key in keys)
        {
            table[key] = default;
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(table);
        return bytes;
    }

    /// <summary>A string's own bytes on a 64-bit runtime: the header and length word plus two bytes per character, 8-aligned.</summary>
    private static long StringBytes(string value) => (24L + (2L * value.Length) + 7) & ~7L;

    /// <summary>
    /// The acceptance check: the ledger half stays inside <paramref name="limit"/> times the ledger-less median, and (when
    /// asked) both halves report the same signature. The paired delta is printed too, since it is the number to trust when
    /// the fixed cost is small against one call.
    /// </summary>
    private static Func<Comparison, Verdict?> Limit(double limit, string what, bool sameAnswer) => comparison =>
    {
        if (comparison.Baseline is not { P50Ms: > 0 } baseline)
        {
            return null;
        }

        var implementation = comparison.Implementation;
        var ratio = implementation.P50Ms / baseline.P50Ms;
        var parity = !sameAnswer || (implementation.Signature == baseline.Signature && implementation.Signature > 0);
        return new Verdict(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{what} stays within {limit:0.00}x of the same call without a ledger, same result"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Harness.Milliseconds(implementation.P50Ms)} ms vs {Harness.Milliseconds(baseline.P50Ms)} ms = {ratio:0.00}x, "
                + $"paired delta {Report.PairedText(comparison)}, signature {implementation.Signature} vs {baseline.Signature}"),
            ratio <= limit && parity);
    };

    /// <summary>Tool arguments as JSON, written by hand: the bench is AOT-analysed, so no reflection serializer.</summary>
    private static string Arguments(params (string Name, string Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// The ledger the tools see: a copy of Forge.Core's <c>FileVersionLedger</c> (a <see cref="Dictionary{TKey,TValue}"/>
/// behind a <see cref="Lock"/>, the platform's case rule as comparer), which is <c>internal</c> to Forge.Core and not
/// visible from here. <see cref="Count"/> exists only so a scenario can report the table it grew.
/// </summary>
internal sealed class BenchFileVersionLedger : IFileVersionLedger
{
    private readonly Dictionary<string, FileVersion> versions = new(KeyComparer);

    private readonly Lock gate = new();

    /// <summary>How two spellings of the same path compare, exactly as Forge.Core's table and <c>PathGate</c> do.</summary>
    public static StringComparer KeyComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return versions.Count;
            }
        }
    }

    public bool TryGet(string path, out FileVersion version)
    {
        lock (gate)
        {
            return versions.TryGetValue(path, out version);
        }
    }

    public void Record(string path, FileVersion version)
    {
        lock (gate)
        {
            versions[path] = version;
        }
    }
}
