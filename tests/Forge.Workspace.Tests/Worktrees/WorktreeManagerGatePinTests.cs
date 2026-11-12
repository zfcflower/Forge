using System.Reflection;
using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

/// <summary>
/// Pins the <see cref="WorktreeManager"/> creation gates: concurrent same-title creates
/// serialize per repository and still mint distinct worktrees/branches, every create
/// releases its entry in the static gate map, and a create cancelled while queued on the
/// gate throws <see cref="OperationCanceledException"/> without wedging later creates.
/// The gate map is internal (no <c>InternalsVisibleTo</c> for this test project), so it
/// is observed through reflection; if those members ever disappear the helpers below
/// fail loudly instead of silently passing. Leak freedom is asserted per repository key
/// (parallel-safe: a whole-process <c>ActiveCreateGates</c> count is polluted by other
/// test classes creating worktrees concurrently); behaviour (distinct worktrees, OCE,
/// later creates working) is asserted directly regardless.
/// </summary>
public sealed class WorktreeManagerGatePinTests
{
    private static readonly Assembly WorkspaceAssembly = typeof(GitCli).Assembly;
    private static readonly Type ManagerType =
        WorkspaceAssembly.GetType("Forge.Workspace.WorktreeManager", throwOnError: true)!;

    private static bool HasGate(string key)
    {
        // The static gate map is global to the process, so a whole-process count is polluted
        // by parallel tests; per-key absence is the precise, parallel-safe leak pin. The
        // map's own lock (System.Threading.Lock) returns a ref struct scope, which cannot
        // be entered via reflection, so the read is lock-free with a bounded retry: our own
        // release already happened-before this check on the same thread, and only a torn
        // read caused by another repo's concurrent gate traffic can throw here.
        var gates = ManagerType.GetField("CreateGates", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("WorktreeManager", "CreateGates");
        var map = (System.Collections.IDictionary)gates.GetValue(null)!;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return map.Contains(key);
            }
            catch (InvalidOperationException) when (attempt < 1000)
            {
            }
        }
    }

    private static string GateKey(string repositoryRoot)
    {
        var paths = WorkspaceAssembly.GetType("Forge.Workspace.WorkspacePaths", throwOnError: true)!;
        var normalize = paths.GetMethod(
            "Normalize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("WorkspacePaths", "Normalize");
        return (string)normalize.Invoke(null, [repositoryRoot])!;
    }

    private static object AcquireGate(string key)
    {
        var acquire = ManagerType.GetMethod("AcquireGate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("WorktreeManager", "AcquireGate");
        return acquire.Invoke(null, [key])!;
    }

    private static void ReleaseGate(string key, object gate)
    {
        var release = ManagerType.GetMethod("ReleaseGate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("WorktreeManager", "ReleaseGate");
        release.Invoke(null, [key, gate]);
    }

    private static SemaphoreSlim GateSemaphore(object gate)
    {
        var field = gate.GetType().GetField(
            "Semaphore", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMemberException("CreateGate", "Semaphore");
        return (SemaphoreSlim)field.GetValue(gate)!;
    }

    private static int GateUsers(object gate)
    {
        var field = gate.GetType().GetField(
            "Users", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMemberException("CreateGate", "Users");
        return (int)field.GetValue(gate)!;
    }

    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.CommitFile("README.md", "# repo\n", "initial");
        return (sandbox, repo);
    }

    [Fact]
    public async Task Eight_way_concurrent_same_title_creates_produce_eight_distinct_worktrees()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = new WorktreeManager(new GitCli());
            var sessions = await Task.WhenAll(
                    Enumerable.Range(0, 8).Select(_ => manager.CreateAsync(repo.Root, "same title")))
                .WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Equal(8, sessions.Select(s => s.Branch).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                8,
                sessions.Select(s => Path.GetFullPath(s.Path))
                    .Distinct(OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal)
                    .Count());
            Assert.All(sessions, s => Assert.StartsWith("forge/", s.Branch, StringComparison.Ordinal));
            Assert.All(sessions, s => Assert.True(Directory.Exists(s.Path)));
            Assert.False(HasGate(GateKey(repo.Root)));
        }
    }

    [Fact]
    public async Task Create_cancelled_while_waiting_for_the_gate_throws_and_leaves_no_gate_behind()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = new WorktreeManager(new GitCli());
            var key = GateKey(repo.Root);

            // Occupy the single permit so the create below parks inside the gate's WaitAsync.
            var held = AcquireGate(key);
            try
            {
                await GateSemaphore(held).WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                ReleaseGate(key, held);
                throw;
            }

            using var cancellation = new CancellationTokenSource();
            var queued = manager.CreateAsync(repo.Root, "gated title", cancellation.Token);
            try
            {
                // The queued create must have taken its place behind us (Users 2) before we cancel,
                // otherwise the cancellation would not prove anything about gate waiting.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (GateUsers(held) < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }

                Assert.Equal(2, GateUsers(held));
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            }
            finally
            {
                GateSemaphore(held).Release();
                ReleaseGate(key, held);
            }

            Assert.False(HasGate(key));

            // Later creates still work: the cancelled waiter released its gate bookkeeping.
            var session = await manager.CreateAsync(repo.Root, "after gate").WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(Directory.Exists(session.Path));
            Assert.StartsWith("forge/", session.Branch, StringComparison.Ordinal);
            Assert.False(HasGate(key));
        }
    }
}
