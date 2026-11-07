using System.Buffers;
using System.Globalization;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The <c>read</c> command: <see cref="TextFileReader"/> against the direct BCL call. Small files are read whole
/// (<see cref="TextFileReader.ReadAllTextAsync"/> vs <see cref="File.ReadAllTextAsync(string, CancellationToken)"/>) and
/// large ones as a hundred-line window (<see cref="TextFileReader.ReadWindowAsync"/> vs opening a
/// <see cref="FileStream"/> and taking the first hundred lines of a bounded head), which is the shape the <c>read</c> tool
/// actually uses.
/// </summary>
internal static class ReadScenarios
{
    /// <summary>The window a <c>read</c> call asks for by default: 100 lines out of a 256 KiB byte budget.</summary>
    private const int WindowLines = 100;
    private const int WindowBytes = 256 * 1024;

    /// <summary>How much a hand written bounded head read costs the baseline: one 64 KiB read, the reader probes with 8 KiB.</summary>
    private const int BaselineHeadBytes = 64 * 1024;

    private const int LargeFileBytes = 100 << 20;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Whole(fixtures, "1KiB", 1 << 10);
        yield return Whole(fixtures, "64KiB", 64 << 10);
        yield return Whole(fixtures, "1MiB", 1 << 20);
        yield return Whole(fixtures, "100MiB", LargeFileBytes);
        yield return Window(fixtures, "1MiB", 1 << 20);
        yield return Window(fixtures, "100MiB", LargeFileBytes);
    }

    private static BenchScenario Whole(Fixtures fixtures, string label, int byteCount)
    {
        var large = byteCount >= 1 << 20;
        return new BenchScenario(
            $"read.whole.{label}",
            "read",
            Heavy: byteCount >= LargeFileBytes,
            PrepareAsync: _ =>
            {
                // The 100 MiB payload is a hole behind a 16 KiB text head: the read is real, the 100 MiB write is not.
                var path = byteCount >= LargeFileBytes
                    ? fixtures.SparseTextFile($"read/whole-{label}.txt", byteCount)
                    : fixtures.TextFile($"read/whole-{label}.txt", byteCount);
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var text = await TextFileReader.ReadAllTextAsync(path, byteCount, cancellationToken).ConfigureAwait(false);
                        return new OpResult(byteCount, Items: 1, text.Length, $"chars={text.Length}");
                    },
                    Baseline = async cancellationToken =>
                    {
                        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                        return new OpResult(byteCount, Items: 1, text.Length, $"chars={text.Length}");
                    },
                    BaselineName = "File.ReadAllTextAsync",
                    ProcessWideAllocation = true,
                    Warmup = byteCount >= LargeFileBytes ? 1 : Harness.DefaultWarmup,
                    Iterations = byteCount >= LargeFileBytes ? 5 : Harness.DefaultIterations,
                    Note = byteCount >= LargeFileBytes
                        ? "n=5: one 100 MiB byte buffer plus a 100 M-char string per call"
                        : null,
                    Check = large ? ThroughputCheck("whole-file read throughput >= 95% of the direct BCL read") : null,
                });
            });
    }

    private static BenchScenario Window(Fixtures fixtures, string label, int byteCount)
    {
        return new BenchScenario(
            $"read.window100lines.{label}",
            "read",
            Heavy: byteCount >= LargeFileBytes,
            PrepareAsync: _ =>
            {
                var path = byteCount >= LargeFileBytes
                    ? fixtures.SparseTextFile($"read/window-{label}.txt", byteCount)
                    : fixtures.TextFile($"read/window-{label}.txt", byteCount);
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var window = await TextFileReader
                            .ReadWindowAsync(path, 1, WindowLines, WindowBytes, cancellationToken)
                            .ConfigureAwait(false);
                        // Items, not bytes: a window call is one tool call, and its cost is the open plus one probe.
                        return new OpResult(
                            Items: 1,
                            Signature: window.LineCount,
                            Observation: $"lines={window.LineCount} truncated={window.Truncated}");
                    },
                    Baseline = async cancellationToken =>
                    {
                        var lines = await ReadHeadLinesAsync(path, WindowLines, cancellationToken).ConfigureAwait(false);
                        return new OpResult(Items: 1, Signature: lines, Observation: $"lines={lines}");
                    },
                    BaselineName = "FileStream + 64 KiB head + 100 lines",
                    ProcessWideAllocation = true,
                    Note = $"the reader probes with 8 KiB, the baseline reads a {BaselineHeadBytes / 1024} KiB head",
                });
            });
    }

    /// <summary>Throughput comparison against the baseline, plus the assertion that both read the same number of characters.</summary>
    private static Func<Comparison, Verdict?> ThroughputCheck(string requirement) =>
        comparison =>
        {
            if (comparison.Baseline is not { P50Ms: > 0 } baseline)
            {
                return null;
            }

            var implementation = comparison.Implementation;
            var throughputRatio = baseline.P50Ms / implementation.P50Ms;
            var sameAnswer = implementation.Signature == baseline.Signature && implementation.Signature > 0;
            return new Verdict(
                requirement,
                $"{implementation.ThroughputText} vs {baseline.ThroughputText} = "
                + $"{throughputRatio.ToString("P1", CultureInfo.InvariantCulture)} of baseline; "
                + $"chars {implementation.Signature} vs {baseline.Signature}",
                throughputRatio >= 0.95 && sameAnswer);
        };

    /// <summary>The baseline for a windowed read: open the file, read one bounded head, count lines in it.</summary>
    private static async Task<long> ReadHeadLinesAsync(string path, int lineCount, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BaselineHeadBytes);
        try
        {
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 0,
                });
            var read = await stream.ReadAsync(buffer.AsMemory(0, BaselineHeadBytes), cancellationToken).ConfigureAwait(false);
            var head = buffer.AsSpan(0, read);
            var lines = 0L;
            var offset = 0;
            while (offset < head.Length && lines < lineCount)
            {
                var newline = head[offset..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    break;
                }

                offset += newline + 1;
                lines++;
            }

            return lines;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
