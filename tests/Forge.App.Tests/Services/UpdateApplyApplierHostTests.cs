using System.Diagnostics;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Where an applier process runs from (review fix F1). The applier's whole job is renaming one directory — the staged tree in
/// the forward direction, the <c>.old-&lt;version&gt;</c> backup in the rollback direction — and Windows refuses to move a
/// directory that holds the image of a running process. Started out of that tree, a forward install silently fell back to
/// copying the whole tree (and left the staging tree behind) and the rollback direction could not claim its backup at all,
/// so the startup repair failed at every launch and the app never got past it. The applier is therefore started from a
/// neutral copy under <c>&lt;data&gt;/updates/.applier-&lt;guid&gt;/</c>, which the next start's housekeeping removes.
/// </summary>
public sealed class UpdateApplyApplierHostTests
{
    [Fact]
    public void The_applier_runs_from_a_neutral_copy_and_not_from_the_staging_tree()
    {
        using var sandbox = UpdateApplySandbox.Create();

        var start = UpdateApplierHost.CreateStartInfo(
            sandbox.DataRoot,
            sandbox.Staging,
            sandbox.Request.ToArguments(),
            out var error);

        Assert.NotNull(start);
        Assert.Null(error);

        var neutral = start!.WorkingDirectory;
        Assert.True(UpdateApplyPaths.IsStrictlyInside(neutral, UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot)), neutral);
        Assert.StartsWith(UpdateApplyPaths.ApplierPrefix, Path.GetFileName(neutral), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(neutral, UpdateApplyPaths.AppExecutableName), start.FileName);

        // Neither tree the applier renames is the tree it runs from; the staging tree is the one the swap moves aside.
        Assert.False(UpdateApplyPaths.IsInside(start.FileName, sandbox.Staging), "the applier must not run out of the staging tree");
        Assert.False(UpdateApplyPaths.IsInside(start.FileName, sandbox.Install), "the applier must not run out of the install directory");

        // The copy is a real tree of the staged payload, not a bare executable: the files beside the executable came along.
        Assert.True(File.Exists(UpdateApplyPaths.AppExecutable(neutral)));
        Assert.True(File.Exists(Path.Combine(neutral, "tree.txt")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(sandbox.Staging, "tree.txt")),
            File.ReadAllText(Path.Combine(neutral, "tree.txt")));

        // The private command line is passed through unchanged, and the source tree is only read.
        Assert.Equal<string>(sandbox.Request.ToArguments(), start.ArgumentList);
        Assert.True(File.Exists(UpdateApplyPaths.AppExecutable(sandbox.Staging)));
    }

    [Fact]
    public void The_recovery_applier_runs_from_a_neutral_copy_and_not_from_the_backup_it_claims()
    {
        using var sandbox = UpdateApplySandbox.Create();
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "previous");
        sandbox.UseStaging(sandbox.OldDirectory);
        sandbox.UseRecovery();

        var start = UpdateApplierHost.CreateStartInfo(
            sandbox.DataRoot,
            sandbox.Request.StagingDirectory,
            sandbox.Request.ToArguments(),
            out var error);

        Assert.NotNull(start);
        Assert.Null(error);
        Assert.True(UpdateApplyPaths.IsInside(start!.WorkingDirectory, UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot)));
        Assert.False(
            UpdateApplyPaths.IsInside(start.WorkingDirectory, sandbox.OldDirectory),
            "the applier must not run out of the backup it is about to claim and rename");

        // A copy, not a move: the backup is still where the claim expects it.
        Assert.True(Directory.Exists(sandbox.OldDirectory));
        Assert.True(File.Exists(UpdateApplyPaths.AppExecutable(start.WorkingDirectory)));
    }

    [Fact]
    public void A_source_tree_without_the_published_executable_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        File.Delete(UpdateApplyPaths.AppExecutable(sandbox.Staging));

        Assert.Null(UpdateApplierHost.CreateStartInfo(sandbox.DataRoot, sandbox.Staging, [], out var error));
        Assert.Contains(UpdateApplyPaths.AppExecutableName, error, StringComparison.Ordinal);

        // Nothing was left behind for the housekeeping to collect.
        Assert.Empty(Directory.EnumerateDirectories(
            UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot),
            UpdateApplyPaths.ApplierPrefix + "*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void The_start_is_a_detached_process_without_a_window()
    {
        using var sandbox = UpdateApplySandbox.Create();

        var start = UpdateApplierHost.CreateStartInfo(sandbox.DataRoot, sandbox.Staging, sandbox.Request.ToArguments(), out _);

        Assert.NotNull(start);
        Assert.False(start!.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(start.WorkingDirectory, Path.GetDirectoryName(start.FileName));
    }

    [Fact]
    public void The_housekeeping_removes_the_neutral_copies_of_finished_appliers()
    {
        using var root = new TempTree("forge-applier-maintain-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "current");

        var updatesRoot = UpdateApplyPaths.UpdatesRoot(dataRoot);
        var first = Path.Combine(updatesRoot, UpdateApplyPaths.ApplierPrefix + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(updatesRoot, UpdateApplyPaths.ApplierPrefix + Guid.NewGuid().ToString("N"));
        UpdateApplySandbox.WriteTree(first, "applier");
        UpdateApplySandbox.WriteTree(second, "applier");
        var staged = Path.Combine(updatesRoot, "1.3.0");
        UpdateApplySandbox.WriteTree(staged, "staged");

        var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);

        Assert.Contains(first, maintenance.Removed);
        Assert.Contains(second, maintenance.Removed);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));

        // The staged version of a download is not an applier copy: the housekeeping leaves it to the next download.
        Assert.True(Directory.Exists(staged));
        Assert.DoesNotContain(staged, maintenance.Removed);
    }

    [Fact]
    public void A_claim_of_a_backup_is_not_applier_housekeeping()
    {
        // The claim of a restore carries the version, so it is neither an applier copy nor an .old/.failed tree: the
        // housekeeping must leave it for the startup repair (which puts it back under its .old name).
        using var root = new TempTree("forge-applier-maintain-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "current");
        var claimed = Path.Combine(
            Path.GetDirectoryName(install)!,
            UpdateApplyPaths.RestoringName(install, "1.2.0", Guid.NewGuid().ToString("N")));
        UpdateApplySandbox.WriteTree(claimed, "previous");

        var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);

        Assert.True(Directory.Exists(claimed));
        Assert.Empty(maintenance.Removed);
        Assert.Empty(maintenance.LeftBehind);
    }

    /// <summary>A throw-away directory tree with path helpers and a best-effort teardown.</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree(string prefix) => Root = TestTempFiles.NewDirectory(prefix);

        public string Root { get; }

        public string Combine(string name) => Path.Combine(Root, name);

        public void Dispose() => TestTempFiles.DeleteTree(Root);
    }
}
