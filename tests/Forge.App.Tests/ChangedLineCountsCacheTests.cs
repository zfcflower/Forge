using Avalonia.Headless;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins the line-count cache's publication contract: the fingerprint and its result are written as one
/// immutable snapshot, so two overlapping refreshes can never leave the cache holding a fingerprint from
/// one computation and a result from another.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ChangedLineCountsCacheTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Unchanged_fingerprint_reuses_cached_line_counts()
    {
        var session = SharedUi.Session;
        var root = NewRoot();
        await InitRepoAsync(root);
        Write(root, "changed.txt", "a");

        var calls = 0;
        var panel = await session.Dispatch(
            () => NewPanel(
                root,
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(Counts("changed.txt", 7, 3));
                }),
            CancellationToken.None);

        var callsAfterRefresh = await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                await panel.RefreshAsync(CancellationToken.None);
                return Volatile.Read(ref calls);
            },
            CancellationToken.None);

        Assert.Equal(1, callsAfterRefresh);
    }

    [Fact]
    public async Task Changed_fingerprint_recomputes_line_counts()
    {
        var session = SharedUi.Session;
        var root = NewRoot();
        await InitRepoAsync(root);
        Write(root, "changed.txt", "a");

        var calls = 0;
        var panel = await session.Dispatch(
            () => NewPanel(
                root,
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(Counts("changed.txt", 7, 3));
                }),
            CancellationToken.None);

        var callsAfterRefresh = await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);

                // A size change rewrites the fingerprint (size + write time), forcing a recompute.
                Write(root, "changed.txt", "bbbb");
                await panel.RefreshAsync(CancellationToken.None);
                return Volatile.Read(ref calls);
            },
            CancellationToken.None);

        Assert.Equal(2, callsAfterRefresh);
    }

    [Fact]
    public async Task Interleaved_refreshes_publish_a_consistent_fingerprint_result_pair()
    {
        var session = SharedUi.Session;
        var root = NewRoot();
        await InitRepoAsync(root);
        Write(root, "changed.txt", "a");

        var call = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstFingerprint = null;
        string? secondFingerprint = null;
        Dictionary<string, FilesPanelViewModel.LineCount>? firstCounts = null;
        Dictionary<string, FilesPanelViewModel.LineCount>? secondCounts = null;

        async Task<Dictionary<string, FilesPanelViewModel.LineCount>> Factory(string fingerprint, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref call);
            if (n == 1)
            {
                firstFingerprint = fingerprint;
                firstCounts = Counts("first.txt", 11, 1);
                firstEntered.SetResult();
                await releaseFirst.Task;
                return firstCounts;
            }

            secondFingerprint = fingerprint;
            secondCounts = Counts("second.txt", 22, 2);
            return secondCounts;
        }

        var panel = await session.Dispatch(() => NewPanel(root, Factory), CancellationToken.None);

        await session.Dispatch(
            async () =>
            {
                // The first refresh misses and is held inside the factory; the second misses too and finishes
                // first. The first then finishes last, which is the interleaving that used to pair one write
                // from each refresh.
                var first = panel.RefreshAsync(CancellationToken.None);
                await firstEntered.Task;
                Write(root, "changed.txt", "bbbb");
                var second = panel.RefreshAsync(CancellationToken.None);
                releaseFirst.SetResult();
                await Task.WhenAll(first, second);
                return true;
            },
            CancellationToken.None);

        var snapshot = await session.Dispatch(() => panel.CountsSnapshotForTests, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.NotNull(firstFingerprint);
        Assert.NotNull(secondFingerprint);
        Assert.NotNull(firstCounts);
        Assert.NotNull(secondCounts);

        if (snapshot.Fingerprint == firstFingerprint)
        {
            Assert.Same(firstCounts, snapshot.Counts);
        }
        else if (snapshot.Fingerprint == secondFingerprint)
        {
            Assert.Same(secondCounts, snapshot.Counts);
        }
        else
        {
            Assert.Fail($"Cached fingerprint {snapshot.Fingerprint} matches neither refresh.");
        }
    }

    private static FilesPanelViewModel NewPanel(
        string root,
        Func<string, CancellationToken, Task<Dictionary<string, FilesPanelViewModel.LineCount>>> factory) =>
        new(new GitCli(), _ => { }, _ => { }, _ => { }, factory, new PanelContext("s1", root, null));

    private static Dictionary<string, FilesPanelViewModel.LineCount> Counts(string path, int added, int removed) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [path] = new FilesPanelViewModel.LineCount(added, removed, false),
        };

    private static async Task InitRepoAsync(string root)
    {
        var git = new GitCli();
        var result = await git.RunAsync(root, ["init"], CancellationToken.None);
        Assert.True(result.Success);
    }

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-counts-");
        roots.Add(root);
        return root;
    }

    private static void Write(string root, string relativePath, string content) =>
        TestTempFiles.WriteFile(root, relativePath, content);
}
