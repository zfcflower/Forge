using System.Diagnostics;
using Forge.App.Git;
using Forge.Core.Tools;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

public sealed class GitStepChangeProbeTests : IDisposable
{
    private readonly string repo = Path.Combine(Path.GetTempPath(), "forge-probe-" + Guid.NewGuid().ToString("n"));
    private readonly GitCli git = new();

    public GitStepChangeProbeTests()
    {
        Directory.CreateDirectory(repo);
        Run("init", "-q");
        Run("config", "user.email", "t@t");
        Run("config", "user.name", "t");
        Run("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
        Run("add", "-A");
        Run("commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(repo, true);
        }
        catch (IOException)
        {
        }
    }

    private void Run(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private Task<StepChangeProbe> Probe() =>
        new GitStepChangeProbe(git, (_, _) => Task.FromResult(WorktreeResolution.Own(repo)))
            .ProbeAsync("s1", "wd", CancellationToken.None);

    [Fact]
    public async Task Unchanged_worktree_is_clean_with_stable_tree()
    {
        var first = await Probe();
        var second = await Probe();
        Assert.Equal(StepChangeState.Clean, first.State);
        Assert.False(string.IsNullOrEmpty(first.Tree));
        Assert.Equal(first.Tree, second.Tree);
    }

    [Fact]
    public async Task Editing_a_file_makes_it_dirty_and_changes_the_tree()
    {
        var before = await Probe();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two");
        var after = await Probe();
        Assert.Equal(StepChangeState.Dirty, after.State);
        Assert.NotEqual(before.Tree, after.Tree);
    }

    [Fact]
    public async Task Untracked_file_counts_as_dirty()
    {
        File.WriteAllText(Path.Combine(repo, "new.txt"), "x");
        Assert.Equal(StepChangeState.Dirty, (await Probe()).State);
    }

    [Fact]
    public async Task Committing_the_edit_keeps_the_same_tree_and_turns_clean()
    {
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two");
        var dirty = await Probe();
        Run("add", "-A");
        Run("commit", "-q", "-m", "edit");
        var clean = await Probe();
        Assert.Equal(StepChangeState.Clean, clean.State);
        Assert.Equal(dirty.Tree, clean.Tree);
    }

    [Fact]
    public async Task Reverting_an_edit_restores_the_original_tree()
    {
        var before = await Probe();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "two");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
        Assert.Equal(before.Tree, (await Probe()).Tree);
    }

    [Fact]
    public async Task Probe_does_not_stage_anything()
    {
        File.WriteAllText(Path.Combine(repo, "new.txt"), "x");
        await Probe();
        Run("diff", "--cached", "--quiet");
    }
}
