using System.Collections.Concurrent;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Live settings the git tools read on every call, so a change in the settings page applies to the next call.</summary>
public sealed record GitToolSettings
{
    public Func<GitPullStrategy> PullStrategy { get; init; } = static () => GitPullStrategy.FastForwardOnly;

    public Func<TimeSpan> NetworkTimeout { get; init; } = static () => TimeSpan.FromMinutes(2);
}

/// <summary>
/// Remembers, per session and worktree, what the agent has read of the branch diff at which HEAD (through <c>git_diff</c>
/// against the default base). PR creation refuses unless the last read of the full file list was made at the current HEAD
/// and every file of that list has since been shown in full at that HEAD, which is how the "self-review the whole diff"
/// gate is enforced in code. A read shows a limited amount of diff text, so a large change takes several reads; files
/// shown by reads narrowed with <c>paths</c> count towards it. Thread-safe; in memory only, so a restart requires a re-read.
/// </summary>
public sealed class GitReviewLedger
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class State
    {
        public string Head = string.Empty;

        /// <summary>The files of the last unfiltered read at <see cref="Head"/>; null until there was one.</summary>
        public HashSet<string>? Required;

        public readonly HashSet<string> Covered = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, State>> bySession =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Records an unfiltered read at <paramref name="headCommit"/>: <paramref name="allFiles"/> is every changed file the
    /// read found and <paramref name="shownFiles"/> those whose diff it showed in full. Files shown earlier at the same HEAD
    /// stay covered.
    /// </summary>
    public void RecordFullRead(string sessionId, string workingDirectory, string headCommit, IEnumerable<string> allFiles, IEnumerable<string> shownFiles)
    {
        var state = StateFor(sessionId, workingDirectory, headCommit);
        lock (state)
        {
            ResetOnNewHead(state, headCommit);
            state.Required = new HashSet<string>(allFiles, StringComparer.Ordinal);
            state.Covered.UnionWith(shownFiles);
        }
    }

    /// <summary>Records files shown in full by a read narrowed with paths; it never changes which files are required.</summary>
    public void RecordShown(string sessionId, string workingDirectory, string headCommit, IEnumerable<string> shownFiles)
    {
        var state = StateFor(sessionId, workingDirectory, headCommit);
        lock (state)
        {
            ResetOnNewHead(state, headCommit);
            state.Covered.UnionWith(shownFiles);
        }
    }

    /// <summary>
    /// What is still unread at <paramref name="headCommit"/>: null when no unfiltered read was made at exactly that HEAD
    /// (the full file list is not known yet), otherwise the changed files whose diff was not shown in full (empty = done).
    /// </summary>
    public IReadOnlyList<string>? Unread(string sessionId, string workingDirectory, string headCommit)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(headCommit))
        {
            return null;
        }

        if (!bySession.TryGetValue(sessionId, out var paths)
            || !paths.TryGetValue(WorkspacePaths.Normalize(workingDirectory), out var state))
        {
            return null;
        }

        lock (state)
        {
            if (state.Required is null || !string.Equals(state.Head, headCommit, StringComparison.Ordinal))
            {
                return null;
            }

            var unread = new List<string>();
            foreach (var file in state.Required)
            {
                if (!state.Covered.Contains(file))
                {
                    unread.Add(file);
                }
            }

            unread.Sort(StringComparer.Ordinal);
            return unread;
        }
    }

    /// <summary>The HEAD of the last unfiltered read, or null.</summary>
    public string? LastReviewed(string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        if (!bySession.TryGetValue(sessionId, out var paths)
            || !paths.TryGetValue(WorkspacePaths.Normalize(workingDirectory), out var state))
        {
            return null;
        }

        lock (state)
        {
            return state.Required is not null ? state.Head : null;
        }
    }

    private State StateFor(string sessionId, string workingDirectory, string headCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommit);
        var paths = bySession.GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, State>(PathComparer));
        return paths.GetOrAdd(WorkspacePaths.Normalize(workingDirectory), static _ => new State());
    }

    private static void ResetOnNewHead(State state, string headCommit)
    {
        if (!string.Equals(state.Head, headCommit, StringComparison.Ordinal))
        {
            state.Head = headCommit;
            state.Required = null;
            state.Covered.Clear();
        }
    }
}

/// <summary>
/// The agent's branch and remote tools. Every call works in <see cref="ToolContext.WorkingDirectory"/> (the session's
/// worktree). Read-only: <c>git_status</c>, <c>git_log</c>, <c>git_diff</c>, <c>git_fetch</c> (updates remote-tracking
/// refs only). Write: <c>git_branch</c> (create, switch, set upstream, delete) and <c>git_sync</c> (pull, push, rebase,
/// merge, continue, abort). Split by side effect instead of resolving it per call, so the approval policy sees a fixed
/// side effect per tool name. Results are compact text the model can read; failures are tool errors that name the
/// <see cref="GitRemoteFailureKind"/> and the next step to take.
/// </summary>
public sealed class GitTools
{
    public const string StatusName = "git_status";
    public const string LogName = "git_log";
    public const string DiffName = "git_diff";
    public const string FetchName = "git_fetch";
    public const string BranchName = "git_branch";
    public const string SyncName = "git_sync";

    public GitTools(GitCli git, GitToolSettings settings, GitReviewLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);

        Status = new GitStatusTool(git);
        Log = new GitLogTool(git);
        Diff = new GitDiffTool(git, ledger);
        Fetch = new GitFetchTool(git, settings);
        Branch = new GitBranchTool(git);
        Sync = new GitSyncTool(git, settings);
        All = [Status, Log, Diff, Fetch, Branch, Sync];
    }

    /// <summary>
    /// <c>{}</c> → branch, HEAD, upstream, ahead/behind (as of the last fetch), remotes, in-progress operation,
    /// conflicted paths, and changed files (porcelain, capped at 200 with a count of the rest).
    /// </summary>
    public ITool Status { get; }

    /// <summary>
    /// <c>{"revision"?: string, "limit"?: int (default 20, max 200)}</c> → exactly one line per commit
    /// (<c>shortsha subject</c>), newest first, no header and no trailing summary line. The description tells the agent to
    /// read the last 20 subjects before writing a commit subject and to follow the repository's style
    /// (Conventional Commits or prose).
    /// </summary>
    public ITool Log { get; }

    /// <summary>
    /// <c>{"base"?: string, "paths"?: string[], "stat_only"?: bool}</c> → <c>git diff &lt;base&gt;...HEAD</c> followed by the
    /// uncommitted working-tree diff (untracked files included). <c>base</c> defaults to the repository's default branch.
    /// The tool caps its own text (<see cref="GitDiffTool"/>): whole-file diffs are shown while they fit and the files left
    /// out are named, so nothing is spilled to an artifact and the model knows what it has not seen. A non-stat read
    /// against the default base records the files it showed in the <see cref="GitReviewLedger"/> (an unfiltered one also the
    /// full file list); <c>stat_only</c> and a non-default base never do.
    /// </summary>
    public ITool Diff { get; }

    /// <summary><c>{"remote"?: string}</c> → fetches and reports the new ahead/behind.</summary>
    public ITool Fetch { get; }

    /// <summary>
    /// <c>{"operation": "create"|"switch"|"set_upstream"|"delete", "name"?: string, "start_point"?: string,
    /// "upstream"?: string, "force"?: bool}</c>. Delete refuses the current branch and a branch checked out in any
    /// worktree.
    /// </summary>
    public ITool Branch { get; }

    /// <summary>
    /// <c>{"operation": "pull"|"push"|"rebase"|"merge"|"continue"|"abort", "onto"?: string, "remote"?: string,
    /// "set_upstream"?: bool, "force_with_lease"?: bool}</c>. Pull only fast-forwards when the strategy is ff-only,
    /// rebase or merge otherwise, per <see cref="GitToolSettings.PullStrategy"/>; the strategy is never taken from this
    /// call's arguments – the user owns it in settings. Push always re-reads the remote tip with ls-remote immediately
    /// before pushing and uses it as the lease; <c>set_upstream</c> defaults to true when the branch has no upstream.
    /// Rebase/merge do <b>not</b> fetch: they rebase onto <c>onto</c>, which defaults to the current branch's upstream and
    /// otherwise to <c>origin/&lt;default branch&gt;</c> as it was last fetched, so a fetch is the caller's explicit step
    /// (rationale: never rebase onto something you did not just look at). On conflicts the result lists the conflicted
    /// files and tells the agent to read both sides' history (<c>git log -p</c>) before resolving, then <c>continue</c>.
    /// </summary>
    public ITool Sync { get; }

    /// <summary>All six, for registration.</summary>
    public IReadOnlyList<ITool> All { get; }
}
