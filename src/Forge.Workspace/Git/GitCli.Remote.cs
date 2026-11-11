using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// Branch and remote operations. Every network call (fetch, pull, push, ls-remote) runs with prompts disabled and a
/// bounded timeout, so a missing credential fails fast as <see cref="GitRemoteFailureKind.AuthenticationFailed"/>
/// instead of hanging: on top of the base environment (<c>GIT_TERMINAL_PROMPT=0</c>) these calls set
/// <c>GCM_INTERACTIVE=never</c>, and <c>GIT_SSH_COMMAND=ssh -o BatchMode=yes</c> unless the user already configured
/// <c>GIT_SSH_COMMAND</c> or <c>core.sshCommand</c>. Refs and branch names are validated against option injection like
/// the rest of this class (leading <c>-</c> rejected, <c>--end-of-options</c> where git accepts it).
/// Expected failures come back as <see cref="GitRemoteResult"/>; only cancellation throws.
/// </summary>
public sealed partial class GitCli
{
    private const string DefaultRemoteName = "origin";
    private const string FetchSuffix = " (fetch)";
    private const string PushSuffix = " (push)";
    private const int MaxPathsInMessage = 10;
    private const int MaxCommitHashLength = 64;
    private const int MinCommitHashLength = 4;
    private const int ShortCommitLength = 7;

    /// <summary>Timeout for fetch, pull, push and ls-remote unless a call overrides it.</summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Current branch, HEAD, upstream and ahead/behind in one read; never touches the network.</summary>
    public async Task<GitBranchStatus> GetBranchStatusAsync(string workingDirectory, CancellationToken ct = default)
    {
        var arguments = Args("status", "--porcelain=v2", "--branch", "-z", "--untracked-files=no");
        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return ParseBranchStatus(output);
    }

    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string workingDirectory, CancellationToken ct = default)
    {
        var output = await RunCheckedAsync(workingDirectory, Args("remote", "-v"), ct).ConfigureAwait(false);

        var fetches = new Dictionary<string, string>(StringComparer.Ordinal);
        var pushes = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in TextLines.Split(output))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var name = line[..tab];
            var rest = line[(tab + 1)..];
            if (rest.EndsWith(FetchSuffix, StringComparison.Ordinal))
            {
                if (fetches.TryAdd(name, rest[..^FetchSuffix.Length]))
                {
                    order.Add(name);
                }
            }
            else if (rest.EndsWith(PushSuffix, StringComparison.Ordinal))
            {
                pushes[name] = rest[..^PushSuffix.Length];
            }
        }

        var remotes = new GitRemote[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            var name = order[i];
            remotes[i] = new GitRemote(name, fetches[name], pushes.TryGetValue(name, out var push) ? push : null);
        }

        return remotes;
    }

    /// <summary>True when <paramref name="name"/> is a valid new branch name (<c>git check-ref-format --branch</c>).</summary>
    public async Task<bool> IsValidBranchNameAsync(string workingDirectory, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] == '-')
        {
            return false;
        }

        var result = await RunAsync(workingDirectory, Args("check-ref-format", "--branch", name), ct).ConfigureAwait(false);
        return result.Success && result.StandardOutput.Trim().Length > 0;
    }

    /// <summary>
    /// Creates <paramref name="name"/> at <paramref name="startPoint"/> (HEAD when null), optionally switching to it.
    /// A blank or invalid name, or one that already exists, is a failure result with
    /// <see cref="GitRemoteFailureKind.Other"/> - not an exception - because it is typed input from the user or the
    /// model; only cancellation throws. A <paramref name="startPoint"/> that does not resolve is likewise a failure.
    /// </summary>
    public async Task<GitRemoteResult> CreateBranchAsync(
        string workingDirectory,
        string name,
        string? startPoint = null,
        bool switchTo = false,
        CancellationToken ct = default)
    {
        if (!await IsValidBranchNameAsync(workingDirectory, name, ct).ConfigureAwait(false))
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                $"'{name}' is not a valid branch name; see git check-ref-format.",
                string.Empty);
        }

        var start = string.IsNullOrWhiteSpace(startPoint) ? null : startPoint;
        if (start is not null && RejectOptionLike(start, "start point") is { } invalidStart)
        {
            return invalidStart;
        }

        List<string> arguments;
        if (switchTo)
        {
            arguments = Args("switch", "-c", name);
        }
        else
        {
            arguments = Args("branch", "--end-of-options", name);
        }

        if (start is not null)
        {
            if (switchTo)
            {
                arguments.Add("--end-of-options");
            }

            arguments.Add(start);
        }

        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToRemoteFailure(result, $"Could not create branch {name}.");
        }

        var at = start ?? "HEAD";
        return GitRemoteResult.Ok(switchTo
            ? $"Created branch {name} at {at} and switched to it."
            : $"Created branch {name} at {at}.");
    }

    /// <summary>
    /// Switches to an existing local branch, or creates a tracking branch from <c>origin/&lt;name&gt;</c> when only the
    /// remote one exists. Refuses with <see cref="GitRemoteFailureKind.DirtyWorkTree"/> when tracked files have
    /// uncommitted changes, rather than letting git carry them across.
    /// </summary>
    public async Task<GitRemoteResult> SwitchBranchAsync(string workingDirectory, string name, CancellationToken ct = default)
    {
        if (RejectOptionLike(name, "branch name") is { } invalid)
        {
            return invalid;
        }

        var changes = await GetStatusAsync(workingDirectory, includeUntracked: false, ct).ConfigureAwait(false);
        if (changes.Count > 0)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.DirtyWorkTree,
                $"The working tree has uncommitted changes in {changes.Count.ToString(CultureInfo.InvariantCulture)} tracked " +
                $"file(s); commit or stash them before switching to {name}.",
                string.Empty);
        }

        if (await RevParseAsync(workingDirectory, "refs/heads/" + name, ct).ConfigureAwait(false) is not null)
        {
            var result = await RunAsync(workingDirectory, Args("switch", "--end-of-options", name), ct).ConfigureAwait(false);
            return result.Success
                ? GitRemoteResult.Ok($"Switched to branch {name}.")
                : ToRemoteFailure(result, $"Could not switch to branch {name}.");
        }

        var tracking = DefaultRemoteName + "/" + name;
        if (await RevParseAsync(workingDirectory, "refs/remotes/" + tracking, ct).ConfigureAwait(false) is not null)
        {
            var result = await RunAsync(
                workingDirectory,
                Args("switch", "--track", "-c", name, "--end-of-options", tracking),
                ct).ConfigureAwait(false);
            return result.Success
                ? GitRemoteResult.Ok($"Created branch {name} tracking {tracking} and switched to it.")
                : ToRemoteFailure(result, $"Could not create a tracking branch for {tracking}.");
        }

        return new GitRemoteResult(
            GitRemoteFailureKind.Other,
            $"Branch {name} does not exist locally or as {tracking}; create it with git_branch create or fetch first.",
            string.Empty);
    }

    /// <summary>Sets the upstream of <paramref name="branch"/> (current branch when null) to <paramref name="upstream"/>, e.g. <c>origin/feature</c>.</summary>
    public async Task<GitRemoteResult> SetUpstreamAsync(string workingDirectory, string? branch, string upstream, CancellationToken ct = default)
    {
        if (RejectOptionLike(upstream, "upstream") is { } invalidUpstream)
        {
            return invalidUpstream;
        }

        var target = string.IsNullOrWhiteSpace(branch) ? null : branch;
        if (target is not null && RejectOptionLike(target, "branch name") is { } invalidBranch)
        {
            return invalidBranch;
        }

        var arguments = Args("branch", "--set-upstream-to=" + upstream);
        if (target is not null)
        {
            arguments.Add("--end-of-options");
            arguments.Add(target);
        }

        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToRemoteFailure(result, $"Could not set the upstream of {target ?? "the current branch"} to {upstream}.");
        }

        return GitRemoteResult.Ok(target is null
            ? $"The current branch now tracks {upstream}."
            : $"Branch {target} now tracks {upstream}.");
    }

    /// <summary>
    /// Fetches <paramref name="remote"/> (all remotes when null), pruning deleted remote branches. A remote that is not
    /// configured, does not exist, or cannot be reached is <see cref="GitRemoteFailureKind.RemoteNotFound"/>.
    /// </summary>
    public async Task<GitRemoteResult> FetchAsync(
        string workingDirectory,
        string? remote = null,
        GitNetworkOptions? options = null,
        CancellationToken ct = default)
    {
        var named = string.IsNullOrWhiteSpace(remote) ? null : remote;
        if (named is not null && RejectOptionLike(named, "remote name") is { } invalid)
        {
            return invalid;
        }

        var arguments = Args("fetch", "--prune");
        if (named is null)
        {
            arguments.Add("--all");
        }
        else
        {
            arguments.Add(named);
        }

        var result = await RunNetworkAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToRemoteFailure(result, named is null ? "Could not fetch any remote." : $"Could not fetch {named}.");
        }

        return GitRemoteResult.Ok(named is null
            ? "Fetched every remote and pruned deleted branches."
            : $"Fetched {named} and pruned deleted branches.");
    }

    /// <summary>
    /// Pulls the upstream of the current branch with the given strategy. <see cref="GitPullStrategy.FastForwardOnly"/>
    /// fails with <see cref="GitRemoteFailureKind.NonFastForward"/> when the branches diverged; rebase and merge stop on
    /// conflicts with <see cref="GitRemoteFailureKind.Conflict"/> and leave the operation in progress.
    /// </summary>
    public async Task<GitRemoteResult> PullAsync(
        string workingDirectory,
        GitPullStrategy strategy,
        GitNetworkOptions? options = null,
        CancellationToken ct = default)
    {
        var status = await GetBranchStatusAsync(workingDirectory, ct).ConfigureAwait(false);
        if (status.Branch is null)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                "HEAD is detached; check out a branch before pulling.",
                string.Empty);
        }

        if (status.Upstream is null)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.NoUpstream,
                $"Branch {status.Branch} has no upstream; set one with git_branch set_upstream, or push it with set_upstream.",
                string.Empty);
        }

        if (status.UpstreamGone)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.NoUpstream,
                $"The upstream {status.Upstream} of {status.Branch} no longer exists on the remote; push the branch again " +
                "with set_upstream, or set a different upstream.",
                string.Empty);
        }

        var arguments = strategy switch
        {
            GitPullStrategy.Rebase => Args("pull", "--rebase"),
            GitPullStrategy.Merge => Args("pull", "--no-rebase", "--no-edit"),

            // --no-rebase as well, so a configured pull.rebase cannot turn the default into a rebase.
            _ => Args("pull", "--no-rebase", "--ff-only"),
        };

        var result = await RunNetworkAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            var failure = await ToRemoteFailureAsync(
                workingDirectory,
                result,
                $"Could not pull {status.Upstream} into {status.Branch} ({StrategyName(strategy)}).",
                ct).ConfigureAwait(false);
            if (failure.Failure == GitRemoteFailureKind.NonFastForward && strategy == GitPullStrategy.FastForwardOnly)
            {
                return new GitRemoteResult(
                    GitRemoteFailureKind.NonFastForward,
                    $"Branch {status.Branch} has diverged from {status.Upstream}; use git_sync rebase, git_sync merge, or " +
                    "change the pull strategy in settings.",
                    failure.StandardError);
            }

            return failure;
        }

        // A rebase pull with nothing to replay says "Current branch X is up to date." rather than "Already up to date.",
        // which the fast-forward and merge strategies print; both spellings mean the upstream was not integrated.
        var output = result.StandardOutput;
        var skipped = output.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
            || output.Contains("is up to date", StringComparison.OrdinalIgnoreCase);
        if (skipped)
        {
            return GitRemoteResult.Ok($"Branch {status.Branch} was already up to date with {status.Upstream}.");
        }

        return GitRemoteResult.Ok(strategy switch
        {
            GitPullStrategy.Rebase => $"Rebased {status.Branch} onto {status.Upstream}.",
            GitPullStrategy.Merge => $"Merged {status.Upstream} into {status.Branch}.",
            _ => $"Fast-forwarded {status.Branch} to {status.Upstream}.",
        });
    }

    /// <summary>
    /// Pushes a branch. A force push is only ever a lease against a commit the caller just read with
    /// <see cref="LsRemoteAsync"/>; there is no plain <c>--force</c>. With <see cref="GitPushOptions.SourceCommit"/> the
    /// refspec names that commit instead of the local branch, so the pushed content cannot drift from what the caller
    /// checked.
    /// </summary>
    public async Task<GitRemoteResult> PushAsync(
        string workingDirectory,
        GitPushOptions push,
        GitNetworkOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(push);

        var branch = string.IsNullOrWhiteSpace(push.Branch)
            ? await GetCurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false)
            : push.Branch;
        if (branch is null)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                "HEAD is detached; check out a branch before pushing.",
                string.Empty);
        }

        if (RejectOptionLike(branch, "branch name") is { } invalidBranch)
        {
            return invalidBranch;
        }

        var remote = await ResolvePushRemoteAsync(workingDirectory, branch, push.Remote, ct).ConfigureAwait(false);
        if (RejectOptionLike(remote, "remote name") is { } invalidRemote)
        {
            return invalidRemote;
        }

        string? expected = null;
        if (push.ForceWithLease && push.ExpectedRemoteCommit is { } observed)
        {
            if (observed.Length > 0 && !IsCommitHash(observed))
            {
                return new GitRemoteResult(
                    GitRemoteFailureKind.Other,
                    $"ExpectedRemoteCommit '{observed}' is not a commit hash; read the remote tip with ls-remote first.",
                    string.Empty);
            }

            expected = observed;
        }

        string? pinned = null;
        if (push.SourceCommit is { } source)
        {
            if (!IsCommitHash(source))
            {
                return new GitRemoteResult(
                    GitRemoteFailureKind.Other,
                    $"SourceCommit '{source}' is not a commit hash; resolve HEAD with rev-parse first.",
                    string.Empty);
            }

            pinned = source;
        }

        var arguments = Args("push");

        // -u needs a local source branch, so a pinned commit has its upstream written after the push instead.
        if (push.SetUpstream && pinned is null)
        {
            arguments.Add("-u");
        }

        if (expected is not null)
        {
            // An empty expected commit means "the remote ref must not exist yet".
            arguments.Add("--force-with-lease=refs/heads/" + branch + ":" + expected);
        }

        arguments.Add(remote);
        arguments.Add((pinned ?? "refs/heads/" + branch) + ":refs/heads/" + branch);

        var result = await RunNetworkAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToRemoteFailure(result, $"Could not push {branch} to {remote}.");
        }

        var report = result.StandardError + result.StandardOutput;
        if (expected is not null
            && report.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase)
            && !await LeaseHoldsAsync(workingDirectory, branch, expected, ct).ConfigureAwait(false))
        {
            // git skips the lease when there is nothing to update, so the lease has to be checked here.
            return new GitRemoteResult(
                GitRemoteFailureKind.NonFastForward,
                $"Nothing was pushed, but the lease on {remote}/{branch} no longer holds; read the remote tip with " +
                "ls-remote and retry.",
                result.StandardError.Trim());
        }
        var details = new List<string>(3);
        if (report.Contains("[new branch]", StringComparison.Ordinal))
        {
            details.Add("new branch");
        }

        if (push.SetUpstream)
        {
            // A pinned push cannot use -u: git only records an upstream for a pushed local branch ref. Writing the two
            // configuration keys git would have written keeps the branch tracking its remote counterpart.
            if (pinned is null || await WriteUpstreamAsync(workingDirectory, branch, remote, ct).ConfigureAwait(false))
            {
                details.Add("upstream set");
            }
        }

        if (expected is not null)
        {
            details.Add(expected.Length == 0 ? "lease: the branch must not exist" : "lease " + Shorten(expected));
        }

        return GitRemoteResult.Ok(details.Count == 0
            ? $"Pushed {branch} to {remote}."
            : $"Pushed {branch} to {remote} ({string.Join(", ", details)}).");
    }

    /// <summary>
    /// Writes what <c>git push -u</c> writes for a branch: <c>branch.&lt;name&gt;.remote</c> and
    /// <c>branch.&lt;name&gt;.merge</c>. Used when the push is pinned to a commit instead of the branch ref. A failure only
    /// means the branch does not track its remote branch yet, so it is reported, never raised.
    /// </summary>
    private async Task<bool> WriteUpstreamAsync(string workingDirectory, string branch, string remote, CancellationToken ct)
    {
        var prefix = "branch." + branch + ".";
        var keys = new (string Key, string Value)[]
        {
            (prefix + "remote", remote),
            (prefix + "merge", "refs/heads/" + branch),
        };

        foreach (var (key, value) in keys)
        {
            var result = await RunCoreAsync(
                workingDirectory,
                Args("config", key, value),
                new GitRunOptions(),
                null,
                ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the current tip of <c>refs/heads/&lt;branch&gt;</c> on <paramref name="remote"/> from the network. The ref being
    /// absent is a successful read with <see cref="GitRemoteRef.Commit"/> null; a remote that is not configured or does
    /// not exist is <see cref="GitRemoteFailureKind.RemoteNotFound"/>.
    /// </summary>
    public async Task<GitRemoteRef> LsRemoteAsync(
        string workingDirectory,
        string remote,
        string branch,
        GitNetworkOptions? options = null,
        CancellationToken ct = default)
    {
        var invalid = RejectOptionLike(remote, "remote name") ?? RejectOptionLike(branch, "branch name");
        if (invalid is { } failure)
        {
            return new GitRemoteRef(failure, null);
        }

        var reference = "refs/heads/" + branch;
        var result = await RunNetworkAsync(
            workingDirectory,
            Args("ls-remote", "--exit-code", remote, reference),
            options,
            ct).ConfigureAwait(false);

        // --exit-code reports "no matching refs" as exit code 2, which is a successful empty answer.
        if (!result.TimedOut && result.ExitCode == 2)
        {
            return new GitRemoteRef(GitRemoteResult.Ok($"{branch} does not exist on {remote}."), null);
        }

        if (!result.Success)
        {
            return new GitRemoteRef(ToRemoteFailure(result, $"Could not read {branch} from {remote}."), null);
        }

        foreach (var line in TextLines.Split(result.StandardOutput))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var sha = line[..tab].Trim();
            if (sha.Length > 0)
            {
                return new GitRemoteRef(GitRemoteResult.Ok($"{branch} on {remote} is at {Shorten(sha)}."), sha);
            }
        }

        return new GitRemoteRef(GitRemoteResult.Ok($"{branch} does not exist on {remote}."), null);
    }

    /// <summary>Rebases the current branch onto <paramref name="upstream"/>; stops with <see cref="GitRemoteFailureKind.Conflict"/> on conflicts.</summary>
    public async Task<GitRemoteResult> RebaseAsync(string workingDirectory, string upstream, CancellationToken ct = default)
    {
        if (RejectOptionLike(upstream, "upstream") is { } invalid)
        {
            return invalid;
        }

        var changes = await GetStatusAsync(workingDirectory, includeUntracked: false, ct).ConfigureAwait(false);
        if (changes.Count > 0)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.DirtyWorkTree,
                $"The working tree has uncommitted changes in {changes.Count.ToString(CultureInfo.InvariantCulture)} tracked " +
                $"file(s); commit or stash them before rebasing onto {upstream}.",
                string.Empty);
        }

        var result = await RunAsync(workingDirectory, Args("rebase", "--end-of-options", upstream), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return await ToRemoteFailureAsync(workingDirectory, result, $"Could not rebase onto {upstream}.", ct).ConfigureAwait(false);
        }

        var branch = await GetCurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false) ?? "the current branch";
        var skipped = result.StandardOutput.Contains("is up to date", StringComparison.OrdinalIgnoreCase);
        return GitRemoteResult.Ok(skipped
            ? $"{branch} was already up to date with {upstream}."
            : $"Rebased {branch} onto {upstream}.");
    }

    /// <summary>Merges <paramref name="revision"/> into the current branch (no fast-forward forcing); stops on conflicts.</summary>
    public Task<GitRemoteResult> MergeAsync(string workingDirectory, string revision, CancellationToken ct = default) =>
        MergeAsync(workingDirectory, revision, noFastForward: false, ct);

    /// <summary>
    /// Merges <paramref name="revision"/> into the current branch. With <paramref name="noFastForward"/> a merge commit is
    /// always created (<c>--no-ff</c>). The target tree need not be clean: when git refuses to overwrite local changes the
    /// failure carries git's own file list in <see cref="GitRemoteResult.StandardError"/>.
    /// </summary>
    public async Task<GitRemoteResult> MergeAsync(string workingDirectory, string revision, bool noFastForward, CancellationToken ct = default)
    {
        if (RejectOptionLike(revision, "revision") is { } invalid)
        {
            return invalid;
        }

        var mergeArguments = noFastForward
            ? Args("merge", "--no-ff", "--no-edit", "--end-of-options", revision)
            : Args("merge", "--no-edit", "--end-of-options", revision);
        var result = await RunAsync(workingDirectory, mergeArguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return await ToRemoteFailureAsync(workingDirectory, result, $"Could not merge {revision}.", ct).ConfigureAwait(false);
        }

        var skipped = result.StandardOutput.Contains("Already up to date", StringComparison.OrdinalIgnoreCase);
        if (skipped)
        {
            return GitRemoteResult.Ok($"Already up to date with {revision}.");
        }

        var branch = await GetCurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false) ?? "the current branch";
        return GitRemoteResult.Ok($"Merged {revision} into {branch}.");
    }

    /// <summary>Which operation (rebase, merge, cherry-pick, revert) is in progress and which paths are unmerged.</summary>
    public async Task<GitConflictState> GetConflictStateAsync(string workingDirectory, CancellationToken ct = default)
    {
        // --git-path, not a hard-coded .git: in a linked worktree these paths live in the worktree's own git dir.
        var arguments = Args(
            "rev-parse",
            "--path-format=absolute",
            "--git-path", "rebase-merge",
            "--git-path", "rebase-apply",
            "--git-path", "MERGE_HEAD",
            "--git-path", "CHERRY_PICK_HEAD",
            "--git-path", "REVERT_HEAD");
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return new GitConflictState(GitOperationKind.None, []);
        }

        var paths = new List<string>(5);
        foreach (var line in TextLines.Split(result.StandardOutput))
        {
            var path = line.Trim();
            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }

        var operation = OperationOf(paths);
        if (operation == GitOperationKind.None)
        {
            return new GitConflictState(GitOperationKind.None, []);
        }

        var unmerged = await RunAsync(
            workingDirectory,
            Args("diff", "--name-only", "--diff-filter=U", "-z"),
            ct).ConfigureAwait(false);
        var conflicted = unmerged.Success
            ? unmerged.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : [];

        return new GitConflictState(operation, conflicted);
    }

    /// <summary>Aborts the in-progress operation; succeeds with a message when nothing is in progress.</summary>
    public async Task<GitRemoteResult> AbortOperationAsync(string workingDirectory, CancellationToken ct = default)
    {
        var state = await GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false);
        var command = CommandOf(state.Operation);
        if (command is null)
        {
            return GitRemoteResult.Ok("No rebase, merge, cherry-pick or revert is in progress; nothing to abort.");
        }

        var result = await RunAsync(workingDirectory, Args(command, "--abort"), ct).ConfigureAwait(false);
        return result.Success
            ? GitRemoteResult.Ok($"Aborted the in-progress {OperationName(state.Operation)}.")
            : ToRemoteFailure(result, $"Could not abort the in-progress {OperationName(state.Operation)}.");
    }

    /// <summary>
    /// Continues the in-progress rebase or merge after conflicts were resolved and staged. Runs with
    /// <c>GIT_EDITOR=true</c> so git never opens an editor; reports <see cref="GitRemoteFailureKind.Conflict"/> when
    /// paths are still unmerged or the next rebase step conflicts.
    /// </summary>
    public async Task<GitRemoteResult> ContinueOperationAsync(string workingDirectory, CancellationToken ct = default)
    {
        var state = await GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false);
        var command = CommandOf(state.Operation);
        if (command is null)
        {
            return GitRemoteResult.Ok("No rebase, merge, cherry-pick or revert is in progress; nothing to continue.");
        }

        if (state.ConflictedPaths.Count > 0)
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Conflict,
                ConflictMessage(
                    $"The {OperationName(state.Operation)} cannot continue yet.",
                    state),
                string.Empty);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_EDITOR"] = "true",
            ["GIT_SEQUENCE_EDITOR"] = "true",
        };
        var result = await RunCoreAsync(
            workingDirectory,
            Args(command, "--continue"),
            new GitRunOptions(),
            environment,
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return await ToRemoteFailureAsync(
                workingDirectory,
                result,
                $"Could not continue the {OperationName(state.Operation)}.",
                ct).ConfigureAwait(false);
        }

        return GitRemoteResult.Ok(state.Operation switch
        {
            GitOperationKind.Merge => "Completed the merge.",
            GitOperationKind.CherryPick => "Continued the cherry-pick to completion.",
            GitOperationKind.Revert => "Continued the revert to completion.",
            _ => "Continued the rebase to completion.",
        });
    }

    /// <summary>
    /// The remote a push of <paramref name="branch"/> goes to: the requested name, else
    /// <c>branch.&lt;branch&gt;.pushRemote</c>, else <c>branch.&lt;branch&gt;.remote</c>, else <c>origin</c>.
    /// </summary>
    internal async Task<string> ResolvePushRemoteAsync(
        string workingDirectory,
        string branch,
        string? requested,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        foreach (var key in new[] { "branch." + branch + ".pushRemote", "branch." + branch + ".remote" })
        {
            var result = await RunCoreAsync(
                workingDirectory,
                Args("config", "--get", key),
                new GitRunOptions(),
                null,
                ct).ConfigureAwait(false);
            var value = result.Success ? result.StandardOutput.Trim() : string.Empty;

            // "." means "the local repository tracks locally"; it is not a push destination.
            if (value.Length > 0 && value != ".")
            {
                return value;
            }
        }

        return DefaultRemoteName;
    }

    /// <summary>
    /// True when the local branch still matches what the lease expects; the empty expectation only holds when the branch
    /// has no commit at all, which a local branch that exists never satisfies.
    /// </summary>
    private async Task<bool> LeaseHoldsAsync(string workingDirectory, string branch, string expected, CancellationToken ct)
    {
        if (expected.Length == 0)
        {
            return false;
        }

        var localHead = await RevParseAsync(workingDirectory, "refs/heads/" + branch, ct).ConfigureAwait(false);
        if (localHead is null)
        {
            return false;
        }

        return string.Equals(expected, localHead, StringComparison.OrdinalIgnoreCase)
            || (await RevParseAsync(workingDirectory, expected, ct).ConfigureAwait(false) is { } resolved
                && string.Equals(resolved, localHead, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs a network command with prompts disabled, an optional SSH batch mode and the network timeout.</summary>
    private async Task<GitCommandResult> RunNetworkAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitNetworkOptions? options,
        CancellationToken ct)
    {
        var environment = await BuildNetworkEnvironmentAsync(workingDirectory, ct).ConfigureAwait(false);
        var runOptions = new GitRunOptions { Timeout = options?.Timeout ?? NetworkTimeout };
        return await RunCoreAsync(workingDirectory, arguments, runOptions, environment, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> BuildNetworkEnvironmentAsync(string workingDirectory, CancellationToken ct)
    {
        // GIT_TERMINAL_PROMPT=0 is already part of the base environment; repeated here so the network contract is complete.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
        {
            return environment;
        }

        var configured = await RunCoreAsync(
            workingDirectory,
            Args("config", "--get", "core.sshCommand"),
            new GitRunOptions(),
            null,
            ct).ConfigureAwait(false);
        if (configured.Success && !string.IsNullOrWhiteSpace(configured.StandardOutput))
        {
            return environment;
        }

        environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        return environment;
    }

    /// <summary>
    /// Classifies a failed operation and, when the repository is left mid-operation with unmerged paths, reports it as
    /// <see cref="GitRemoteFailureKind.Conflict"/> with the file list. The repository decides, not the text: git merge
    /// prints its conflict report on stdout, and a rebase reports the next conflicting step differently again.
    /// </summary>
    private async Task<GitRemoteResult> ToRemoteFailureAsync(
        string workingDirectory,
        GitCommandResult result,
        string message,
        CancellationToken ct)
    {
        var kind = GitFailureClassifier.Classify(result);
        if (kind is not (GitRemoteFailureKind.Conflict or GitRemoteFailureKind.Other))
        {
            return ToRemoteFailure(result, message);
        }

        var state = await GetConflictStateAsync(workingDirectory, ct).ConfigureAwait(false);
        if (state.ConflictedPaths.Count == 0 && !(state.InProgress && kind == GitRemoteFailureKind.Conflict))
        {
            return ToRemoteFailure(result, message);
        }

        return new GitRemoteResult(
            GitRemoteFailureKind.Conflict,
            ConflictMessage(message, state),
            result.StandardError.Trim());
    }

    private static GitRemoteResult ToRemoteFailure(GitCommandResult result, string message)
    {
        var kind = GitFailureClassifier.Classify(result);
        var detail = Trim(result.StandardError);
        return new GitRemoteResult(
            kind,
            detail.Length == 0 ? message : message + " " + detail,
            result.StandardError.Trim());
    }

    private static string ConflictMessage(string message, GitConflictState state)
    {
        var text = new StringBuilder(message);
        if (state.InProgress)
        {
            text.Append(" The ").Append(OperationName(state.Operation)).Append(" is still in progress.");
        }

        if (state.ConflictedPaths.Count > 0)
        {
            text.Append(" Conflicted files (").Append(state.ConflictedPaths.Count.ToString(CultureInfo.InvariantCulture)).Append("): ");
            var listed = Math.Min(state.ConflictedPaths.Count, MaxPathsInMessage);
            for (var i = 0; i < listed; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(state.ConflictedPaths[i]);
            }

            if (state.ConflictedPaths.Count > listed)
            {
                text.Append(" and ").Append((state.ConflictedPaths.Count - listed).ToString(CultureInfo.InvariantCulture)).Append(" more");
            }

            text.Append('.');
        }

        return text.Append(" Resolve them, stage the result, then continue or abort.").ToString();
    }

    /// <summary>Rejects a value git would parse as an option; returns the failure to hand back, or null when it is usable.</summary>
    private static GitRemoteResult? RejectOptionLike(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, $"A {kind} is required.", string.Empty);
        }

        if (value[0] == '-')
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                $"'{value}' is not a valid {kind}: a leading '-' would be read as a git option.",
                string.Empty);
        }

        return null;
    }

    private static bool IsCommitHash(string value)
    {
        if (value.Length is < MinCommitHashLength or > MaxCommitHashLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string Shorten(string commit) =>
        commit.Length <= ShortCommitLength ? commit : commit[..ShortCommitLength];

    private static string StrategyName(GitPullStrategy strategy) => strategy switch
    {
        GitPullStrategy.Rebase => "rebase",
        GitPullStrategy.Merge => "merge",
        _ => "fast-forward only",
    };

    private static string? CommandOf(GitOperationKind operation) => operation switch
    {
        GitOperationKind.Rebase => "rebase",
        GitOperationKind.Merge => "merge",
        GitOperationKind.CherryPick => "cherry-pick",
        GitOperationKind.Revert => "revert",
        _ => null,
    };

    internal static string OperationName(GitOperationKind operation) => operation switch
    {
        GitOperationKind.Rebase => "rebase",
        GitOperationKind.Merge => "merge",
        GitOperationKind.CherryPick => "cherry-pick",
        GitOperationKind.Revert => "revert",
        _ => "operation",
    };

    private static GitOperationKind OperationOf(List<string> paths) =>
        Exists(paths, 0) || Exists(paths, 1) ? GitOperationKind.Rebase
        : Exists(paths, 2) ? GitOperationKind.Merge
        : Exists(paths, 3) ? GitOperationKind.CherryPick
        : Exists(paths, 4) ? GitOperationKind.Revert
        : GitOperationKind.None;

    private static bool Exists(List<string> paths, int index) =>
        index < paths.Count && (File.Exists(paths[index]) || Directory.Exists(paths[index]));

    /// <summary>
    /// Reads the <c>--porcelain=v2 --branch</c> header records. Every record is NUL-terminated, including the headers.
    /// </summary>
    private static GitBranchStatus ParseBranchStatus(string porcelain)
    {
        const string OidHeader = "branch.oid ";
        const string HeadHeader = "branch.head ";
        const string UpstreamHeader = "branch.upstream ";
        const string AheadBehindHeader = "branch.ab ";
        const string DetachedHead = "(detached)";
        const string InitialCommit = "(initial)";

        string? branch = null;
        string? head = null;
        string? upstream = null;
        int? ahead = null;
        int? behind = null;
        var detached = false;
        var hasAheadBehind = false;

        foreach (var record in porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length < 3 || record[0] != '#' || record[1] != ' ')
            {
                continue;
            }

            var header = record.AsSpan(2);
            if (header.StartsWith(OidHeader, StringComparison.Ordinal))
            {
                var value = header[OidHeader.Length..];
                if (!value.SequenceEqual(InitialCommit))
                {
                    head = value.ToString();
                }
            }
            else if (header.StartsWith(HeadHeader, StringComparison.Ordinal))
            {
                var value = header[HeadHeader.Length..];
                if (value.SequenceEqual(DetachedHead))
                {
                    detached = true;
                }
                else
                {
                    branch = value.ToString();
                }
            }
            else if (header.StartsWith(UpstreamHeader, StringComparison.Ordinal))
            {
                upstream = header[UpstreamHeader.Length..].ToString();
            }
            else if (header.StartsWith(AheadBehindHeader, StringComparison.Ordinal))
            {
                var value = header[AheadBehindHeader.Length..];
                var space = value.IndexOf(' ');
                if (space > 1)
                {
                    if (TryReadCount(value[1..space], out var aheadCount))
                    {
                        ahead = aheadCount;
                    }

                    var behindPart = value[(space + 1)..];
                    if (behindPart.Length > 1 && TryReadCount(behindPart[1..], out var behindCount))
                    {
                        behind = behindCount;
                    }
                }

                hasAheadBehind = true;
            }
        }

        return new GitBranchStatus(branch, head, upstream, ahead, behind, detached, upstream is not null && !hasAheadBehind);
    }

    private static bool TryReadCount(ReadOnlySpan<char> value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count);
}

/// <summary>Maps a failed git invocation to a <see cref="GitRemoteFailureKind"/>; pure, so it is unit-tested on captured stderr.</summary>
public static class GitFailureClassifier
{
    // git runs under LC_ALL=C, so these are its stable English messages. Ordered: the first category that matches wins,
    // because a rejected push can mention several of them at once.
    private static readonly string[] AuthenticationMarkers =
    [
        "Authentication failed",
        "could not read Username",
        "Permission denied (publickey",
        "terminal prompts disabled",
        "invalid credentials",
    ];

    private static readonly string[] RemoteNotFoundMarkers =
    [
        "does not appear to be a git repository",
        "Repository not found",
        "No such remote",
        "requested repository not found",
    ];

    private static readonly string[] RemoteContextMarkers = ["remote", "repository"];

    private static readonly string[] NonFastForwardMarkers =
    [
        "non-fast-forward",
        "[rejected]",
        "stale info",
        "fetch first",
        "Not possible to fast-forward",
        "Diverging branches",
    ];

    private static readonly string[] ConflictMarkers = ["CONFLICT", "could not apply", "Merge conflict", "fix conflicts"];

    private static readonly string[] NoUpstreamMarkers = ["no tracking information", "has no upstream"];

    private static readonly string[] DirtyWorkTreeMarkers =
    [
        "would be overwritten",
        "Please commit your changes or stash them",
        "You have unstaged changes",
        "cannot rebase: You have unstaged",
    ];

    private static readonly string[] NetworkMarkers =
    [
        "Could not resolve host",
        "Connection refused",
        "Connection timed out",
        "unable to access",
        "SSL",
        "Network is unreachable",
        "early EOF",
    ];

    public static GitRemoteFailureKind Classify(GitCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TimedOut)
        {
            return GitRemoteFailureKind.TimedOut;
        }

        if (result.Success)
        {
            return GitRemoteFailureKind.None;
        }

        var error = result.StandardError;
        if (ContainsAny(error, AuthenticationMarkers) || ContainsHttpStatus(error, "403"))
        {
            return GitRemoteFailureKind.AuthenticationFailed;
        }

        if (ContainsAny(error, RemoteNotFoundMarkers)
            || (ContainsAny(error, RemoteContextMarkers) && error.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return GitRemoteFailureKind.RemoteNotFound;
        }

        if (ContainsAny(error, NonFastForwardMarkers))
        {
            return GitRemoteFailureKind.NonFastForward;
        }

        if (ContainsAny(error, ConflictMarkers))
        {
            return GitRemoteFailureKind.Conflict;
        }

        if (ContainsAny(error, NoUpstreamMarkers))
        {
            return GitRemoteFailureKind.NoUpstream;
        }

        if (ContainsAny(error, DirtyWorkTreeMarkers))
        {
            return GitRemoteFailureKind.DirtyWorkTree;
        }

        if (ContainsAny(error, NetworkMarkers))
        {
            return GitRemoteFailureKind.Network;
        }

        return GitRemoteFailureKind.Other;
    }

    /// <summary>
    /// True when <paramref name="status"/> appears as a standalone HTTP status. The bare digits also occur inside commit
    /// hashes, temporary directory names and ports, so a match surrounded by other hex digits is not a status code.
    /// </summary>
    private static bool ContainsHttpStatus(string text, string status)
    {
        var start = 0;
        while (start < text.Length)
        {
            var found = text.IndexOf(status, start, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            var before = found == 0 || !char.IsAsciiHexDigit(text[found - 1]);
            var after = found + status.Length >= text.Length || !char.IsAsciiHexDigit(text[found + status.Length]);
            if (before && after)
            {
                return true;
            }

            start = found + 1;
        }

        return false;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
