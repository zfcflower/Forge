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

        // One call answers what used to take three: is this a work tree (not a bare repository or a plain folder), and does
        // HEAD already point at a commit (an unborn branch does not). The tree refreshes this per worktree directory, where
        // the process start dominates the read, so the calls are the cost. A not-a-repository folder prints nothing; a bare
        // repository answers "false" then "true".
        var probe = await RunAsync(
                workingDirectory,
                Args("rev-parse", "--is-inside-work-tree", "--is-bare-repository", "--verify", "--quiet", "HEAD"),
                ct)
            .ConfigureAwait(false);
        var lines = new List<string>(3);
        using (var reader = TextLines.Split(probe.StandardOutput).GetEnumerator())
        {
            while (reader.MoveNext() && lines.Count < 3)
            {
                lines.Add(reader.Current);
            }
        }

        if (lines.Count < 2
            || !string.Equals(lines[0], "true", StringComparison.Ordinal)
            || string.Equals(lines[1], "true", StringComparison.Ordinal))
        {
            return GitHead.NotRepository;
        }

        var hasCommit = lines.Count >= 3 && lines[2].Trim().Length > 0;

        // symbolic-ref answers for an unborn branch too (a fresh `git init`), and fails for a detached HEAD.
        var symbolic = await RunAsync(workingDirectory, Args("symbolic-ref", "--quiet", "--short", "HEAD"), ct).ConfigureAwait(false);
        var branch = symbolic.Success ? symbolic.StandardOutput.Trim() : string.Empty;
        if (branch.Length == 0)
        {
            return GitHead.Detached;
        }

        return new GitHead(hasCommit ? GitHeadKind.Branch : GitHeadKind.UnbornBranch, branch);
    }

    /// <summary>
    /// The checkout's HEAD and the kind of operation in progress (merge / rebase / cherry-pick / revert), read in one
    /// <c>rev-parse</c> (the work-tree / bare / commit probe, the five <c>--git-path</c> files, and the commit itself) plus
    /// one <c>symbolic-ref</c>. The tree refresh asks this for every registered worktree directory, where process startup
    /// dominates the read, so this is the hot path's shape: the same answers <see cref="GetHeadAsync"/> and
    /// <see cref="GetConflictStateAsync"/> give, in half the processes. The conflicted-file list is not read here;
    /// <see cref="GetConflictStateAsync"/> asks for that when the operation needs aborting. A bare repository or a plain
    /// folder is <see cref="GitHead.NotRepository"/> with no operation.
    /// </summary>
    public async Task<(GitHead Head, GitOperationKind Operation)> GetCheckoutStateAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            return (GitHead.NotRepository, GitOperationKind.None);
        }

        var probe = await RunAsync(
                workingDirectory,
                Args(
                    "rev-parse",
                    "--path-format=absolute",
                    "--git-path", "rebase-merge",
                    "--git-path", "rebase-apply",
                    "--git-path", "MERGE_HEAD",
                    "--git-path", "CHERRY_PICK_HEAD",
                    "--git-path", "REVERT_HEAD",
                    "--is-inside-work-tree",
                    "--is-bare-repository",
                    "--verify",
                    "--quiet",
                    "HEAD"),
                ct)
            .ConfigureAwait(false);
        var lines = new List<string>(8);
        using (var reader = TextLines.Split(probe.StandardOutput).GetEnumerator())
        {
            while (reader.MoveNext() && lines.Count < 8)
            {
                lines.Add(reader.Current);
            }
        }

        // Five paths, then the two flags, then the HEAD commit when it exists: anything shorter is not a checkout (a plain
        // folder prints nothing, a bare repository prints the paths and "false"/"true").
        if (lines.Count < 7
            || !string.Equals(lines[5], "true", StringComparison.Ordinal)
            || string.Equals(lines[6], "true", StringComparison.Ordinal))
        {
            return (GitHead.NotRepository, GitOperationKind.None);
        }

        var operation = OperationOf([lines[0], lines[1], lines[2], lines[3], lines[4]]);
        var hasCommit = lines.Count > 7 && lines[7].Trim().Length > 0;
        var symbolic = await RunAsync(workingDirectory, Args("symbolic-ref", "--quiet", "--short", "HEAD"), ct).ConfigureAwait(false);
        var branch = symbolic.Success ? symbolic.StandardOutput.Trim() : string.Empty;
        var head = branch.Length == 0
            ? GitHead.Detached
            : new GitHead(hasCommit ? GitHeadKind.Branch : GitHeadKind.UnbornBranch, branch);
        return (head, operation);
    }
}
