namespace Forge.Workspace;

public enum GitEntryStatus { Unmodified, Modified, Added, Deleted, Renamed, Copied, TypeChanged, Unmerged, Untracked, Ignored }

public enum GitStatusKind { Ordinary, Renamed, Copied, Unmerged, Untracked, Ignored }

/// <summary>One <c>git status --porcelain=v2</c> entry; paths are workspace-relative and unquoted.</summary>
public sealed record GitStatusEntry(
    string Path,
    GitStatusKind Kind,
    GitEntryStatus IndexStatus,
    GitEntryStatus WorkTreeStatus,
    string? OriginalPath = null)
{
    public bool IsStaged => IndexStatus is not GitEntryStatus.Unmodified and not GitEntryStatus.Untracked;

    public bool IsUnstaged => Kind is GitStatusKind.Untracked || WorkTreeStatus is not GitEntryStatus.Unmodified;
}

/// <summary>What a checkout directory's HEAD points at; <see cref="NotRepository"/> also covers a directory git cannot read at all.</summary>
public enum GitHeadKind { NotRepository, Branch, UnbornBranch, Detached }

/// <summary>
/// The branch a checkout directory has checked out. <see cref="Branch"/> is null only for
/// <see cref="GitHeadKind.NotRepository"/> and <see cref="GitHeadKind.Detached"/>; an unborn branch (a fresh
/// <c>git init</c> with no commit yet) still reports the name HEAD points at.
/// </summary>
public sealed record GitHead(GitHeadKind Kind, string? Branch)
{
    public static readonly GitHead NotRepository = new(GitHeadKind.NotRepository, null);

    public static readonly GitHead Detached = new(GitHeadKind.Detached, null);
}

public sealed record GitBranch(string Name, string CommitId, bool IsCurrent, string? Upstream, DateTimeOffset? CommittedAt)
{
    /// <summary>The unambiguous full ref name, e.g. <c>refs/heads/main</c> or <c>refs/remotes/origin/main</c>.</summary>
    public string RefName { get; init; } = string.Empty;

    /// <summary>True for a remote-tracking ref; branch names alone are not sufficient to distinguish local from remote.</summary>
    public bool IsRemote { get; init; }
}

public sealed record GitCommit(
    string Id,
    string ShortId,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    string Body);

public sealed record GitLogOptions
{
    /// <summary>Revision or range such as <c>HEAD</c> or <c>main..feature</c>; null means <c>HEAD</c>.</summary>
    public string? Revision { get; init; }

    public IReadOnlyList<string> Paths { get; init; } = [];

    public int Limit { get; init; } = 50;

    public int Skip { get; init; }

    public bool FirstParent { get; init; }

    public bool IncludeMerges { get; init; } = true;

    public bool AllReferences { get; init; }
}

/// <summary>
/// Which tree pair a diff compares. Defaults match <c>git diff</c>: index versus working tree.
/// <see cref="From"/>/<see cref="To"/> name commits (or a commit and the working tree when
/// <see cref="To"/> is null), <see cref="Staged"/> compares <see cref="From"/> against the index.
/// </summary>
public sealed record GitDiffOptions
{
    public string? From { get; init; }

    public string? To { get; init; }

    public bool Staged { get; init; }

    public IReadOnlyList<string> Paths { get; init; } = [];

    public int ContextLines { get; init; } = 3;

    public bool DetectRenames { get; init; } = true;

    /// <summary>Appends synthetic add diffs for untracked files; git itself never shows them.</summary>
    public bool IncludeUntracked { get; init; }

    /// <summary>Untracked files larger than this are reported truncated instead of read whole.</summary>
    public int MaxUntrackedBytes { get; init; } = 1024 * 1024;
}

public sealed record GitWorktree(
    string Path,
    string? HeadCommit,
    string? Branch,
    bool IsDetached,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason,
    bool IsBare);
