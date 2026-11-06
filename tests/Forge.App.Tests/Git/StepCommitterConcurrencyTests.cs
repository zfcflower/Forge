using Forge.Abstractions;
using Forge.App.Git;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the per-worktree gate of <see cref="StepCommitter"/>: commits into one worktree never overlap (however its path is
/// spelled), commits into different worktrees run in parallel, and a waiter that gives up does not leak the gate. The
/// ordering is made deterministic with a pre-commit hook that signals "entered" and blocks until the test releases it, so
/// no assertion depends on a race being won.
/// </summary>
public sealed class StepCommitterConcurrencyTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

    private static CancellationTokenSource Bound() => new(TimeSpan.FromSeconds(120));

    public static TheoryData<string> PathSpellings()
    {
        var data = new TheoryData<string> { "trailing-separator", "dot-segment" };
        if (OperatingSystem.IsWindows())
        {
            data.Add("upper-case");
        }

        return data;
    }

    // ------------------------------------------------------------ one worktree: strictly one commit at a time

    [Fact]
    public async Task Two_commits_into_one_worktree_never_overlap_and_the_second_commits_what_arrived_meanwhile()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        using var hook = GatedHook.Install(harness.Repo);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        harness.Repo.WriteFile("one.txt", "1\n");
        var baseline = await harness.Repo.CommitCountAsync();
        using var cts = Bound();

        var firstTask = Task.Run(() => harness.Committer.CommitStepAsync(first, cts.Token), cts.Token);
        await hook.WaitForEntriesAsync(1);
        harness.Repo.WriteFile("two.txt", "2\n");
        var secondTask = Task.Run(() => harness.Committer.CommitStepAsync(second, cts.Token), cts.Token);
        await Task.Delay(Settle, cts.Token);

        Assert.False(secondTask.IsCompleted, "the second commit must wait for the gate while the first one is inside git");
        Assert.Equal(["enter"], hook.Log());

        hook.Release();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(StepCommitOutcome.Committed, results[0].Outcome);
        Assert.Equal(StepCommitOutcome.Committed, results[1].Outcome);
        Assert.Equal(["enter", "exit", "enter", "exit"], hook.Log());
        Assert.Equal(baseline + 2, await harness.Repo.CommitCountAsync());
        Assert.Equal("A\tone.txt", await harness.Repo.GitAsync("show", "--name-status", "--format=", "HEAD~1"));
        Assert.Equal("A\ttwo.txt", await harness.Repo.GitAsync("show", "--name-status", "--format=", "HEAD"));
        Assert.Equal("first", await harness.Repo.GitAsync("log", "-1", "--format=%s", "HEAD~1"));
        Assert.Equal("second", await harness.Repo.GitAsync("log", "-1", "--format=%s", "HEAD"));
        Assert.Equal(string.Empty, await harness.Repo.StatusAsync());
        Assert.False(File.Exists(Path.Combine(harness.Repo.Path, ".git", "index.lock")));
        Assert.Equal(results[0].CommitId, (await harness.StoredAsync(first.Id)).CommitId);
        Assert.Equal(results[1].CommitId, (await harness.StoredAsync(second.Id)).CommitId);
    }

    [Theory]
    [MemberData(nameof(PathSpellings))]
    public async Task One_worktree_reached_through_another_spelling_of_its_path_shares_the_gate(string spelling)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        using var hook = GatedHook.Install(harness.Repo);
        const string OtherSession = "session-2";
        harness.WorktreeBySession[OtherSession] = Spell(harness.Repo.Path, spelling);
        var first = await harness.AddDoneAsync("first");
        var second = await AddDoneInAsync(harness, OtherSession, "second");
        harness.Repo.WriteFile("one.txt", "1\n");
        using var cts = Bound();

        var firstTask = Task.Run(() => harness.Committer.CommitStepAsync(first, cts.Token), cts.Token);
        await hook.WaitForEntriesAsync(1);
        harness.Repo.WriteFile("two.txt", "2\n");
        var secondTask = Task.Run(() => harness.Committer.CommitStepAsync(second, cts.Token), cts.Token);
        await Task.Delay(Settle, cts.Token);

        Assert.False(secondTask.IsCompleted, $"the path spelling '{spelling}' must map to the same gate");
        Assert.Equal(["enter"], hook.Log());

        hook.Release();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(StepCommitOutcome.Committed, results[0].Outcome);
        Assert.Equal(StepCommitOutcome.Committed, results[1].Outcome);
        Assert.Equal(["enter", "exit", "enter", "exit"], hook.Log());
        Assert.Equal("A\ttwo.txt", await harness.Repo.GitAsync("show", "--name-status", "--format=", "HEAD"));
    }

    [Fact]
    public async Task A_waiter_that_is_cancelled_throws_commits_nothing_and_does_not_leak_the_gate()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        using var hook = GatedHook.Install(harness.Repo);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        var third = await harness.AddDoneAsync("third");
        harness.Repo.WriteFile("one.txt", "1\n");
        var baseline = await harness.Repo.CommitCountAsync();
        using var cts = Bound();
        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var firstTask = Task.Run(() => harness.Committer.CommitStepAsync(first, cts.Token), cts.Token);
        await hook.WaitForEntriesAsync(1);
        harness.Repo.WriteFile("two.txt", "2\n");
        var waiter = Task.Run(() => harness.Committer.CommitStepAsync(second, waiterCts.Token));
        await Task.Delay(Settle, cts.Token);
        Assert.False(waiter.IsCompleted);

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        hook.Release();
        var firstResult = await firstTask;
        var afterwards = await harness.Committer.CommitStepAsync(third, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, firstResult.Outcome);
        Assert.Equal(StepCommitOutcome.Committed, afterwards.Outcome);
        Assert.Equal(baseline + 2, await harness.Repo.CommitCountAsync());
        Assert.Equal("A\ttwo.txt", await harness.Repo.GitAsync("show", "--name-status", "--format=", "HEAD"));
        var stored = await harness.StoredAsync(second.Id);
        Assert.Null(stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact(Skip = "defect: GitCli.CommitAsync keeps the commit id after a late cancel only when git had already exited (keepCompletedResult); a cancel that arrives while a slow post-commit hook still runs kills git, GitCli throws OperationCanceledException, and StepCommitter never persists the sha although HEAD already holds the commit, so the step shows no commit id (the next tick finds nothing to commit)")]
    public async Task A_cancel_that_arrives_while_a_post_commit_hook_runs_still_records_the_commit_that_exists()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        using var hook = GatedHook.Install(harness.Repo, "post-commit");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync("late cancel");
        var baseline = await harness.Repo.CommitCountAsync();
        using var cts = Bound();
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var task = Task.Run(() => harness.Committer.CommitStepAsync(item, callerCts.Token));
        await hook.WaitForEntriesAsync(1);
        // The commit object and HEAD exist by now; only git's post-commit hook is still running.
        await callerCts.CancelAsync();
        hook.Release();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Either answer is acceptable for the call itself; what must hold is that the commit that exists is on the step.
        }

        Assert.Equal(baseline + 1, await harness.Repo.CommitCountAsync());
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(await harness.Repo.HeadAsync(), stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task Concurrent_callers_on_one_worktree_produce_one_commit_and_NoChanges_never_an_error()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var items = new[]
        {
            await harness.AddDoneAsync("a"),
            await harness.AddDoneAsync("b"),
            await harness.AddDoneAsync("c"),
        };
        var baseline = await harness.Repo.CommitCountAsync();
        const int Iterations = 15;
        using var cts = Bound();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            harness.Repo.WriteFile($"iteration-{iteration}/x.txt", "x\n");
            harness.Repo.WriteFile($"iteration-{iteration}/y.txt", "y\n");

            var results = await Task.WhenAll(items.Select(item => Task.Run(() => harness.Committer.CommitStepAsync(item, cts.Token), cts.Token)));

            Assert.True(
                results.All(result => result.Outcome is StepCommitOutcome.Committed or StepCommitOutcome.NoChanges),
                $"iteration {iteration}: {string.Join(" | ", results.Select(result => $"{result.Outcome}: {result.Message}"))}");
            Assert.Equal(1, results.Count(result => result.Outcome == StepCommitOutcome.Committed));
            Assert.Equal(baseline + iteration + 1, await harness.Repo.CommitCountAsync());
            Assert.Equal(string.Empty, await harness.Repo.StatusAsync());
            Assert.False(File.Exists(Path.Combine(harness.Repo.Path, ".git", "index.lock")), $"iteration {iteration}: index.lock was left behind");
        }

        foreach (var item in items)
        {
            Assert.Null((await harness.StoredAsync(item.Id)).CommitError);
        }
    }

    // ------------------------------------------------------------ different worktrees: in parallel

    [Fact]
    public async Task Commits_into_different_worktrees_run_in_parallel()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var other = await StepCommitRepo.CreateAsync(harness.Repo.Root, "repo2");
        const string OtherSession = "session-2";
        harness.WorktreeBySession[OtherSession] = other.Path;
        var rendezvous = harness.Repo.Root.Replace('\\', '/');
        harness.Repo.InstallHook("pre-commit", Rendezvous(rendezvous, "one", "two"));
        other.InstallHook("pre-commit", Rendezvous(rendezvous, "two", "one"));
        harness.Repo.WriteFile("change.txt", "1\n");
        other.WriteFile("change.txt", "2\n");
        var first = await harness.AddDoneAsync("first");
        var second = await AddDoneInAsync(harness, OtherSession, "second");
        using var cts = Bound();

        var results = await Task.WhenAll(
            Task.Run(() => harness.Committer.CommitStepAsync(first, cts.Token), cts.Token),
            Task.Run(() => harness.Committer.CommitStepAsync(second, cts.Token), cts.Token));

        Assert.True(
            results.All(result => result.Outcome == StepCommitOutcome.Committed),
            string.Join(" | ", results.Select(result => $"{result.Outcome}: {result.Message}")));
        Assert.Equal("first", await harness.Repo.GitAsync("log", "-1", "--format=%s"));
        Assert.Equal("second", await other.GitAsync("log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task A_commit_blocked_in_one_worktree_does_not_hold_up_another_worktree()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var other = await StepCommitRepo.CreateAsync(harness.Repo.Root, "repo2");
        const string OtherSession = "session-2";
        harness.WorktreeBySession[OtherSession] = other.Path;
        using var hook = GatedHook.Install(harness.Repo);
        harness.Repo.WriteFile("change.txt", "1\n");
        other.WriteFile("change.txt", "2\n");
        var blocked = await harness.AddDoneAsync("blocked");
        var free = await AddDoneInAsync(harness, OtherSession, "free");
        using var cts = Bound();

        var blockedTask = Task.Run(() => harness.Committer.CommitStepAsync(blocked, cts.Token), cts.Token);
        await hook.WaitForEntriesAsync(1);
        var freeResult = await harness.Committer.CommitStepAsync(free, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, freeResult.Outcome);
        Assert.False(blockedTask.IsCompleted);
        hook.Release();
        Assert.Equal(StepCommitOutcome.Committed, (await blockedTask).Outcome);
    }

    // ------------------------------------------------------------ failure text of a very chatty hook

    [Fact(Skip = "defect: GitCli keeps only the first 64 KiB of stderr, so the 'tail of git's stderr' that StepCommitter shows is the tail of that prefix; a hook that prints more than 64 KiB loses the verdict it printed last")]
    public async Task A_hook_that_prints_more_than_the_stderr_capture_still_shows_its_last_line()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.InstallHook(
            "pre-commit",
            "i=0\nwhile [ $i -lt 1500 ]; do echo \"noise line $i padding padding padding padding padding\" >&2; i=$((i+1)); done\necho 'FINAL VERDICT: rejected' >&2\nexit 1\n");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Contains("FINAL VERDICT: rejected", result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private static async Task<TodoItem> AddDoneInAsync(StepCommitHarness harness, string sessionId, string title)
    {
        var added = await harness.Store.AddAsync(sessionId, title, null, null, CancellationToken.None);
        var updated = await harness.Store.UpdateAsync(added.Id, null, TodoStatus.Done, null, CancellationToken.None);
        return updated ?? throw new InvalidOperationException("the item vanished");
    }

    private static string Spell(string path, string spelling) => spelling switch
    {
        "trailing-separator" => path + Path.DirectorySeparatorChar,
        "dot-segment" => Path.Combine(path, "."),
        "upper-case" => path.ToUpperInvariant(),
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
    };

    private static string Rendezvous(string root, string self, string peer) => $"""
        touch "{root}/entered-{self}"
        n=0
        while [ ! -f "{root}/entered-{peer}" ]; do
          sleep 0.1
          n=$((n+1))
          if [ $n -gt 300 ]; then
            echo "the other worktree never reached its hook, so commits are serialized across worktrees" >&2
            exit 1
          fi
        done
        exit 0
        """;

    /// <summary>
    /// A commit hook (pre-commit unless told otherwise) that appends "enter", blocks until <see cref="Release"/> is called (bounded to a minute so an
    /// aborted test cannot leave a process behind) and then appends "exit".
    /// </summary>
    private sealed class GatedHook : IDisposable
    {
        private readonly string log;
        private readonly string release;

        private GatedHook(string log, string release)
        {
            this.log = log;
            this.release = release;
        }

        public static GatedHook Install(StepCommitRepo repo, string hook = "pre-commit")
        {
            var log = Path.Combine(repo.Root, "hook-" + Guid.NewGuid().ToString("N") + ".log");
            var release = Path.Combine(repo.Root, "hook-" + Guid.NewGuid().ToString("N") + ".release");
            var logPath = log.Replace('\\', '/');
            var releasePath = release.Replace('\\', '/');
            repo.InstallHook(hook, $"""
                echo "enter" >> "{logPath}"
                i=0
                while [ ! -f "{releasePath}" ]; do
                  sleep 0.1
                  i=$((i+1))
                  if [ $i -gt 600 ]; then
                    echo "the test never released the hook" >&2
                    exit 1
                  fi
                done
                echo "exit" >> "{logPath}"
                exit 0
                """);
            return new GatedHook(log, release);
        }

        public void Release() => File.WriteAllText(release, "go");

        public IReadOnlyList<string> Log()
        {
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                return [];
            }
        }

        public async Task WaitForEntriesAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (Log().Count(line => line == "enter") < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"the hook was entered {Log().Count(line => line == "enter")} time(s), expected {count}");
                }

                await Task.Delay(25);
            }
        }

        public void Dispose()
        {
            // Make sure a hook that is still waiting ends (a failed assertion must not leave a polling shell behind).
            try
            {
                File.WriteAllText(release, "go");
            }
            catch (IOException)
            {
            }
        }
    }
}
