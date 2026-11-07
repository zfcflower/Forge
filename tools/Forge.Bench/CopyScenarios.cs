namespace Forge.Bench;

/// <summary>
/// The copy case (the tool set has no <c>copy</c> command on purpose - a copy is a <c>bash</c> command - so this only
/// reports the platform cost): <see cref="File.Copy(string, string, bool)"/> of a 1 MiB and a 100 MiB file against a
/// managed stream copy. Both write real bytes: the payload is not a hole, because the write is the thing measured.
/// </summary>
internal static class CopyScenarios
{
    /// <summary>Destinations are rotated instead of deleted before each iteration: a delete inside the timed call would be measured.</summary>
    private const int DestinationSlots = 4;

    private const int BufferBytes = 1024 * 1024;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Copy(fixtures, "1MiB", 1 << 20, Harness.DefaultIterations);
        yield return Copy(fixtures, "100MiB", 100 << 20, 5);
    }

    private static BenchScenario Copy(Fixtures fixtures, string label, long byteCount, int iterations)
    {
        var large = byteCount >= 100 << 20;
        return new BenchScenario(
            $"copy.file.{label}",
            "copy",
            Heavy: large,
            PrepareAsync: _ =>
            {
                var source = fixtures.RealFile($"copy/source-{label}.bin", byteCount);
                var directory = fixtures.Workspace.DirectoryAt("copy/dest");
                var destinations = new string[DestinationSlots];
                for (var slot = 0; slot < destinations.Length; slot++)
                {
                    destinations[slot] = Path.Combine(directory, $"copy-{label}-{slot}.bin");
                }

                // One counter, so the two operations rotate through the same destinations instead of colliding.
                var next = 0;
                string Take() => destinations[next++ % destinations.Length];

                var run = new Func<CancellationToken, Task<OpResult>>(cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Take();
                    File.Copy(source, destination, overwrite: true);
                    var length = new FileInfo(destination).Length;
                    return Task.FromResult(new OpResult(byteCount, Items: 1, length, $"length={length}"));
                });

                var baseline = new Func<CancellationToken, Task<OpResult>>(async cancellationToken =>
                {
                    var destination = Take();
                    await using var from = new FileStream(
                        source,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                            BufferSize = BufferBytes,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        });
                    await using var to = new FileStream(
                        destination,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Create,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = BufferBytes,
                            Options = FileOptions.Asynchronous,
                        });
                    await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
                    var length = new FileInfo(destination).Length;
                    return new OpResult(byteCount, Items: 1, length, $"length={length}");
                });

                return Task.FromResult(new BenchCase
                {
                    Run = run,
                    Baseline = baseline,
                    BaselineName = "FileStream.CopyToAsync (1 MiB buffers)",
                    ProcessWideAllocation = true,
                    Iterations = iterations,
                    Warmup = large ? 1 : Harness.DefaultWarmup,
                    WatchMemory = large,
                    Note = large
                        ? "n=5: the 100 MiB source and every destination are really written, no hole"
                        : "the payload is really written (no hole); both sides verify the destination length from metadata",
                });
            });
    }
}
