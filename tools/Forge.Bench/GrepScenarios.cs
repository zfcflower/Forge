using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The <c>grep</c> command on a 10k-file, ~14 MB repository: the managed engine (bounded channel, eight readers, literal
/// search) for a query with hits and one with no hits, each against the optional ripgrep backend for the same query. The
/// two backends must return the same number of matches - a difference is a defect in the parity contract, and the row's
/// acceptance check fails on it instead of hiding it behind two timings.
/// </summary>
internal static class GrepScenarios
{
    /// <summary>The <c>grep</c> default: the scan stops after this many matching lines.</summary>
    private const int Limit = 1000;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Query(fixtures, "hit", Fixtures.HitToken);
        yield return Query(fixtures, "miss", Fixtures.MissToken);
    }

    private static BenchScenario Query(Fixtures fixtures, string label, string token)
    {
        var expected = string.Equals(token, Fixtures.HitToken, StringComparison.Ordinal)
            ? Fixtures.RepositoryFilesWithToken * Fixtures.LinesOf(Fixtures.RepositoryFileBytes)
            : 0;

        return new BenchScenario(
            $"grep.literal.{label}.10k",
            "grep",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var root = fixtures.Repository();
                var ripgrep = await fixtures.RipgrepAsync(cancellationToken).ConfigureAwait(false);
                var request = new GrepRequest
                {
                    Root = root,
                    StartPath = root,
                    Pattern = token,
                    Limit = Limit,
                };

                Func<CancellationToken, Task<OpResult>>? baseline = ripgrep is null
                    ? null
                    : async ct =>
                    {
                        var outcome = await RipgrepBackend
                            .SearchAsync(request, ripgrep, fixtures.Runner, ct)
                            .ConfigureAwait(false);
                        if (!outcome.Succeeded || outcome.Result is null)
                        {
                            throw new InvalidOperationException($"the ripgrep backend did not complete: {outcome.FailureReason}");
                        }

                        return new OpResult(
                            Items: outcome.Result.FilesScanned,
                            Signature: outcome.Result.MatchCount,
                            Observation: $"matches={outcome.Result.MatchCount} files={outcome.Result.FilesScanned}");
                    };

                return new BenchCase
                {
                    Run = async ct =>
                    {
                        var result = await GrepEngine.SearchAsync(request, ct).ConfigureAwait(false);
                        return new OpResult(
                            Items: result.FilesScanned,
                            Signature: result.MatchCount,
                            Observation:
                                $"matches={result.MatchCount} files={result.FilesScanned} truncated={result.Truncated} (expected {expected})");
                    },
                    Baseline = baseline,
                    BaselineName = ripgrep is null ? null : "ripgrep backend (rg --version verified)",
                    ProcessWideAllocation = true,
                    Note = ripgrep is null
                        ? "ripgrep not found on PATH: baseline and parity comparison skipped"
                        : "the managed engine runs 8 reader tasks",
                    Check = comparison => new Verdict(
                        "grep backends return the same match count as each other and as the fixture expects",
                        comparison.Baseline is null
                            ? $"managed matches={comparison.Implementation.Signature}, expected={expected}, ripgrep unavailable"
                            : $"managed matches={comparison.Implementation.Signature}, "
                                + $"ripgrep matches={comparison.Baseline.Signature}, expected={expected}",
                        comparison.Implementation.Signature == expected
                        && (comparison.Baseline is null || comparison.Baseline.Signature == expected)),
                };
            });
    }
}
