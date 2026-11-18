using Forge.App.Git;
using Xunit;

namespace Forge.App.Tests;

public sealed class RepositoryRemotesTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-remotes-" + Guid.NewGuid().ToString("n"));

    public RepositoryRemotesTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("[core]\n\tbare = false\n[remote \"origin\"]\n\turl = x\n", true)]
    [InlineData("[core]\r\n\tbare = false\r\n[REMOTE \"origin\"]\r\n\turl = x\r\n", true)]
    [InlineData("[remote.origin]\n\turl = x\n", true)]
    [InlineData("[remotes \"x\"]\n", false)]
    [InlineData("[core]\n\tbare = false\n[branch \"main\"]\n\tremote = origin\n", false)]
    public void ConfigHasRemote_reads_the_sections_git_lists(string config, bool expected)
    {
        var path = Write("c/config", config);
        Assert.Equal(expected, RepositoryRemotes.ConfigHasRemote(path));
    }

    [Fact]
    public void A_worktree_resolves_to_the_main_repository_config()
    {
        var main = Write("main/.git/config", "[remote \"origin\"]\n");
        Write("main/.git/worktrees/w/commondir", "../..\n");
        var worktree = Path.Combine(dir, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(dir, "main", ".git", "worktrees", "w") + "\n");

        Assert.True(RepositoryRemotes.SameConfig(main, RepositoryRemotes.FindConfig(worktree)));
    }

    [Fact]
    public void A_submodule_without_commondir_uses_its_own_config()
    {
        var module = Write("super/.git/modules/m/config", "[core]\n");
        var checkout = Path.Combine(dir, "super", "m");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: ../.git/modules/m\n");

        Assert.True(RepositoryRemotes.SameConfig(module, RepositoryRemotes.FindConfig(checkout)));
    }

    [Fact]
    public void A_malformed_gitdir_line_means_no_repository_instead_of_throwing()
    {
        var folder = Path.Combine(dir, "bad");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: \0bad\n");

        Assert.Null(RepositoryRemotes.FindConfig(folder));
    }

    [Fact]
    public void One_failing_listener_does_not_keep_the_others_from_hearing()
    {
        var heard = 0;
        void Throwing(string path) => throw new InvalidOperationException("boom");
        void Counting(string path) => heard++;
        RepositoryRemotes.Added += Throwing;
        RepositoryRemotes.Added += Counting;
        try
        {
            RepositoryRemotes.RaiseAdded("x");
        }
        finally
        {
            RepositoryRemotes.Added -= Throwing;
            RepositoryRemotes.Added -= Counting;
        }

        Assert.Equal(1, heard);
    }
}
