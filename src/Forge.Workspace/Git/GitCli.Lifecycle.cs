namespace Forge.Workspace;

/// <summary>
/// Git reads and writes the session/worktree lifecycle needs: merge abort and progress, ancestry, branch rename, the
/// upstream remote of a branch (config only, no network) and sparse-checkout configuration. Same rules as the rest of
/// <see cref="GitCli"/>: refs go through <c>Reference</c> against option injection and arguments are never shell-joined.
/// </summary>
public sealed partial class GitCli
{
    /// <summary>Aborts an in-progress merge (<c>git merge --abort</c>); throws <see cref="GitException"/> when none is running.</summary>
    public async Task AbortMergeAsync(string workingDirectory, CancellationToken ct = default) =>
        await RunCheckedAsync(workingDirectory, Args("merge", "--abort"), ct).ConfigureAwait(false);

    /// <summary>True while the checkout has an unfinished merge (<c>MERGE_HEAD</c> exists in its own git dir).</summary>
    public async Task<bool> HasMergeInProgressAsync(string workingDirectory, CancellationToken ct = default) =>
        (await GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false)).Operation == GitOperationKind.Merge;

    /// <summary>True while the checkout has an unfinished rebase.</summary>
    public async Task<bool> HasRebaseInProgressAsync(string workingDirectory, CancellationToken ct = default) =>
        (await GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false)).Operation == GitOperationKind.Rebase;

    /// <summary>Commits in a range such as <c>base..branch</c>, newest first, merges included.</summary>
    public Task<IReadOnlyList<GitCommit>> ListCommitsAsync(string workingDirectory, string range, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        return GetLogAsync(workingDirectory, new GitLogOptions { Revision = range }, ct);
    }

    /// <summary>
    /// Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> (<c>merge-base --is-ancestor</c>).
    /// Exit code 1 is the answer "no"; any other failure (unknown revision, not a repository) throws.
    /// </summary>
    public async Task<bool> IsAncestorAsync(string workingDirectory, string ancestor, string descendant, CancellationToken ct = default)
    {
        Reference(ancestor, nameof(ancestor));
        Reference(descendant, nameof(descendant));
        var arguments = Args("merge-base", "--is-ancestor", "--end-of-options", ancestor, descendant);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (result.Success)
        {
            return true;
        }

        if (!result.TimedOut && result.ExitCode == 1)
        {
            return false;
        }

        throw Failure(result, arguments);
    }

    /// <summary>Renames a local branch (<c>git branch -m</c>). An existing target name or an unknown source throws.</summary>
    public async Task RenameBranchAsync(string workingDirectory, string oldName, string newName, CancellationToken ct = default)
    {
        Reference(oldName, nameof(oldName));
        Reference(newName, nameof(newName));
        await RunCheckedAsync(workingDirectory, Args("branch", "-m", "--end-of-options", oldName, newName), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The remote a branch is configured to track (<c>branch.&lt;name&gt;.remote</c>), or null when it has none. Reads
    /// configuration only; there is no network access.
    /// </summary>
    public async Task<string?> GetUpstreamRemoteAsync(string workingDirectory, string branch, CancellationToken ct = default)
    {
        Reference(branch, nameof(branch));
        var value = await ReadConfigAsync(workingDirectory, "branch." + branch + ".remote", ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The sparse-checkout configuration of the checkout at <paramref name="path"/>, or null when it is not sparse
    /// (<c>core.sparseCheckout</c> unset or false). Only then is <c>git sparse-checkout list</c> consulted.
    /// </summary>
    public async Task<SparseCheckout?> GetSparseConfigAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var enabled = await ReadConfigAsync(path, "core.sparseCheckout", ct).ConfigureAwait(false);
        if (!string.Equals(enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var output = await RunCheckedAsync(path, Args("sparse-checkout", "list"), ct).ConfigureAwait(false);
        var directories = new List<string>();
        foreach (var line in TextLines.Split(output))
        {
            var entry = line.Trim();
            if (entry.Length > 0)
            {
                directories.Add(entry);
            }
        }

        return new SparseCheckout(directories);
    }

    /// <summary>Top-level directories of a revision (<c>git ls-tree -d --name-only</c>), in git's order.</summary>
    public async Task<IReadOnlyList<string>> ListTopLevelDirectoriesAsync(string workingDirectory, string revision, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        var output = await RunCheckedAsync(
            workingDirectory,
            Args("ls-tree", "-d", "--name-only", "-z", "--end-of-options", revision),
            ct).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// <c>git sparse-checkout set --cone</c> in a worktree. Git turns on <c>extensions.worktreeConfig</c> and keeps the
    /// pattern state in that worktree's own config, so other checkouts are unaffected.
    /// </summary>
    internal async Task SetSparseCheckoutAsync(string worktreePath, IReadOnlyList<string> directories, CancellationToken ct)
    {
        if (directories.Count == 0)
        {
            throw new ArgumentException("A sparse checkout needs at least one directory.", nameof(directories));
        }

        var arguments = Args("sparse-checkout", "set", "--cone", "--");
        foreach (var directory in directories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            arguments.Add(directory);
        }

        await RunCheckedAsync(worktreePath, arguments, ct).ConfigureAwait(false);
    }

    /// <summary>Plain <c>git checkout</c>: populates the working tree of a worktree added with <c>--no-checkout</c>.</summary>
    internal async Task CheckoutAsync(string worktreePath, CancellationToken ct) =>
        await RunCheckedAsync(worktreePath, Args("checkout"), ct).ConfigureAwait(false);

    private async Task<string?> ReadConfigAsync(string workingDirectory, string key, CancellationToken ct)
    {
        var arguments = Args("config", "--get", key);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (result.Success)
        {
            return result.StandardOutput.Trim();
        }

        // Exit 1 = the key is not set; anything else (not a repository, timeout) is a real failure.
        if (!result.TimedOut && result.ExitCode == 1)
        {
            return null;
        }

        throw Failure(result, arguments);
    }
}
