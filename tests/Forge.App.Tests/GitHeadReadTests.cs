using System.Diagnostics;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The tree refresh reads every registered worktree's HEAD, so <see cref="GitCli.GetHeadAsync"/> and
/// <see cref="GitCli.GetCheckoutStateAsync"/> are on the hot path. Both were cut to two git processes: one rev-parse answers
/// "work tree? bare? does HEAD have a commit?", and one symbolic-ref names the branch; the checkout read gets the running
/// operation out of the same rev-parse. These cases pin the answers the old three- and four-call versions gave.
/// </summary>
public sealed class GitHeadReadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-git-head-" + Guid.NewGuid().ToString("N"));

    public GitHeadReadTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A git object is read-only on Windows and a process handle can outlive the test; the temp directory is the
            // OS's problem then.
        }
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        // stderr is drained on its own task first: a full stderr pipe would block the child while stdout is still being read.
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} exited {process.ExitCode}: {stderr.GetAwaiter().GetResult()}");
    }

    private string Repository(string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        Git(directory, "init", "-q", "-b", "main");
        Git(directory, "config", "user.email", "forge@example.com");
        Git(directory, "config", "user.name", "Forge Test");
        return directory;
    }

    private static void Commit(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "file.txt"), "content\n");
        Git(directory, "add", "file.txt");
        Git(directory, "commit", "-q", "-m", "first");
    }

    [Fact]
    public async Task A_branch_with_a_commit_is_a_branch()
    {
        var directory = Repository("branch");
        Commit(directory);

        var head = await new GitCli().GetHeadAsync(directory, CancellationToken.None);

        Assert.Equal(GitHeadKind.Branch, head.Kind);
        Assert.Equal("main", head.Branch);
    }

    [Fact]
    public async Task An_unborn_branch_keeps_its_name()
    {
        var directory = Repository("unborn");

        var head = await new GitCli().GetHeadAsync(directory, CancellationToken.None);

        Assert.Equal(GitHeadKind.UnbornBranch, head.Kind);
        Assert.Equal("main", head.Branch);
    }

    [Fact]
    public async Task A_detached_head_is_reported_without_a_branch()
    {
        var directory = Repository("detached");
        Commit(directory);
        Git(directory, "checkout", "-q", "--detach");

        var head = await new GitCli().GetHeadAsync(directory, CancellationToken.None);

        Assert.Equal(GitHeadKind.Detached, head.Kind);
        Assert.Null(head.Branch);
    }

    [Fact]
    public async Task A_checkout_reads_its_running_operation_with_its_head()
    {
        var directory = Repository("merging");
        Commit(directory);
        File.WriteAllText(Path.Combine(directory, ".git", "MERGE_HEAD"), new string('0', 40) + "\n");

        var (head, operation) = await new GitCli().GetCheckoutStateAsync(directory, CancellationToken.None);

        Assert.Equal(GitHeadKind.Branch, head.Kind);
        Assert.Equal("main", head.Branch);
        Assert.Equal(GitOperationKind.Merge, operation);
    }

    [Fact]
    public async Task A_bare_repository_is_not_a_checkout()
    {
        var directory = Path.Combine(root, "bare.git");
        Directory.CreateDirectory(directory);
        Git(directory, "init", "-q", "--bare");

        var (head, operation) = await new GitCli().GetCheckoutStateAsync(directory, CancellationToken.None);

        Assert.Equal(GitHeadKind.NotRepository, head.Kind);
        Assert.Equal(GitOperationKind.None, operation);
        Assert.Equal(GitHeadKind.NotRepository, (await new GitCli().GetHeadAsync(directory, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task A_plain_folder_and_a_missing_one_are_not_repositories()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        var git = new GitCli();

        Assert.Equal(GitHeadKind.NotRepository, (await git.GetHeadAsync(plain, CancellationToken.None)).Kind);
        Assert.Equal(GitHeadKind.NotRepository, (await git.GetHeadAsync(Path.Combine(root, "missing"), CancellationToken.None)).Kind);
        Assert.Equal(GitHeadKind.NotRepository, (await git.GetCheckoutStateAsync(plain, CancellationToken.None)).Head.Kind);
        Assert.Equal(GitHeadKind.NotRepository, (await git.GetCheckoutStateAsync(Path.Combine(root, "missing"), CancellationToken.None)).Head.Kind);
    }
}
