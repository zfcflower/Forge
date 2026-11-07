using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The <c>find</c>/<c>ls</c> traversal: <see cref="FileWalker.Walk"/> over synthetic trees of 1k, 10k and 100k files
/// against <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>, plus one row that walks 10k files
/// through a <see cref="GitIgnore"/> filter - the shape <c>grep</c> uses - so the price of the ignore rules is visible
/// instead of being folded into the nearest number.
/// </summary>
internal static class WalkScenarios
{
    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Walk(fixtures, "1k", 1_000, Harness.DefaultIterations);
        yield return Walk(fixtures, "10k", 10_000, Harness.DefaultIterations);
        yield return Walk(fixtures, "100k", 100_000, 10);
        yield return Filtered(fixtures, "10k", 10_000);
    }

    private static BenchScenario Walk(Fixtures fixtures, string label, int fileCount, int iterations)
    {
        return new BenchScenario(
            $"walk.plain.{label}",
            "walk",
            Heavy: fileCount >= 100_000,
            PrepareAsync: _ =>
            {
                var root = fixtures.Tree($"walk-{label}", fileCount);
                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken => Task.FromResult(Count(
                        FileWalker.Walk(root, root, int.MaxValue, filter: null, cancellationToken))),
                    Baseline = cancellationToken => Task.FromResult(Count(
                        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))),
                    BaselineName = "Directory.EnumerateFileSystemEntries(AllDirectories)",
                    Iterations = iterations,
                    Warmup = fileCount >= 100_000 ? 1 : Harness.DefaultWarmup,
                    WatchMemory = fileCount >= 100_000,
                    Note = fileCount >= 100_000 ? "n=10" : null,
                    Check = SameCount("walk visits the same entry count as Directory.EnumerateFileSystemEntries"),
                });
            });
    }

    private static BenchScenario Filtered(Fixtures fixtures, string label, int fileCount)
    {
        return new BenchScenario(
            $"walk.gitignore.{label}",
            "walk",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var root = fixtures.Tree($"walk-{label}", fileCount);
                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken => Task.FromResult(Count(FileWalker.Walk(
                        root,
                        root,
                        int.MaxValue,
                        new GitIgnore(root),
                        cancellationToken))),
                    Baseline = cancellationToken => Task.FromResult(Count(
                        FileWalker.Walk(root, root, int.MaxValue, filter: null, cancellationToken))),
                    BaselineName = "FileWalker.Walk without a filter",
                    Note = "the shape grep uses: one GitIgnore per walk",
                });
            });
    }

    /// <summary>Counts what a walk yields. Both sides count every entry (directories included), which is the only number
    /// <see cref="Directory.EnumerateFileSystemEntries(string, string, SearchOption)"/> can produce without a stat per entry.</summary>
    private static OpResult Count(IEnumerable<WalkEntry> entries)
    {
        long files = 0, all = 0;
        foreach (var entry in entries)
        {
            all++;
            if (!entry.IsDirectory)
            {
                files++;
            }
        }

        return new OpResult(Items: all, Signature: all, Observation: $"files={files} entries={all}");
    }

    private static OpResult Count(IEnumerable<string> paths)
    {
        long all = 0;
        foreach (var _ in paths)
        {
            all++;
        }

        return new OpResult(Items: all, Signature: all, Observation: $"entries={all}");
    }

    private static Func<Comparison, Verdict?> SameCount(string requirement) =>
        comparison => comparison.Baseline is null
            ? null
            : new Verdict(
                requirement,
                $"{comparison.Implementation.Signature} entries vs {comparison.Baseline.Signature}",
                comparison.Implementation.Signature == comparison.Baseline.Signature);
}
