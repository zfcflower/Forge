using System.Diagnostics;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

// F-WS-28: the in-process create gate serializes same-process creations, but another process can still take the
// minted branch or path between the branch-list read and the `git worktree add`. Those cross-process collisions
// are retried with the next suffix up to 20 times, then fail with the last git detail; an existing branch,
// directory or worktree is never deleted. The racing writer is a real separate process: a git wrapper that
// creates the requested branch right before forwarding the add, so git itself reports the collision.
public sealed class WorktreeManagerCrossProcessTests
{
    private const int GitBudgetSeconds = 60;

    [Fact]
    public async Task Cross_process_branch_collision_is_retried_with_the_next_suffix()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var repo = IsoRepo.Create();
        using var racer = new RacingGit(oneShot: true);
        var manager = new WorktreeManager(new GitCli(racer.Executable));

        var session = await manager.CreateAsync(repo.Root, "Fix Login");

        Assert.Equal("forge/fix-login-2", session.Branch);
        Assert.True(Directory.Exists(session.Path));
        Assert.Equal(session.Branch, IsoGit.CurrentBranch(session.Path));
        Assert.Equal(repo.Head(), session.BaseCommit);

        // The branch the other process won is kept, never deleted or reused.
        Assert.Contains("forge/fix-login", IsoGit.BranchList(repo.Root));
    }

    [Fact]
    public async Task Cross_process_collisions_exhausting_the_retry_cap_throw_with_the_last_git_detail()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var repo = IsoRepo.Create();
        using var racer = new RacingGit(oneShot: false);
        var manager = new WorktreeManager(new GitCli(racer.Executable));

        var exception = await Assert.ThrowsAsync<GitException>(
            () => manager.CreateAsync(repo.Root, "Fix Login"));

        // Twenty collisions are retried with the next suffix; the twenty-first escapes as the GitException carrying
        // the last git failure detail, not a silent success.
        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already exists", exception.StandardError, StringComparison.Ordinal);

        // Nothing was deleted on the way out: every branch the other process won is still there (one per attempt:
        // 20 retries plus the one that escaped), and none of the failed adds left a registered worktree behind.
        var branches = IsoGit.BranchList(repo.Root);
        Assert.Equal(21, branches.Count(static branch => branch.StartsWith("forge/fix-login", StringComparison.Ordinal)));
        Assert.Contains("forge/fix-login-20", branches);
        Assert.Contains("forge/fix-login-21", branches);
        var registered = IsoGit.RunChecked(repo.Root, "worktree", "list", "--porcelain")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(static line => line.StartsWith("worktree ", StringComparison.Ordinal));
        Assert.Equal(1, registered);
    }

    /// <summary>
    /// A stand-in for the system git that forwards every call verbatim, but before a <c>worktree add -b</c> it creates
    /// the requested branch itself - exactly what a concurrent process winning the name between the manager's branch
    /// read and its add looks like. <paramref name="oneShot"/> disarms after the first collision, so the manager's
    /// retry succeeds; otherwise every attempt collides until the retry cap is exhausted.
    /// </summary>
    private sealed class RacingGit : IDisposable
    {
        private readonly TempDirectory root = new();

        public RacingGit(bool oneShot)
        {
            ScriptDirectory = root.Combine("racer");
            Directory.CreateDirectory(ScriptDirectory);
            File.WriteAllText(Path.Combine(ScriptDirectory, oneShot ? "race.flag" : "always.flag"), "x", new UTF8Encoding(false));
            Executable = WriteScript();
        }

        private string ScriptDirectory { get; }

        public string Executable { get; }

        public void Dispose() => root.Dispose();

        private string WriteScript()
        {
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(ScriptDirectory, "git.cmd");
                const string script = """
                    @echo off
                    setlocal EnableExtensions EnableDelayedExpansion
                    set "DIR=%~dp0"
                    set "BRANCH="
                    :scan
                    if "%~1"=="" goto scanned
                    if /i "%~1"=="-b" if not "%~2"=="" set "BRANCH=%~2"
                    shift
                    goto scan
                    :scanned
                    if defined BRANCH if exist "%DIR%always.flag" git branch "%BRANCH%" >nul 2>&1
                    if defined BRANCH if exist "%DIR%race.flag" (
                        git branch "%BRANCH%" >nul 2>&1
                        del "%DIR%race.flag" >nul 2>&1
                    )
                    rem Arguments are forwarded through a variable with delayed expansion; the caret eaten from
                    rem HEAD^{commit} by the cmd command line before this script runs is put back first.
                    set "ARGS=%*"
                    set "ARGS=%ARGS:HEAD{commit}=HEAD^{commit}%"
                    git !ARGS!
                    exit /b %ERRORLEVEL%
                    """;
                File.WriteAllText(path, script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
                return path;
            }
            else
            {
                var path = Path.Combine(ScriptDirectory, "git");
                const string script = """
                    #!/bin/sh
                    dir=$(dirname "$0")
                    branch=""
                    prev=""
                    for arg in "$@"; do
                      if [ "$prev" = "-b" ]; then branch="$arg"; fi
                      prev="$arg"
                    done
                    if [ -n "$branch" ] && [ -f "$dir/always.flag" ]; then git branch "$branch" >/dev/null 2>&1; fi
                    if [ -n "$branch" ] && [ -f "$dir/race.flag" ]; then git branch "$branch" >/dev/null 2>&1; rm -f "$dir/race.flag"; fi
                    exec git "$@"
                    """;
                File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                return path;
            }
        }
    }

    /// <summary>Isolated git runner: per-process empty global/system stubs so the developer's config never leaks in.</summary>
    private static class IsoGit
    {
        private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "forge-wt-cross-tests", Guid.NewGuid().ToString("n"));
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

        public void Dispose() => sandbox.Dispose();
    }
}
