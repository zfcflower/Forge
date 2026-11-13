using Avalonia.Headless;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.Workspace;
using Xunit;
using static Forge.App.Tests.Git.GitPanelTesting;

namespace Forge.App.Tests.Git;

/// <summary>
/// Every <see cref="GitRemoteFailureKind"/> is driven through the panel with a failure git really produces (a loopback
/// stub that answers 401 or never answers, a closed port, a rejected push, a conflicting rebase, a missing remote, a
/// branch without upstream, a dirty work tree) and the row's <c>HintText</c>, <c>HasHint</c>, <c>MessageIsError</c>
/// and <c>HasResult</c> are pinned. The two kinds no verb of the row can produce any more (no upstream without pulling,
/// an unclassified failure) are covered by the classifier rows only. The pure classifier mapping
/// (<see cref="GitFailureClassifier"/>) is pinned on captured stderr next to it.
/// <para>Seam note: the panel has no injection point for a <see cref="GitRemoteResult"/> (it builds its own
/// <see cref="GitCli"/> calls), so the end-to-end rows rely on real git behaviour.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSyncFailureKindTests
{
    /// <summary>
    /// Kinds no verb of the row can produce any more. <c>NoUpstream</c> and <c>Other</c> were the branch switch's failures;
    /// the switch went away with the branch list, so those two are covered by the classifier rows below instead.
    /// </summary>
    private static readonly GitRemoteFailureKind[] NotReachableFromTheRow =
        [GitRemoteFailureKind.NoUpstream, GitRemoteFailureKind.Other];

    public static TheoryData<GitRemoteFailureKind> AllKinds()
    {
        var data = new TheoryData<GitRemoteFailureKind>();
        foreach (var kind in Enum.GetValues<GitRemoteFailureKind>())
        {
            if (!NotReachableFromTheRow.Contains(kind))
            {
                data.Add(kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Every_failure_kind_reaches_the_panel_with_its_hint_and_error_flag(GitRemoteFailureKind kind)
    {
        await using var scenario = await Scenario.CreateAsync(kind);
        var session = SharedUi.Session;
        var panel = await session.Dispatch(
            () => NewPanel(strategy: scenario.Strategy, networkTimeout: scenario.NetworkTimeout),
            CancellationToken.None);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    panel.Attach(Context("s1", scenario.WorkDirectory));
                    await WaitIdleAsync(panel);
                    Assert.True(panel.HasRepository);
                    Assert.False(panel.HasResult, "a fresh worktree has no result before the operation");
                    Assert.Equal(string.Empty, panel.HintText);

                    scenario.Act(panel);
                    await WaitIdleAsync(panel);

                    Assert.True(panel.HasResult, "the operation must report something for " + kind);
                    Assert.Equal(kind != GitRemoteFailureKind.None, panel.MessageIsError);
                    Assert.Equal(ExpectedHint(kind), panel.HintText);
                    Assert.Equal(kind != GitRemoteFailureKind.None, panel.HasHint);
                    Assert.NotEqual(string.Empty, panel.LastResultText);

                    // The failing operation never leaves the panel in a "refresh problem" state: the repository is fine.
                    Assert.False(panel.HasRefreshProblem);
                    scenario.AssertAfter(panel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(() => panel.Dispose(), CancellationToken.None);
        }
    }

    [Fact]
    public void The_hint_texts_are_empty_only_for_none_and_pairwise_different()
    {
        var hints = new Dictionary<GitRemoteFailureKind, string>();
        foreach (var kind in Enum.GetValues<GitRemoteFailureKind>())
        {
            hints[kind] = ExpectedHint(kind);
        }

        Assert.Equal(string.Empty, hints[GitRemoteFailureKind.None]);
        foreach (var (kind, hint) in hints.Where(static pair => pair.Key != GitRemoteFailureKind.None))
        {
            Assert.True(hint.Length > 0, kind + " has no hint text");
        }

        var distinct = hints.Where(static pair => pair.Key != GitRemoteFailureKind.None).Select(static pair => pair.Value).Distinct().Count();
        Assert.Equal(hints.Count - 1, distinct);
    }

    // ------------------------------------------------------------ the pure mapping

    public static TheoryData<GitRemoteFailureKind, int, string, bool> Captured() => new()
    {
        { GitRemoteFailureKind.None, 0, string.Empty, false },
        { GitRemoteFailureKind.AuthenticationFailed, 128, "fatal: could not read Username for 'https://example.test': terminal prompts disabled", false },
        { GitRemoteFailureKind.AuthenticationFailed, 128, "remote: Invalid username or password.\nfatal: Authentication failed for 'https://example.test/r.git/'", false },
        { GitRemoteFailureKind.NonFastForward, 1, " ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs", false },
        { GitRemoteFailureKind.NonFastForward, 1, "fatal: Not possible to fast-forward, aborting.", false },
        { GitRemoteFailureKind.Conflict, 1, "CONFLICT (content): Merge conflict in file.txt\nerror: could not apply 1234567... edit", false },
        { GitRemoteFailureKind.RemoteNotFound, 128, "fatal: '/no/such/path' does not appear to be a git repository", false },
        { GitRemoteFailureKind.RemoteNotFound, 128, "remote: Repository not found.\nfatal: repository 'https://example.test/r.git/' not found", false },
        { GitRemoteFailureKind.NoUpstream, 1, "There is no tracking information for the current branch.", false },
        { GitRemoteFailureKind.DirtyWorkTree, 1, "error: Your local changes to the following files would be overwritten by checkout:\n\tfile.txt", false },
        { GitRemoteFailureKind.Network, 128, "fatal: unable to access 'https://example.test/r.git/': Could not resolve host: example.test", false },
        { GitRemoteFailureKind.Network, 128, "fatal: unable to access 'http://127.0.0.1:9/r.git/': Failed to connect to 127.0.0.1 port 9: Connection refused", false },
        { GitRemoteFailureKind.TimedOut, -1, string.Empty, true },
        { GitRemoteFailureKind.Other, 1, "fatal: something nobody has a marker for", false },
    };

    [Fact]
    public void The_captured_mapping_rows_cover_every_failure_kind()
    {
        var covered = new HashSet<GitRemoteFailureKind>(Captured().Select(static row => (GitRemoteFailureKind)row[0]));
        foreach (var kind in Enum.GetValues<GitRemoteFailureKind>())
        {
            Assert.Contains(kind, covered);
        }
    }

    [Theory]
    [MemberData(nameof(Captured))]
    public void Classifier_maps_captured_stderr_to_the_kind(GitRemoteFailureKind expected, int exitCode, string stderr, bool timedOut)
    {
        var actual = GitFailureClassifier.Classify(new GitCommandResult(exitCode, string.Empty, stderr, timedOut));
        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------ expectations and scenarios

    /// <summary>The localized hint each kind must show (independent of how the panel computes it).</summary>
    private static string ExpectedHint(GitRemoteFailureKind kind) => kind switch
    {
        GitRemoteFailureKind.None => string.Empty,
        GitRemoteFailureKind.AuthenticationFailed => Loc.Instance[GitStrings.HintAuthentication],
        GitRemoteFailureKind.NonFastForward => Loc.Instance[GitStrings.HintNonFastForward],
        GitRemoteFailureKind.Conflict => Loc.Instance[GitStrings.HintConflict],
        GitRemoteFailureKind.RemoteNotFound => Loc.Instance[GitStrings.HintRemoteNotFound],
        GitRemoteFailureKind.NoUpstream => Loc.Instance[GitStrings.HintNoUpstream],
        GitRemoteFailureKind.DirtyWorkTree => Loc.Instance[GitStrings.HintDirtyWorkTree],
        GitRemoteFailureKind.Network => Loc.Instance[GitStrings.HintNetwork],
        GitRemoteFailureKind.TimedOut => Loc.Instance[GitStrings.HintTimedOut],
        GitRemoteFailureKind.Other => Loc.Instance[GitStrings.HintOther],
        _ => throw new InvalidOperationException("no expected hint for " + kind),
    };

    /// <summary>One real repository state plus the panel command that makes git fail (or succeed) in the wanted way.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly GitTestSandbox sandbox;
        private readonly StubHttpRemote? stub;

        private Scenario(
            GitTestSandbox sandbox,
            StubHttpRemote? stub,
            string directory,
            Action<GitSyncViewModel> act,
            Action<GitSyncViewModel>? assertAfter,
            GitPullStrategy strategy,
            TimeSpan? networkTimeout)
        {
            this.sandbox = sandbox;
            this.stub = stub;
            WorkDirectory = directory;
            Act = act;
            AssertAfterCore = assertAfter;
            Strategy = strategy;
            NetworkTimeout = networkTimeout;
        }

        public string WorkDirectory { get; }

        public Action<GitSyncViewModel> Act { get; }

        public GitPullStrategy Strategy { get; }

        public TimeSpan? NetworkTimeout { get; }

        private Action<GitSyncViewModel>? AssertAfterCore { get; }

        public void AssertAfter(GitSyncViewModel panel) => AssertAfterCore?.Invoke(panel);

        public static async Task<Scenario> CreateAsync(GitRemoteFailureKind kind)
        {
            await Task.CompletedTask;
            var sandbox = GitTestSandbox.Create();
            StubHttpRemote? stub = null;
            try
            {
                var work = sandbox.CloneTracked("a");
                Action<GitSyncViewModel> act = static p => p.FetchCommand.Execute(null);
                Action<GitSyncViewModel>? after = null;
                var directory = work;
                var strategy = GitPullStrategy.FastForwardOnly;
                TimeSpan? timeout = null;

                switch (kind)
                {
                    case GitRemoteFailureKind.None:
                        break;

                    case GitRemoteFailureKind.AuthenticationFailed:
                        stub = new StubHttpRemote(hold: false);
                        GitTestSandbox.PointOriginAt(work, stub.Url);
                        break;

                    case GitRemoteFailureKind.Network:
                        GitTestSandbox.PointOriginAt(work, StubHttpRemote.ClosedPortUrl());
                        break;

                    case GitRemoteFailureKind.TimedOut:
                        // The request is never answered; the panel's own (short) network timeout has to end the call.
                        stub = new StubHttpRemote(hold: true);
                        GitTestSandbox.PointOriginAt(work, stub.Url);
                        timeout = TimeSpan.FromMilliseconds(500);
                        break;

                    case GitRemoteFailureKind.RemoteNotFound:
                        GitTestSandbox.PointOriginAt(work, Path.Combine(sandbox.Root, "does-not-exist.git"));
                        break;

                    case GitRemoteFailureKind.NonFastForward:
                    {
                        var other = sandbox.Clone("other");
                        sandbox.CommitAndPush(other, "remote.txt", "from the other clone\n", "remote work");
                        sandbox.CommitFile(work, "local.txt", "local work\n", "local work");
                        act = static p => p.PushCommand.Execute(null);
                        break;
                    }

                    case GitRemoteFailureKind.Conflict:
                    {
                        var other = sandbox.Clone("other");
                        sandbox.CommitAndPush(other, "file.txt", "other version\n", "other edit");
                        sandbox.CommitFile(work, "file.txt", "local version\n", "local edit");
                        strategy = GitPullStrategy.Rebase;
                        act = static p => p.PullCommand.Execute(null);
                        after = static p => Assert.True(p.HasOperation, "the stopped rebase must be visible after the pull");
                        break;
                    }

                    case GitRemoteFailureKind.NoUpstream:
                    {
                        var branchless = sandbox.Clone("b");
                        GitTestShell.RunChecked(branchless, "checkout", "-q", "-b", "no-upstream");
                        directory = branchless;
                        act = static p => p.PullCommand.Execute(null);
                        break;
                    }

                    case GitRemoteFailureKind.DirtyWorkTree:
                    {
                        // The remote moves the very file the worktree has edited; that is what makes a fast-forward pull
                        // refuse with "Your local changes ... would be overwritten by merge".
                        var other = sandbox.Clone("other");
                        sandbox.CommitAndPush(other, "file.txt", "remote edit\n", "remote edit");
                        File.WriteAllText(Path.Combine(work, "file.txt"), "uncommitted edit\n");
                        act = static p => p.PullCommand.Execute(null);
                        break;
                    }

                    case GitRemoteFailureKind.Other:
                        act = static p =>
                        {
                        };
                        break;

                    default:
                        Assert.Fail("no real-git scenario for the failure kind " + kind + "; add one so the panel mapping stays pinned");
                        break;
                }

                return new Scenario(sandbox, stub, directory, act, after, strategy, timeout);
            }
            catch
            {
                if (stub is not null)
                {
                    await stub.DisposeAsync();
                }

                sandbox.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (stub is not null)
            {
                await stub.DisposeAsync();
            }

            sandbox.Dispose();
        }
    }
}
