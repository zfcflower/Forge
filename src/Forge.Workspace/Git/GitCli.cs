using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// Async wrapper over the system <c>git</c> executable. Arguments are passed as an argument list and never
/// through a shell; the executable is resolved from PATH unless configured, and the child environment is
/// pinned to a C locale with terminal prompts disabled so output stays parseable. Captured output is raw
/// UTF-8 bytes decoded as they arrive, so CRLF, a missing trailing newline and control characters inside a
/// line survive unchanged.
/// </summary>
public sealed partial class GitCli
{
    private const string FallbackExecutable = "git";
    private const int ErrorCaptureCharacters = 64 * 1024;
    private const int ByteBufferSize = 16 * 1024;
    private const int MaxCallbackLineCharacters = 1024 * 1024;
    private const int MaxStreamedRecordCharacters = 8 * 1024 * 1024;

    private static readonly TimeSpan PumpGracePeriod = TimeSpan.FromSeconds(5);

    private static readonly string[] ConfigPrefix =
    [
        "-c", "core.quotepath=false",
        "-c", "color.ui=false",
        "-c", "diff.noprefix=false",
        "-c", "diff.mnemonicPrefix=false",
        "-c", "diff.renames=true",
    ];

    private static readonly TimeSpan InfiniteTimeout = Timeout.InfiniteTimeSpan;

    public GitCli(string? executablePath = null)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? FallbackExecutable : executablePath;
    }

    public string ExecutablePath { get; }

    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxCapturedCharacters { get; init; } = 8 * 1024 * 1024;

    public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
        RunAsync(workingDirectory, arguments, new GitRunOptions(), ct);

    /// <summary>
    /// Runs git to completion. Cancellation kills the process tree and throws
    /// <see cref="OperationCanceledException"/>; a timeout kills the tree and returns a result with
    /// <see cref="GitCommandResult.TimedOut"/> set. Captured output is capped, so very large results are cut
    /// off with a marker and <see cref="GitCommandResult.IsTruncated"/> set instead of exhausting memory.
    /// </summary>
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await RunCoreAsync(workingDirectory, arguments, options, null, ct).ConfigureAwait(false);
    }

    /// <param name="standardInput">
    /// Text written to the child's stdin as UTF-8 without a byte order mark, after which stdin is closed; null leaves stdin
    /// alone (inherited, as for every other command).
    /// </param>
    /// <param name="keepCompletedResult">
    /// A cancellation that lands after git has already exited with code 0 returns that result instead of throwing, because
    /// the command's effect (a commit) exists and the caller has to learn about it. A cancellation that kills a running
    /// git still throws, and so does a timeout.
    /// </param>
    private async Task<GitCommandResult> RunCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitRunOptions options,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct,
        string? standardInput = null,
        bool keepCompletedResult = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);

        var captureLimit = options.MaxCapturedCharacters ?? MaxCapturedCharacters;
        var timeout = options.Timeout ?? DefaultTimeout;

        using var process = Start(workingDirectory, arguments, environment, redirectInput: standardInput is not null);
        using var timeoutSource = CreateTimeoutSource(ct, timeout);
        using var killOnCancel = timeoutSource.Token.Register(static state => TryKill((Process)state!), process);

        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, options.OnStandardOutputLine, captureLimit, keepTail: false, options.CarriageReturnEndsLine, timeoutSource.Token);

        // git writes diagnostics to stderr with the decisive line last (a hook's verdict, the rejecting rule), so stderr
        // keeps its tail: a hook that prints more than the cap must not lose the line that says why it refused.
        var stderrTask = PumpAsync(process.StandardError.BaseStream, options.OnStandardErrorLine, Math.Min(captureLimit, ErrorCaptureCharacters), keepTail: true, options.CarriageReturnEndsLine, timeoutSource.Token);
        var stdinTask = standardInput is null ? Task.CompletedTask : WriteStandardInputAsync(process, standardInput, timeoutSource.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
        }

        var stdout = await AwaitPumpAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await AwaitPumpAsync(stderrTask).ConfigureAwait(false);
        await AwaitInputAsync(stdinTask).ConfigureAwait(false);

        var exitCode = timedOut ? -1 : ExitCodeOf(process);
        if (!(keepCompletedResult && !timedOut && exitCode == 0))
        {
            ct.ThrowIfCancellationRequested();
        }

        return new GitCommandResult(exitCode, stdout.Text, stderr.Text, timedOut) { IsTruncated = stdout.Truncated };
    }

    /// <summary>
    /// Waits for the stdin writer after git has exited. The writer ends by itself once the pipe breaks; the grace period
    /// only covers a stray grandchild that kept the read end open, which must not hang the call.
    /// </summary>
    private static async Task AwaitInputAsync(Task input)
    {
        try
        {
            await input.WaitAsync(PumpGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>
    /// Feeds <paramref name="text"/> to git and closes stdin. Runs next to the output pumps, so a git that writes while it
    /// reads cannot deadlock. A git that exits or is killed before it has read everything breaks the pipe; that is not an
    /// error here, because the exit code and the cancellation already say what happened.
    /// </summary>
    private static async Task WriteStandardInputAsync(Process process, string text, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var stream = process.StandardInput.BaseStream;
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// Streams stdout line by line without buffering the whole output, for commands such as <c>log</c> over a
    /// very large tree. Lines are split on LF only, so a lone CR stays inside the line; a line longer than the
    /// line buffer is reported cut off. The process is killed if the consumer stops early, the call is
    /// cancelled, or the timeout elapses; a failing exit code throws after the last line.
    /// </summary>
    public async IAsyncEnumerable<string> StreamLinesAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        options ??= new GitRunOptions();

        var timeout = options.Timeout ?? DefaultTimeout;
        using var process = Start(workingDirectory, arguments);
        using var timeoutSource = CreateTimeoutSource(ct, timeout);
        using var killOnCancel = timeoutSource.Token.Register(static state => TryKill((Process)state!), process);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, options.OnStandardErrorLine, ErrorCaptureCharacters, keepTail: true, carriageReturnEndsLine: false, timeoutSource.Token);

        try
        {
            await foreach (var line in ReadLinesAsync(process.StandardOutput.BaseStream, timeoutSource.Token).ConfigureAwait(false))
            {
                yield return line;
            }

            await FinishStreamAsync(process, arguments, timeout, ct, timeoutSource.Token, stderrTask).ConfigureAwait(false);
        }
        finally
        {
            TryKill(process);
        }
    }

    /// <summary>
    /// Streams stdout split on a separator (typically NUL for git's <c>-z</c> output). Records are split on the
    /// separator itself, so embedded newlines stay inside a record and a trailing separator produces no empty
    /// record.
    /// </summary>
    public async IAsyncEnumerable<string> StreamRecordsAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        char separator,
        GitRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        options ??= new GitRunOptions();

        var timeout = options.Timeout ?? DefaultTimeout;
        using var process = Start(workingDirectory, arguments);
        using var timeoutSource = CreateTimeoutSource(ct, timeout);
        using var killOnCancel = timeoutSource.Token.Register(static state => TryKill((Process)state!), process);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, options.OnStandardErrorLine, ErrorCaptureCharacters, keepTail: true, carriageReturnEndsLine: false, timeoutSource.Token);

        try
        {
            await foreach (var record in ReadRecordsAsync(process.StandardOutput.BaseStream, separator, timeoutSource.Token).ConfigureAwait(false))
            {
                yield return record;
            }

            await FinishStreamAsync(process, arguments, timeout, ct, timeoutSource.Token, stderrTask).ConfigureAwait(false);
        }
        finally
        {
            TryKill(process);
        }
    }

    public async Task<bool> IsRepositoryAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory, Args("rev-parse", "--git-dir"), ct).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>Absolute path of the working tree root, or null when the directory is not inside a repository.</summary>
    public async Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory, Args("rev-parse", "--show-toplevel"), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var path = result.StandardOutput.Trim();
        return path.Length == 0 ? null : WorkspacePaths.Normalize(path);
    }

    public Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string workingDirectory, CancellationToken ct = default) =>
        GetStatusAsync(workingDirectory, includeUntracked: true, ct);

    public async Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string workingDirectory, bool includeUntracked, CancellationToken ct = default)
    {
        var arguments = Args(
            "status",
            "--porcelain=v2",
            "-z",
            includeUntracked ? "--untracked-files=all" : "--untracked-files=no",
            "--no-ahead-behind");

        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return GitStatusParser.Parse(output);
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string workingDirectory, bool includeRemote = false, CancellationToken ct = default)
    {
        var arguments = Args(
            "for-each-ref",
            "--sort=-committerdate",
            "--format=%(refname:short)%09%(refname)%09%(objectname)%09%(HEAD)%09%(upstream:short)%09%(committerdate:iso-strict)",
            "refs/heads");
        if (includeRemote)
        {
            arguments.Add("refs/remotes");
        }

        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        var branches = new List<GitBranch>();
        foreach (var line in TextLines.Split(output))
        {
            var fields = line.Split('\t');
            if (fields.Length < 6 || fields[0].Length == 0 || fields[1].Length == 0)
            {
                continue;
            }

            DateTimeOffset? committedAt = null;
            if (DateTimeOffset.TryParse(fields[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                committedAt = parsed;
            }

            branches.Add(new GitBranch(
                fields[0],
                fields[2],
                fields[3] == "*",
                fields[4].Length == 0 ? null : fields[4],
                committedAt)
            {
                RefName = fields[1],
                IsRemote = fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal),
            });
        }

        return branches;
    }

    /// <summary>Current branch name, or null when HEAD is detached or the directory is not a repository.</summary>
    public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory, Args("rev-parse", "--abbrev-ref", "HEAD"), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var name = result.StandardOutput.Trim();
        return name.Length == 0 || name == "HEAD" ? null : name;
    }

    /// <summary>
    /// Local <c>main</c>/<c>master</c>/<c>trunk</c> or the remote default branch; null when none is known. A
    /// resolvable remote-tracking ref such as <c>origin/main</c> is returned as is, because the local branch of
    /// the same name may be absent or stale.
    /// </summary>
    public async Task<string?> GetDefaultBranchAsync(string workingDirectory, CancellationToken ct = default)
    {
        var symbolic = await RunAsync(workingDirectory, Args("symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"), ct).ConfigureAwait(false);
        if (symbolic.Success)
        {
            var reference = symbolic.StandardOutput.Trim();
            if (reference.Length > 0)
            {
                if (await RevParseAsync(workingDirectory, reference, ct).ConfigureAwait(false) is not null)
                {
                    return reference;
                }

                var slash = reference.IndexOf('/');
                var name = slash >= 0 ? reference[(slash + 1)..] : reference;
                if (name.Length > 0 && await RevParseAsync(workingDirectory, name, ct).ConfigureAwait(false) is not null)
                {
                    return name;
                }
            }
        }

        var branches = await GetBranchesAsync(workingDirectory, ct: ct).ConfigureAwait(false);
        foreach (var candidate in new[] { "main", "master", "trunk" })
        {
            foreach (var branch in branches)
            {
                if (string.Equals(branch.Name, candidate, StringComparison.Ordinal))
                {
                    return branch.Name;
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string workingDirectory, GitLogOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GitLogOptions();
        var arguments = Args("log", "--no-color", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1e");
        if (options.Limit > 0)
        {
            arguments.Add($"--max-count={options.Limit.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.Skip > 0)
        {
            arguments.Add($"--skip={options.Skip.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.FirstParent)
        {
            arguments.Add("--first-parent");
        }

        if (!options.IncludeMerges)
        {
            arguments.Add("--no-merges");
        }

        if (options.AllReferences)
        {
            arguments.Add("--all");
        }

        if (!string.IsNullOrWhiteSpace(options.Revision))
        {
            arguments.Add("--end-of-options");
            arguments.Add(Reference(options.Revision, nameof(GitLogOptions.Revision)));
        }

        if (options.Paths.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(options.Paths);
        }

        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.TrimStart('\n').Split('\x1f', 7);
            if (fields.Length < 7)
            {
                continue;
            }

            commits.Add(new GitCommit(
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : default,
                fields[5],
                fields[6].TrimEnd('\n')));
        }

        return commits;
    }

    public async Task<GitCommit> GetCommitAsync(string workingDirectory, string revision, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        var commits = await GetLogAsync(workingDirectory, new GitLogOptions { Revision = revision, Limit = 1 }, ct).ConfigureAwait(false);
        if (commits.Count == 0)
        {
            throw new GitException($"Revision '{revision}' has no commits.", Args("log", revision), 1, string.Empty);
        }

        return commits[0];
    }

    /// <summary>
    /// Stages the given paths (or everything when empty) and commits them with one message. The message travels over
    /// stdin (<c>commit -F -</c>), not as a <c>-m</c> argument, so its length is not bound by the operating system's
    /// command line limit (about 32,000 characters on Windows). Once git has created the commit the call no longer
    /// honours cancellation: the new commit is read back and returned even if <paramref name="ct"/> was cancelled in the
    /// meantime, so the caller never loses the id of a commit that exists. The read-back is bounded by
    /// <see cref="DefaultTimeout"/> and fails with <see cref="GitException"/> like any other command.
    /// </summary>
    public async Task<GitCommit> CommitAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(paths);

        var add = Args("add", "-A");
        if (paths.Count > 0)
        {
            add.Add("--");
            add.AddRange(paths);
        }

        await RunCheckedAsync(workingDirectory, add, ct).ConfigureAwait(false);

        var commit = Args("commit", "-F", "-");
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

        // The commit exists from here on; reading it back must not be undone by a cancellation that arrives now.
        return await GetCommitAsync(workingDirectory, "HEAD", CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<string> GetDiffTextAsync(string workingDirectory, GitDiffOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GitDiffOptions();
        var arguments = BuildDiffArguments(options);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<FileDiff>> GetDiffAsync(string workingDirectory, GitDiffOptions? options = null, CancellationToken ct = default)
    {
        options ??= new GitDiffOptions();
        var text = await GetDiffTextAsync(workingDirectory, options, ct).ConfigureAwait(false);
        var files = new List<FileDiff>(UnifiedDiffParser.Parse(text));
        if (options.IncludeUntracked)
        {
            await AppendUntrackedAsync(workingDirectory, options, files, ct).ConfigureAwait(false);
        }

        return files;
    }

    /// <summary>
    /// Writes the whole working tree (tracked and untracked files, honouring the repository's ignore rules) as a tree
    /// object and returns its hash. The index git uses is a temporary file seeded with a copy of the real index, so the
    /// snapshot never touches the user's index, HEAD, branch, refs or stash; the temporary file is always deleted.
    /// Two snapshots of an unchanged tree produce the same hash.
    /// </summary>
    public async Task<string> SnapshotTreeAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var root = await GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false)
            ?? throw new GitException(
                $"'{workingDirectory}' is not inside a git repository.",
                Args("rev-parse", "--show-toplevel"),
                1,
                string.Empty);

        var index = await ResolveIndexPathAsync(root, ct).ConfigureAwait(false);
        var temporaryIndex = Path.Combine(
            Path.GetTempPath(),
            "forge-snapshot-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("n") + ".index");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = temporaryIndex,

            // Optional locks are refreshes of the real index (and rlimits); a snapshot must not take them.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        try
        {
            if (index is not null && File.Exists(index))
            {
                File.Copy(index, temporaryIndex, overwrite: true);
            }

            await RunCheckedAsync(root, Args("add", "--all"), environment, ct).ConfigureAwait(false);
            var output = await RunCheckedAsync(root, Args("write-tree"), environment, ct).ConfigureAwait(false);
            var tree = output.Trim();
            if (tree.Length == 0)
            {
                throw new GitException("git write-tree returned no tree hash.", Args("write-tree"), 0, string.Empty);
            }

            return tree;
        }
        finally
        {
            DeleteQuietly(temporaryIndex);
            DeleteQuietly(temporaryIndex + ".lock");
        }
    }

    /// <summary>The unified diff between two tree hashes, as git prints it.</summary>
    public Task<string> GetTreeDiffTextAsync(
        string workingDirectory,
        string fromTree,
        string toTree,
        CancellationToken ct = default) =>
        GetTreeDiffTextCoreAsync(workingDirectory, fromTree, toTree, 3, ct);

    private async Task<string> GetTreeDiffTextCoreAsync(
        string workingDirectory,
        string fromTree,
        string toTree,
        int contextLines,
        CancellationToken ct)
    {
        var arguments = Args(
            "diff",
            "--no-color",
            "--no-ext-diff",
            "--unified=" + contextLines.ToString(CultureInfo.InvariantCulture),
            "--find-renames",
            "--end-of-options",
            Reference(fromTree, nameof(fromTree)),
            Reference(toTree, nameof(toTree)));
        return await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
    }

    /// <summary>The diff between two tree hashes, parsed into files, hunks and numbered lines.</summary>
    public async Task<IReadOnlyList<FileDiff>> GetTreeDiffAsync(
        string workingDirectory,
        string fromTree,
        string toTree,
        CancellationToken ct = default)
    {
        var text = await GetTreeDiffTextAsync(workingDirectory, fromTree, toTree, ct).ConfigureAwait(false);
        return UnifiedDiffParser.Parse(text);
    }

    /// <summary>
    /// The name/status list (<c>A</c>/<c>M</c>/<c>D</c>/<c>R</c>/<c>T</c>) between two tree hashes, with the old path of a
    /// rename in <see cref="GitStatusEntry.OriginalPath"/>. Both sides are trees, so the worktree status is unmodified.
    /// </summary>
    public async Task<IReadOnlyList<GitStatusEntry>> GetTreeChangesAsync(
        string workingDirectory,
        string fromTree,
        string toTree,
        CancellationToken ct = default)
    {
        var arguments = Args(
            "diff",
            "--name-status",
            "-z",
            "--no-ext-diff",
            "--find-renames",
            "--end-of-options",
            Reference(fromTree, nameof(fromTree)),
            Reference(toTree, nameof(toTree)));
        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return GitStatusParser.ParseNameStatus(output);
    }

    private async Task<string?> ResolveIndexPathAsync(string workingDirectory, CancellationToken ct)
    {
        var result = await RunAsync(
            workingDirectory,
            Args("rev-parse", "--path-format=absolute", "--git-path", "index"),
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var path = result.StandardOutput.Trim();
        return path.Length == 0 ? null : path;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<string?> GetMergeBaseAsync(string workingDirectory, string first, string second, CancellationToken ct = default)
    {
        Reference(first, nameof(first));
        Reference(second, nameof(second));
        var arguments = Args("merge-base", "--end-of-options", first, second);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        var sha = result.StandardOutput.Trim();
        return sha.Length == 0 ? null : sha;
    }

    /// <summary>Content of <paramref name="path"/> at <paramref name="revision"/>, decoded as UTF-8; null when absent.</summary>
    public async Task<string?> GetFileAtRevisionAsync(string workingDirectory, string revision, string path, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var arguments = Args("show", "--end-of-options", $"{revision}:{path}");
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        return result.StandardOutput;
    }

    public async Task<string?> RevParseAsync(string workingDirectory, string revision, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        var arguments = Args("rev-parse", "--verify", "--quiet", "--end-of-options", revision);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        var sha = result.StandardOutput.Trim();
        return sha.Length == 0 ? null : sha;
    }

    /// <summary>Commits reachable from <paramref name="revision"/> but not from <paramref name="excludeRevision"/>.</summary>
    public async Task<int> CountCommitsAheadAsync(string workingDirectory, string revision, string excludeRevision, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        Reference(excludeRevision, nameof(excludeRevision));
        var arguments = Args("rev-list", "--count", "--end-of-options", revision, "^" + excludeRevision);
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        return int.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>True when <paramref name="revision"/> is reachable from some branch, remote-tracking branch or tag.</summary>
    public async Task<bool> IsCommitReferencedAsync(string workingDirectory, string revision, CancellationToken ct = default)
    {
        Reference(revision, nameof(revision));
        var arguments = Args("for-each-ref", "--count=1", "--contains", revision, "--format=%(refname)");
        var result = await RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        return result.StandardOutput.Trim().Length > 0;
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string workingDirectory, CancellationToken ct = default)
    {
        var output = await RunCheckedAsync(workingDirectory, Args("worktree", "list", "--porcelain"), ct).ConfigureAwait(false);
        return GitWorktreeParser.Parse(output);
    }

    /// <summary>
    /// Creates a worktree on a new branch (<c>git worktree add -b</c>). <paramref name="noCheckout"/> leaves the worktree
    /// empty (<c>--no-checkout</c>) so a sparse checkout can be configured before the first checkout.
    /// </summary>
    public async Task<GitWorktree> AddWorktreeAsync(
        string workingDirectory,
        string worktreePath,
        string branch,
        string? startPoint = null,
        CancellationToken ct = default,
        bool noCheckout = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        Reference(branch, nameof(branch));
        var arguments = noCheckout
            ? Args("worktree", "add", "--no-checkout", "-b", branch, "--end-of-options", worktreePath)
            : Args("worktree", "add", "-b", branch, "--end-of-options", worktreePath);

        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            arguments.Add(Reference(startPoint, nameof(startPoint)));
        }

        await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        return await FindWorktreeAsync(workingDirectory, worktreePath, ct).ConfigureAwait(false);
    }

    /// <summary>Adds a worktree that checks out an existing branch.</summary>
    public async Task<GitWorktree> AttachWorktreeAsync(string workingDirectory, string worktreePath, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        Reference(branch, nameof(branch));
        await RunCheckedAsync(workingDirectory, Args("worktree", "add", "--end-of-options", worktreePath, branch), ct).ConfigureAwait(false);
        return await FindWorktreeAsync(workingDirectory, worktreePath, ct).ConfigureAwait(false);
    }

    public async Task RemoveWorktreeAsync(string workingDirectory, string worktreePath, bool force = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var arguments = Args("worktree", "remove");
        if (force)
        {
            arguments.Add("--force");
        }

        arguments.Add("--end-of-options");
        arguments.Add(worktreePath);
        await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
    }

    public Task PruneWorktreesAsync(string workingDirectory, CancellationToken ct = default) =>
        RunCheckedAsync(workingDirectory, Args("worktree", "prune"), ct);

    /// <summary>Removes a branch; callers decide whether that is safe.</summary>
    public async Task DeleteBranchAsync(string workingDirectory, string branch, bool force = false, CancellationToken ct = default)
    {
        Reference(branch, nameof(branch));
        await RunCheckedAsync(workingDirectory, Args("branch", force ? "-D" : "-d", "--end-of-options", branch), ct).ConfigureAwait(false);
    }

    private async Task<GitWorktree> FindWorktreeAsync(string workingDirectory, string worktreePath, CancellationToken ct)
    {
        var target = WorkspacePaths.NormalizeCandidate(Path.IsPathRooted(worktreePath)
            ? worktreePath
            : Path.Combine(workingDirectory, worktreePath));
        foreach (var worktree in await ListWorktreesAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            if (WorkspacePaths.Equals(worktree.Path, target))
            {
                return worktree;
            }
        }

        return new GitWorktree(target, null, null, false, false, null, false, null, false);
    }

    private async Task AppendUntrackedAsync(string workingDirectory, GitDiffOptions options, List<FileDiff> files, CancellationToken ct)
    {
        var arguments = Args("ls-files", "--others", "--exclude-standard", "-z");
        if (options.Paths.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(options.Paths);
        }

        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var diff = await FileDiffFactory.FromUntrackedFileAsync(workingDirectory, record, options.MaxUntrackedBytes, ct).ConfigureAwait(false);
            if (diff is not null)
            {
                files.Add(diff);
            }
        }
    }

    private static List<string> BuildDiffArguments(GitDiffOptions options)
    {
        var arguments = Args("diff", "--no-color", "--no-ext-diff");
        if (options.Staged)
        {
            arguments.Add("--cached");
        }

        arguments.Add($"--unified={options.ContextLines.ToString(CultureInfo.InvariantCulture)}");
        arguments.Add(options.DetectRenames ? "--find-renames" : "--no-renames");

        var from = string.IsNullOrWhiteSpace(options.From) ? null : Reference(options.From, nameof(GitDiffOptions.From));
        var to = string.IsNullOrWhiteSpace(options.To) ? null : Reference(options.To, nameof(GitDiffOptions.To));
        arguments.Add("--end-of-options");
        if (from is not null)
        {
            arguments.Add(from);
        }

        if (to is not null)
        {
            arguments.Add(to);
        }

        if (options.Paths.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(options.Paths);
        }

        return arguments;
    }

    private async Task<string> RunCheckedAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        await RunCheckedAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);

    private async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        var result = await RunCoreAsync(workingDirectory, arguments, new GitRunOptions(), environment, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw Failure(result, arguments);
        }

        if (result.IsTruncated)
        {
            throw Truncated(result, arguments);
        }

        return result.StandardOutput;
    }

    private static GitException Failure(GitCommandResult result, IReadOnlyList<string> arguments)
    {
        var message = result.TimedOut
            ? $"git {Describe(arguments)} timed out."
            : $"git {Describe(arguments)} failed with exit code {result.ExitCode}: {Trim(result.StandardError)}";
        return new GitException(message, arguments, result.ExitCode, result.StandardError);
    }

    private static GitOutputTruncatedException Truncated(GitCommandResult result, IReadOnlyList<string> arguments) =>
        new(
            $"git {Describe(arguments)} produced more output than the capture limit, so the result is incomplete.",
            arguments,
            result.ExitCode,
            result.StandardError);

    /// <summary>Rejects a revision, range or ref that git would otherwise parse as an option.</summary>
    private static string Reference(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value[0] == '-')
        {
            throw new ArgumentException(
                $"'{value}' is not a valid revision or ref: a leading '-' would be parsed as a git option.",
                parameterName);
        }

        return value;
    }

    private static CancellationTokenSource CreateTimeoutSource(CancellationToken ct, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout >= TimeSpan.Zero && timeout != InfiniteTimeout)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }

    /// <summary>
    /// Repository-location and discovery variables never inherited from the parent process. Each one would make
    /// git use a repository, index, object store or discovery boundary other than the one the working directory
    /// names: GIT_DIR, GIT_WORK_TREE, GIT_COMMON_DIR (repository and worktree location), GIT_INDEX_FILE (index),
    /// GIT_OBJECT_DIRECTORY and GIT_ALTERNATE_OBJECT_DIRECTORIES (object store), GIT_NAMESPACE (ref namespace),
    /// GIT_PREFIX (set by git for aliases and hooks, shifts relative paths), GIT_CEILING_DIRECTORIES and
    /// GIT_DISCOVERY_ACROSS_FILESYSTEM (where upward discovery stops). A caller that needs one passes it
    /// explicitly through the environment argument, which is applied afterwards.
    /// </summary>
    private static readonly string[] InheritedRepositoryVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_PREFIX",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    ];

    private Process Start(string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null, bool redirectInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in InheritedRepositoryVariables)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LANGUAGE"] = "C";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["TERM"] = "dumb";

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new GitException($"Could not start '{ExecutablePath}'.", arguments, -1, string.Empty);
            }
        }
        catch (Exception exception) when (exception is not GitException)
        {
            process.Dispose();
            throw new GitException($"Could not start '{ExecutablePath}': {exception.Message}", arguments, -1, string.Empty);
        }

        return process;
    }

    /// <summary>
    /// Reads a stream as raw bytes into one decoded string, keeping every character git wrote. The capture limit
    /// is enforced while reading, so memory stays bounded even for a huge output. Head mode keeps the first
    /// <paramref name="captureLimit"/> characters of the captured text, tail mode the last ones; the per-line callback sees
    /// every line either way.
    /// </summary>
    private static async Task<PumpResult> PumpAsync(
        Stream stream,
        Action<string>? onLine,
        int captureLimit,
        bool keepTail,
        bool carriageReturnEndsLine,
        CancellationToken ct)
    {
        var limit = Math.Max(0, captureLimit);
        var capture = new StringBuilder(Math.Min(limit, 64 * 1024));
        var line = onLine is null ? null : new StringBuilder(256);
        var truncated = false;

        // A chunk that ends on '\r' has already ended its line; whether the '\n' that opens the next chunk is the
        // second half of that CRLF can only be decided when the chunk arrives, so the pending skip survives between calls.
        var skipLeadingLf = false;
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(ByteBufferSize));
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    truncated = true;
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes.AsSpan(0, read), chars, flush: false);
                if (count > 0)
                {
                    Consume(chars.AsSpan(0, count), capture, line, onLine, limit, keepTail, carriageReturnEndsLine, ref skipLeadingLf, ref truncated);
                }
            }

            var tail = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            if (tail > 0)
            {
                Consume(chars.AsSpan(0, tail), capture, line, onLine, limit, keepTail, carriageReturnEndsLine, ref skipLeadingLf, ref truncated);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }

        if (line is not null && onLine is not null && line.Length > 0)
        {
            onLine(line.ToString());
        }

        if (keepTail && capture.Length > limit)
        {
            DropLeading(capture, capture.Length - limit);
            truncated = true;
        }

        if (truncated && capture.Length > 0)
        {
            if (keepTail)
            {
                capture.Insert(0, $"[… output truncated; showing the last {capture.Length.ToString(CultureInfo.InvariantCulture)} characters]\n");
            }
            else
            {
                capture.Append("\n… [output truncated]");
            }
        }

        return new PumpResult(capture.ToString(), truncated);
    }

    private static void Consume(
        ReadOnlySpan<char> span,
        StringBuilder capture,
        StringBuilder? line,
        Action<string>? onLine,
        int limit,
        bool keepTail,
        bool carriageReturnEndsLine,
        ref bool skipLeadingLf,
        ref bool truncated)
    {
        if (keepTail)
        {
            capture.Append(span);

            // Trim in bulk once the buffer reaches twice the limit, so keeping the tail costs O(1) per character.
            if (capture.Length >= 2L * limit && capture.Length > limit)
            {
                DropLeading(capture, capture.Length - limit);
                truncated = true;
            }
        }
        else
        {
            var room = limit - capture.Length;
            if (room <= 0)
            {
                truncated = true;
            }
            else if (span.Length <= room)
            {
                capture.Append(span);
            }
            else
            {
                capture.Append(span[..room]);
                truncated = true;
            }
        }

        if (line is null || onLine is null)
        {
            return;
        }

        var position = 0;

        // The pending skip is spent on the first character of this chunk whatever it is: a '\n' is the second half of
        // the CRLF that ended the last chunk, anything else means the '\r' stood alone and the break already happened.
        if (skipLeadingLf)
        {
            skipLeadingLf = false;
            if (span.Length > 0 && span[0] == '\n')
            {
                position = 1;
            }
        }

        while (position < span.Length)
        {
            var rest = span[position..];
            var newline = carriageReturnEndsLine ? rest.IndexOfAny('\n', '\r') : rest.IndexOf('\n');
            if (newline < 0)
            {
                AppendBounded(line, rest, MaxCallbackLineCharacters);
                return;
            }

            AppendBounded(line, rest[..newline], MaxCallbackLineCharacters);
            position += newline + 1;
            var text = line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
            line.Clear();
            onLine(text);

            // CRLF is one break, not a break plus an empty line: swallow the '\n' that follows this '\r', in this
            // chunk when it is already there and across the chunk boundary when it is not.
            if (carriageReturnEndsLine && rest[newline] == '\r')
            {
                if (position < span.Length)
                {
                    if (span[position] == '\n')
                    {
                        position++;
                    }
                }
                else
                {
                    skipLeadingLf = true;
                }
            }
        }
    }

    /// <summary>Removes the first <paramref name="count"/> characters, and a low surrogate this leaves at the front.</summary>
    private static void DropLeading(StringBuilder capture, int count)
    {
        if (count < capture.Length && char.IsLowSurrogate(capture[count]))
        {
            count++;
        }

        capture.Remove(0, Math.Min(count, capture.Length));
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new ChunkReader(stream);
        var line = new StringBuilder(256);
        while (true)
        {
            var chunk = reader.Chunk;
            if (chunk.Length == 0)
            {
                if (!await reader.FillAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                chunk = reader.Chunk;
                if (chunk.Length == 0)
                {
                    continue;
                }
            }

            var newline = chunk.IndexOf('\n');
            if (newline < 0)
            {
                AppendBounded(line, chunk, MaxCallbackLineCharacters);
                reader.Advance(chunk.Length);
                continue;
            }

            AppendBounded(line, chunk[..newline], MaxCallbackLineCharacters);
            reader.Advance(newline + 1);
            var text = line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
            line.Clear();
            yield return text;
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static async IAsyncEnumerable<string> ReadRecordsAsync(Stream stream, char separator, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new ChunkReader(stream);
        var record = new StringBuilder(256);
        while (true)
        {
            var chunk = reader.Chunk;
            if (chunk.Length == 0)
            {
                if (!await reader.FillAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                chunk = reader.Chunk;
                if (chunk.Length == 0)
                {
                    continue;
                }
            }

            var index = chunk.IndexOf(separator);
            if (index < 0)
            {
                AppendBounded(record, chunk, MaxStreamedRecordCharacters);
                reader.Advance(chunk.Length);
                continue;
            }

            AppendBounded(record, chunk[..index], MaxStreamedRecordCharacters);
            reader.Advance(index + 1);
            var text = record.ToString();
            record.Clear();
            yield return text;
        }

        if (record.Length > 0)
        {
            yield return record.ToString();
        }
    }

    private static void AppendBounded(StringBuilder builder, ReadOnlySpan<char> span, int maxLength)
    {
        var room = maxLength - builder.Length;
        if (room <= 0 || span.Length == 0)
        {
            return;
        }

        builder.Append(span.Length <= room ? span : span[..room]);
    }

    private static async Task FinishStreamAsync(
        Process process,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        CancellationToken exitToken,
        Task<PumpResult> stderrTask)
    {
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(exitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            timedOut = !ct.IsCancellationRequested;
        }

        var stderr = (await AwaitPumpAsync(stderrTask).ConfigureAwait(false)).Text;
        ct.ThrowIfCancellationRequested();
        if (timedOut)
        {
            throw new GitException($"git {Describe(arguments)} timed out after {timeout.TotalSeconds:0.#}s.", arguments, -1, stderr);
        }

        var exitCode = ExitCodeOf(process);
        if (exitCode != 0)
        {
            throw new GitException($"git {Describe(arguments)} failed with exit code {exitCode}: {Trim(stderr)}", arguments, exitCode, stderr);
        }
    }

    private static async Task<PumpResult> AwaitPumpAsync(Task<PumpResult> pump)
    {
        try
        {
            return await pump.WaitAsync(PumpGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Observe(pump);
            return PumpResult.Empty;
        }
    }

    private static void Observe(Task<PumpResult> pump) =>
        _ = pump.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
        }
    }

    private static List<string> Args(params string[] arguments)
    {
        var list = new List<string>(arguments.Length + ConfigPrefix.Length);
        list.AddRange(ConfigPrefix);
        list.AddRange(arguments);
        return list;
    }

    private static string Describe(IReadOnlyList<string> arguments)
    {
        var text = string.Join(' ', arguments);
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static string Trim(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private readonly record struct PumpResult(string Text, bool Truncated)
    {
        public static PumpResult Empty => new(string.Empty, false);
    }

    /// <summary>Incrementally decodes a byte stream into chunks of characters, so no read splits a code point.</summary>
    private sealed class ChunkReader(Stream stream) : IDisposable
    {
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private readonly byte[] bytes = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        private readonly char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(ByteBufferSize));
        private int start;
        private int end;
        private bool finished;

        public ReadOnlySpan<char> Chunk => chars.AsSpan(start, end - start);

        public void Advance(int count) => start += count;

        public async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (finished)
            {
                return false;
            }

            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    finished = true;
                    start = 0;
                    end = 0;
                    return false;
                }

                if (read == 0)
                {
                    finished = true;
                    start = 0;
                    end = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
                    return end > 0;
                }

                start = 0;
                end = decoder.GetChars(bytes.AsSpan(0, read), chars, flush: false);
                if (end > 0)
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
