namespace Forge.Workspace;

/// <summary>
/// The HEAD side of a checkout: which branch a directory has checked out, or whether it is not a repository at all.
/// Read for display (a tree row label), so every expected state comes back as a value and nothing is thrown.
/// </summary>
public sealed partial class GitCli
{
    /// <summary>
    /// What <paramref name="workingDirectory"/>'s HEAD points at. Never throws for a missing repository, a bare repository
    /// (which has no checkout at all), a detached HEAD or a repository without a commit; only cancellation escapes.
    /// </summary>
    public async Task<GitHead> GetHeadAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // A registered project's directory can be gone (or replaced by a file) by the time it is refreshed; asking git about
        // it would fail to even start the process, and this read reports state instead of throwing.
        if (!Directory.Exists(workingDirectory))
        {
            return GitHead.NotRepository;
        }

        // `--show-toplevel` succeeds only inside a work tree, so one call separates "there is a checkout here" from both
        // "not a repository" and "a bare repository": a bare one names a branch in HEAD but has no working tree, and a
        // caller that treated it as a checkout would write into the repository's own data directory.
        var topLevel = await RunAsync(workingDirectory, Args("rev-parse", "--show-toplevel"), ct).ConfigureAwait(false);
        if (!topLevel.Success)
        {
            return GitHead.NotRepository;
        }

        // symbolic-ref answers for an unborn branch too (a fresh `git init`), and fails for a detached HEAD.
        var symbolic = await RunAsync(workingDirectory, Args("symbolic-ref", "--quiet", "--short", "HEAD"), ct).ConfigureAwait(false);
        var branch = symbolic.Success ? symbolic.StandardOutput.Trim() : string.Empty;
        if (branch.Length == 0)
        {
            return GitHead.Detached;
        }

        var commit = await RevParseAsync(workingDirectory, "HEAD", ct).ConfigureAwait(false);
        return new GitHead(commit is null ? GitHeadKind.UnbornBranch : GitHeadKind.Branch, branch);
    }
}
