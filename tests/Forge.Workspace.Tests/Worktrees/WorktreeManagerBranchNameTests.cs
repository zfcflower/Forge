using System.Diagnostics;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

// Covers WorktreeManager.CreateAsync(repo, title, branchName, ct) — the explicit-name overload:
// "Creates a worktree on a caller-chosen branch name. Unlike the title overload the name is never
// suffixed: an invalid name (git check-ref-format --branch) throws ArgumentException, and a branch
// that already exists throws GitException naming it, because the user typed that exact name. The
// worktree directory slug comes from the part after the last / and is suffixed only when that
// directory exists. A null or blank branchName falls back to the title overload."
public sealed class WorktreeManagerBranchNameTests
{
    private const int GitBudgetSeconds = 60;

    [Fact]
    public async Task Explicit_name_is_used_exactly_and_returned_in_the_session()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());

        var session = await manager.CreateAsync(repo.Root, "Some Title", "feature/login");

        Assert.Equal("feature/login", session.Branch);
        Assert.True(Directory.Exists(session.Path));
        Assert.Equal("feature/login", IsoGit.CurrentBranch(session.Path));
        Assert.Equal(repo.Head(), session.BaseCommit);
        Assert.Equal(repo.Head(), IsoGit.RevParse(session.Path, "HEAD"));
    }

    [Fact]
    public async Task Slash_segmented_name_takes_the_directory_slug_from_the_last_segment()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());

        var session = await manager.CreateAsync(repo.Root, "Some Title", "feature/x/y");

        Assert.Equal("feature/x/y", session.Branch);
        Assert.Equal("y", session.Slug);
        Assert.Equal("y", Path.GetFileName(session.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.True(Directory.Exists(session.Path));
    }

    [Fact]
    public async Task Directory_collision_suffixes_the_directory_but_keeps_the_branch()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());

        var first = await manager.CreateAsync(repo.Root, "Title", "team/alpha");
        var second = await manager.CreateAsync(repo.Root, "Title", "other/alpha");

        Assert.Equal("other/alpha", second.Branch);
        Assert.NotEqual(first.Path, second.Path);
        var leaf = Path.GetFileName(second.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.StartsWith("alpha-", leaf, StringComparison.Ordinal);
        Assert.True(Directory.Exists(second.Path));
        Assert.Equal("other/alpha", IsoGit.CurrentBranch(second.Path));
    }

    [Fact]
    public async Task Existing_branch_throws_GitException_naming_it_and_leaves_no_directory()
    {
        using var repo = IsoRepo.Create();
        repo.Run("branch", "taken");
        var manager = new WorktreeManager(new GitCli());
        var before = WorktreeDirs(repo.Root);

        var exception = await Assert.ThrowsAsync<GitException>(
            () => manager.CreateAsync(repo.Root, "Title", "taken"));

        Assert.Contains("taken", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, WorktreeDirs(repo.Root));
    }

    [Theory]
    [InlineData("bad..name")]
    [InlineData("-x")]
    [InlineData("has space")]
    [InlineData("feature.lock")]
    [InlineData("trailing.lock")]
    public async Task Invalid_name_throws_ArgumentException_and_creates_nothing(string branchName)
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());
        var before = WorktreeDirs(repo.Root);

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.CreateAsync(repo.Root, "Title", branchName));

        Assert.DoesNotContain(branchName, IsoGit.BranchList(repo.Root));
        Assert.Equal(before, WorktreeDirs(repo.Root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_name_falls_back_to_the_title_overload(string? branchName)
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());

        var session = await manager.CreateAsync(repo.Root, "My Title Here", branchName);

        Assert.StartsWith("forge/", session.Branch, StringComparison.Ordinal);
        Assert.True(Directory.Exists(session.Path));
    }

    [Fact]
    public async Task Blank_name_with_custom_prefix_falls_back_to_the_title_overload()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli(), new WorktreeOptions { BranchPrefix = "work/" });

        var session = await manager.CreateAsync(repo.Root, "My Title", null);

        Assert.StartsWith("work/", session.Branch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_name_wins_exactly_once()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());

        var first = manager.CreateAsync(repo.Root, "Title", "race/branch");
        var second = manager.CreateAsync(repo.Root, "Title", "race/branch");
        var outcomes = await Task.WhenAll(
            first.ContinueWith(t => (Ok: t.IsCompletedSuccessfully, Session: t.IsCompletedSuccessfully ? t.Result : null, Error: t.Exception?.InnerException)),
            second.ContinueWith(t => (Ok: t.IsCompletedSuccessfully, Session: t.IsCompletedSuccessfully ? t.Result : null, Error: t.Exception?.InnerException)));

        Assert.Equal(1, outcomes.Count(static o => o.Ok));
        var loser = outcomes.Single(static o => !o.Ok);
        Assert.IsType<GitException>(loser.Error);
        Assert.Contains("race/branch", loser.Error!.Message, StringComparison.Ordinal);

        var winner = outcomes.Single(static o => o.Ok);
        Assert.Equal("race/branch", winner.Session!.Branch);
        Assert.Equal(1, WorktreeDirs(repo.Root).Count(static d => Path.GetFileName(d)!.StartsWith("branch", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelled_token_throws_and_creates_nothing()
    {
        using var repo = IsoRepo.Create();
        var manager = new WorktreeManager(new GitCli());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CreateAsync(repo.Root, "Title", "cancel/me", cancelled.Token));

        Assert.DoesNotContain("cancel/me", IsoGit.BranchList(repo.Root));
        Assert.Equal([], WorktreeDirs(repo.Root));
    }

    private static IReadOnlyList<string> WorktreeDirs(string repositoryRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var container = Path.Combine(parent!, ".forge-worktrees", Path.GetFileName(repositoryRoot));
        if (!Directory.Exists(container))
        {
            return [];
        }

        return Directory.GetDirectories(container).OrderBy(static d => d, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Isolated git runner: per-process empty global/system stubs so the developer's config never leaks in.</summary>
    private static class IsoGit
    {
        private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "forge-wt-branch-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            var global = Path.Combine(dir, "global.config");
            var system = Path.Combine(dir, "system.config");
            File.WriteAllText(global, string.Empty, new UTF8Encoding(false));
            File.WriteAllText(system, string.Empty, new UTF8Encoding(false));
            return (global, system);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static (int ExitCode, string Output, string Error) Run(string directory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[]
            {
                "-c", "user.name=Forge Test",
                "-c", "user.email=forge@example.com",
                "-c", "core.autocrlf=false",
                "-c", "core.safecrlf=false",
                "-c", "init.defaultBranch=main",
                "-c", "commit.gpgsign=false",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
            startInfo.Environment["GIT_PAGER"] = "cat";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Stubs.Value.Global;
            startInfo.Environment["GIT_CONFIG_SYSTEM"] = Stubs.Value.System;
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(GitBudgetSeconds)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                }

                throw new TimeoutException("git " + string.Join(' ', arguments) + " timed out.");
            }

            return (process.ExitCode, stdout.Result, stderr.Result);
        }

        public static string RunChecked(string directory, params string[] arguments)
        {
            var (exitCode, output, error) = Run(directory, arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
            }

            return output;
        }

        public static string RevParse(string repository, string revision) =>
            RunChecked(repository, "rev-parse", revision).Trim();

        public static string CurrentBranch(string repository) =>
            RunChecked(repository, "branch", "--show-current").Trim();

        public static IReadOnlyList<string> BranchList(string repository) =>
            RunChecked(repository, "branch", "--list", "--format=%(refname:short)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .ToList();
    }

    /// <summary>Throwaway repository on branch main with pinned identity; global config stays out via IsoGit.</summary>
    private sealed class IsoRepo : IDisposable
    {
        private readonly TempDirectory sandbox;

        private IsoRepo(TempDirectory sandbox, string root)
        {
            this.sandbox = sandbox;
            Root = root;
        }

        public string Root { get; }

        public static IsoRepo Create()
        {
            var sandbox = new TempDirectory();
            var root = Path.Combine(sandbox.Path, "repo");
            Directory.CreateDirectory(root);
            IsoGit.RunChecked(sandbox.Path, "init", "-q", "-b", "main", root);
            IsoGit.RunChecked(root, "config", "--local", "user.name", "Forge Test");
            IsoGit.RunChecked(root, "config", "--local", "user.email", "forge@example.com");
            IsoGit.RunChecked(root, "config", "--local", "core.autocrlf", "false");
            IsoGit.RunChecked(root, "config", "--local", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(root, "README.md"), "# repo\n", new UTF8Encoding(false));
            IsoGit.RunChecked(root, "add", "-A");
            IsoGit.RunChecked(root, "commit", "-q", "-m", "initial");
            return new IsoRepo(sandbox, root);
        }

        public string Head() => IsoGit.RevParse(Root, "HEAD");

        public void Run(params string[] arguments) => IsoGit.RunChecked(Root, arguments);

        public void Dispose() => sandbox.Dispose();
    }
}
