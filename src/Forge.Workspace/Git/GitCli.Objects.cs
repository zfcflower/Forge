using System.Text;

namespace Forge.Workspace;

/// <summary>One entry of a flat tree built with <see cref="GitCli.MakeTreeAsync"/>: a regular file (mode 100644) in the tree root.</summary>
public sealed record GitTreeEntry(string Name, string BlobId);

/// <summary>
/// Plumbing used to publish files outside any branch of the working repository: store a file as a blob, write a tree, make
/// a parentless commit, push a bare commit id to a ref, plus the helpers the screenshot step needs (a detached temporary
/// worktree, a pathspec-limited commit). Same rules as the rest of <see cref="GitCli"/>: arguments go through the argument
/// list, refs and ids are validated against option injection, expected remote failures are returned, not thrown.
/// </summary>
public sealed partial class GitCli
{
    private const string FallbackIdentityName = "Forge";
    private const string FallbackIdentityEmail = "forge@localhost";

    private static readonly string[] IdentityFailureMarkers =
    [
        "empty ident name",
        "unable to auto-detect email address",
        "Author identity unknown",
        "Committer identity unknown",
        "tell me who you are",
        "auto-detection is disabled",
    ];

    /// <summary>
    /// The repository-location variables <see cref="GitCli"/> never lets a child inherit; a command that is not git itself
    /// (the screenshot command, which may run git on its own) must not inherit them either, or it would see another repository.
    /// </summary>
    internal static IReadOnlyList<string> RepositoryEnvironmentVariables => InheritedRepositoryVariables;

    /// <summary>
    /// Stores the bytes of <paramref name="filePath"/> in the object database as a blob and returns its id. No filter
    /// (line-ending conversion, clean filters) is applied, so a binary file such as a PNG is stored exactly as it is.
    /// The file may live anywhere on disk; only the object store of <paramref name="workingDirectory"/>'s repository is written.
    /// </summary>
    public async Task<string> HashObjectAsync(string workingDirectory, string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var arguments = Args("hash-object", "-w", "--no-filters", "--", filePath);
        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return ParseObjectId(output, arguments);
    }

    /// <summary>
    /// Writes a tree object holding <paramref name="entries"/> as regular files in its root and returns the tree id. Names
    /// must be plain file names: no slash, no NUL, not <c>.</c>, <c>..</c> or <c>.git</c>, no duplicates.
    /// </summary>
    public async Task<string> MakeTreeAsync(string workingDirectory, IReadOnlyList<GitTreeEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var input = new StringBuilder(entries.Count * 80);
        foreach (var entry in entries)
        {
            if (!IsPlainTreeName(entry.Name))
            {
                throw new ArgumentException($"'{entry.Name}' is not a valid tree entry name.", nameof(entries));
            }

            if (!IsCommitHash(entry.BlobId))
            {
                throw new ArgumentException($"'{entry.BlobId}' is not an object id.", nameof(entries));
            }

            if (!names.Add(entry.Name))
            {
                throw new ArgumentException($"Tree entry '{entry.Name}' appears twice.", nameof(entries));
            }

            input.Append("100644 blob ").Append(entry.BlobId).Append('\t').Append(entry.Name).Append('\0');
        }

        var arguments = Args("mktree", "-z");
        var result = await RunCoreAsync(workingDirectory, arguments, new GitRunOptions(), null, ct, standardInput: input.ToString())
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        return ParseObjectId(result.StandardOutput, arguments);
    }

    /// <summary>
    /// Creates a commit object for <paramref name="tree"/> and returns its id; no ref is moved. Without
    /// <paramref name="parent"/> the commit is an orphan root. The message travels over stdin. When git has no identity to
    /// author the commit with (a machine without <c>user.name</c>/<c>user.email</c>) the commit is made as
    /// <c>Forge &lt;forge@localhost&gt;</c> instead of failing.
    /// </summary>
    public async Task<string> CommitTreeAsync(
        string workingDirectory,
        string tree,
        string message,
        string? parent = null,
        CancellationToken ct = default)
    {
        Reference(tree, nameof(tree));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var arguments = Args("commit-tree", tree);
        if (parent is not null)
        {
            arguments.Add("-p");
            arguments.Add(Reference(parent, nameof(parent)));
        }

        arguments.Add("-F");
        arguments.Add("-");

        var text = message.EndsWith('\n') ? message : message + "\n";
        var result = await RunCoreAsync(workingDirectory, arguments, new GitRunOptions(), null, ct, standardInput: text).ConfigureAwait(false);
        if (!result.Success && HasIdentityFailure(result.StandardError))
        {
            var identity = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_AUTHOR_NAME"] = FallbackIdentityName,
                ["GIT_AUTHOR_EMAIL"] = FallbackIdentityEmail,
                ["GIT_COMMITTER_NAME"] = FallbackIdentityName,
                ["GIT_COMMITTER_EMAIL"] = FallbackIdentityEmail,
            };
            result = await RunCoreAsync(workingDirectory, arguments, new GitRunOptions(), identity, ct, standardInput: text).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        return ParseObjectId(result.StandardOutput, arguments);
    }

    /// <summary>
    /// Pushes the commit <paramref name="commit"/> (an id) to <paramref name="destinationRef"/> (<c>refs/heads/...</c>) on
    /// <paramref name="remote"/>, always as a lease against <paramref name="expectedRemoteCommit"/>: the tip the caller just read with
    /// <see cref="LsRemoteAsync"/>, or the empty string when the ref must not exist yet. Used for refs that Forge owns and
    /// rewrites, such as the screenshot assets branch; there is no plain <c>--force</c>. A lease that no longer holds comes back as
    /// <see cref="GitRemoteFailureKind.NonFastForward"/> with nothing pushed.
    /// </summary>
    public async Task<GitRemoteResult> PushCommitAsync(
        string workingDirectory,
        string remote,
        string commit,
        string destinationRef,
        string expectedRemoteCommit,
        GitNetworkOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRemoteCommit);
        if (RejectOptionLike(remote, "remote name") is { } invalidRemote)
        {
            return invalidRemote;
        }

        if (!IsCommitHash(commit) || commit.Length < 40)
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, $"'{commit}' is not a full commit id.", string.Empty);
        }

        if (expectedRemoteCommit.Length > 0 && !IsCommitHash(expectedRemoteCommit))
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                $"ExpectedRemoteCommit '{expectedRemoteCommit}' is not a commit hash; read the remote tip with ls-remote first.",
                string.Empty);
        }

        if (!destinationRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            || destinationRef.Length == "refs/heads/".Length
            || !await IsValidRefNameAsync(workingDirectory, destinationRef, ct).ConfigureAwait(false))
        {
            return new GitRemoteResult(
                GitRemoteFailureKind.Other,
                $"'{destinationRef}' is not a valid branch ref (expected refs/heads/<name>).",
                string.Empty);
        }

        var arguments = Args(
            "push",
            "--force-with-lease=" + destinationRef + ":" + expectedRemoteCommit,
            remote,
            commit + ":" + destinationRef);
        var result = await RunNetworkAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        return result.Success
            ? GitRemoteResult.Ok($"Pushed {Shorten(commit)} to {remote} {destinationRef}.")
            : ToRemoteFailure(result, $"Could not push {Shorten(commit)} to {remote} {destinationRef}.");
    }

    /// <summary>
    /// Adds a worktree at <paramref name="worktreePath"/> that checks out <paramref name="commit"/> detached, so no branch is
    /// created or moved. Remove it with <see cref="RemoveWorktreeAsync"/> and <see cref="PruneWorktreesAsync"/>.
    /// </summary>
    public Task AddDetachedWorktreeAsync(string workingDirectory, string worktreePath, string commit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        Reference(commit, nameof(commit));
        return RunCheckedAsync(
            workingDirectory,
            Args("worktree", "add", "--detach", "--end-of-options", worktreePath, commit),
            ct);
    }

    /// <summary>
    /// The tracked files at or below <paramref name="paths"/> (repository-relative, forward slashes), read from the index.
    /// Paths are matched literally, so a name with glob characters or a leading <c>:</c> means itself.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListTrackedFilesAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return [];
        }

        var arguments = Args("--literal-pathspecs", "ls-files", "-z", "--");
        arguments.AddRange(paths);
        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Commits the working-tree state of exactly <paramref name="paths"/> (additions and deletions) and nothing else: anything
    /// else that is staged stays staged and out of the commit. Cancellation after git created the commit still returns it,
    /// like <see cref="CommitAsync"/>.
    /// </summary>
    public async Task<GitCommit> CommitPathsAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }

        var add = Args("--literal-pathspecs", "add", "-A", "--");
        add.AddRange(paths);
        await RunCheckedAsync(workingDirectory, add, ct).ConfigureAwait(false);

        var commit = Args("--literal-pathspecs", "commit", "-F", "-", "--only", "--");
        commit.AddRange(paths);
        var result = await RunCoreAsync(
                workingDirectory,
                commit,
                new GitRunOptions(),
                null,
                ct,
                standardInput: message,
                keepCompletedResult: true)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, commit);
        }

        return await GetCommitAsync(workingDirectory, "HEAD", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The value of one configuration key as the repository sees it (local, global and system), or null when it is not set.</summary>
    public async Task<string?> GetConfigValueAsync(string workingDirectory, string key, CancellationToken ct = default)
    {
        Reference(key, nameof(key));
        var result = await RunAsync(workingDirectory, Args("config", "--get", "--", key), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var value = result.StandardOutput.Trim();
        return value.Length == 0 ? null : value;
    }

    private async Task<bool> IsValidRefNameAsync(string workingDirectory, string reference, CancellationToken ct)
    {
        // check-ref-format parses its own options and does not know --end-of-options. The caller only passes names that start
        // with refs/heads/, and anything else is refused here, so the value can never be read as an option.
        if (reference.StartsWith('-'))
        {
            return false;
        }

        var result = await RunAsync(workingDirectory, Args("check-ref-format", reference), ct).ConfigureAwait(false);
        return result.Success;
    }

    private static string ParseObjectId(string output, IReadOnlyList<string> arguments)
    {
        var id = output.Trim();
        if (!IsCommitHash(id))
        {
            throw new GitException(
                $"git {Describe(arguments)} did not print an object id: '{Trim(output)}'.",
                arguments,
                0,
                string.Empty);
        }

        return id;
    }

    private static bool IsPlainTreeName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.IndexOfAny(['/', '\0', '\\', '\t', '\n']) < 0
        && name is not ("." or ".." or ".git");

    private static bool HasIdentityFailure(string standardError)
    {
        foreach (var marker in IdentityFailureMarkers)
        {
            if (standardError.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
