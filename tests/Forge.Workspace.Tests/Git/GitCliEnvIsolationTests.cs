using System.Diagnostics;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Pins <see cref="GitCli"/> environment isolation: a child process whose inherited
/// <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c>/<c>GIT_INDEX_FILE</c>/<c>GIT_OBJECT_DIRECTORY</c>/
/// <c>GIT_COMMON_DIR</c> all point at a decoy repository must still snapshot and read the
/// status of the target repository. The test-process environment is never mutated (the
/// poison lives only in the child's environment block), so this test is parallel-safe and
/// needs no dedicated xunit collection; a collection with
/// <c>DisableParallelization</c> was the fallback and was rejected for exactly that reason.
/// </summary>
public sealed class GitCliEnvIsolationTests
{
    private static string RunGit(string directory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Computes the snapshot tree without <see cref="GitCli"/>: seed a throwaway index from the
    /// real one, stage everything, and hash the tree, exactly what an independent implementation
    /// would do with raw git.
    /// </summary>
    private static string ExpectedTree(string root)
    {
        var gitDir = Path.Combine(root, ".git");
        var realIndex = Path.Combine(gitDir, "index");
        var temporaryIndex = Path.Combine(
            Path.GetTempPath(), "forge-expected-" + Guid.NewGuid().ToString("n") + ".index");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = temporaryIndex,
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        try
        {
            if (File.Exists(realIndex))
            {
                File.Copy(realIndex, temporaryIndex, overwrite: true);
            }

            RunGit(root, environment, "add", "--all");
            return RunGit(root, environment, "write-tree").Trim();
        }
        finally
        {
            try
            {
                File.Delete(temporaryIndex);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Poisoned_repository_location_variables_do_not_redirect_snapshot_or_status()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        using var target = new TestRepo(sandbox, name: "target");
        using var decoy = new TestRepo(sandbox, name: "decoy");

        target.Write("a.txt", "base\n");
        target.Commit("base");
        target.Write("a.txt", "working change\n");
        target.Write("staged.txt", "staged\n");
        target.RunGit("add", "staged.txt");
        target.Write("new.txt", "untracked\n");

        decoy.Write("other.txt", "decoy content\n");
        var decoyHead = decoy.Commit("decoy base");

        var expectedTree = ExpectedTree(target.Root);
        Assert.NotEmpty(expectedTree);

        var decoyGitDir = Path.Combine(decoy.Root, ".git");
        var poison = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = decoyGitDir,
            ["GIT_WORK_TREE"] = decoy.Root,
            ["GIT_INDEX_FILE"] = Path.Combine(decoyGitDir, "index"),
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(decoyGitDir, "objects"),
            ["GIT_COMMON_DIR"] = decoyGitDir,
        };
        var decoyObjectsBefore = RunGit(decoy.Root, null, "count-objects", "-v");
        var decoyStatusBefore = RunGit(decoy.Root, null, "status", "--porcelain");

        using var child = GitEnvIsolationChildProcess.Start(target.Root, poison);
        var stdoutTask = child.StandardOutput.ReadToEndAsync();
        var stderrTask = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (TimeoutException)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            Assert.Fail("The git child process did not exit within 120 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, child.ExitCode);
        Assert.DoesNotContain("fatal", stdout + stderr, StringComparison.OrdinalIgnoreCase);

        var treeLine = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("tree ", StringComparison.Ordinal));
        Assert.NotNull(treeLine);
        Assert.Equal(expectedTree, treeLine.Substring("tree ".Length).Trim());

        var entryPaths = stdout.Split('\n')
            .Where(l => l.StartsWith("entry ", StringComparison.Ordinal))
            .Select(l => l.Substring("entry ".Length).Trim())
            .ToArray();
        Assert.Contains(entryPaths, p => p.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(entryPaths, p => p.EndsWith("staged.txt", StringComparison.Ordinal));
        Assert.Contains(entryPaths, p => p.EndsWith("new.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(entryPaths, p => p.EndsWith("other.txt", StringComparison.Ordinal));

        // The decoy repository is untouched: no objects written through the poisoned variables,
        // no status change, same HEAD.
        Assert.Equal(decoyObjectsBefore, RunGit(decoy.Root, null, "count-objects", "-v"));
        Assert.Equal(decoyStatusBefore, RunGit(decoy.Root, null, "status", "--porcelain"));
        Assert.Equal(decoyHead, decoy.RunGit("rev-parse", "HEAD").Trim());
    }
}
