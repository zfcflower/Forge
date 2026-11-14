namespace Forge.Core.Tools;

/// <summary>
/// What a review change set compares. Either one commit against its first parent (<see cref="Commit"/>, how a finished
/// checklist step is reviewed), or everything on the branch plus the uncommitted tree against <see cref="Base"/>
/// (null = the repository's default branch), narrowed to <see cref="Paths"/> when given.
/// </summary>
public sealed record ReviewChangeSetRequest
{
    /// <summary>The session's worktree; the change set is read there and nowhere else.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Commit or ref to compare against; null means the repository's default branch. Ignored when <see cref="Commit"/> is set.</summary>
    public string? Base { get; init; }

    /// <summary>A single commit to review against its first parent; null reviews the branch and the working tree.</summary>
    public string? Commit { get; init; }

    /// <summary>Compare only these paths; empty means the whole change set.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>One file of a change set. <see cref="Status"/> is <c>added</c>, <c>modified</c>, <c>deleted</c>, <c>renamed</c> or <c>copied</c>.</summary>
public sealed record ReviewChangedFile(string Path, string Status, bool IsBinary);

/// <summary>One commit of a change set: the full sha and its subject line.</summary>
public sealed record ReviewCommit(string Sha, string Subject);

/// <summary>
/// The git side of a review, already rendered: the package text (see <c>ReviewPackageText</c>) decides how to lay it out,
/// this type only carries what git answered. <see cref="DiffText"/> is git's own unified diff, so the reviewer sees the
/// same bytes the agent would get from <c>git_diff</c>.
/// </summary>
public sealed record ReviewChangeSet
{
    /// <summary>The worktree the change set was read in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>HEAD of the worktree the review is bound to: a later commit is a different change set.</summary>
    public string? HeadCommit { get; init; }

    /// <summary>
    /// The revision the diff runs from: the resolved default branch, the reviewed commit's parent (null for a root
    /// commit, which then diffs against the empty tree), or HEAD when only uncommitted work is under review.
    /// </summary>
    public string? Base { get; init; }

    /// <summary>The files the change set touches, in git's order.</summary>
    public IReadOnlyList<ReviewChangedFile> Files { get; init; } = [];

    /// <summary>The commits the change set contains, newest first; empty when only the working tree changed.</summary>
    public IReadOnlyList<ReviewCommit> Commits { get; init; } = [];

    /// <summary>The unified diff itself; empty when nothing changed.</summary>
    public string DiffText { get; init; } = string.Empty;

    /// <summary>
    /// The diff is incomplete: git's capture limit or the source's own cap cut it. The package tells the reviewer which
    /// paths to read with <c>git_diff</c>.
    /// </summary>
    public bool DiffTruncated { get; init; }
}

/// <summary>
/// Either a change set or the reason there is none. Failures are text the model can act on, never exceptions; only
/// cancellation throws.
/// </summary>
public sealed record ReviewChangeSetResult(ReviewChangeSet? ChangeSet, string? Error)
{
    /// <summary>True when <see cref="ChangeSet"/> is null and <see cref="Error"/> says why.</summary>
    public bool IsError => ChangeSet is null;

    public static ReviewChangeSetResult Ok(ReviewChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return new ReviewChangeSetResult(changeSet, null);
    }

    public static ReviewChangeSetResult Failed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new ReviewChangeSetResult(null, error);
    }
}

/// <summary>
/// The only git capability Core holds for reviews. <c>Forge.Core</c> does not reference git: the app implements this over
/// its own git layer and hands it to <c>AgentHostOptions.Reviews</c>, which is what registers <c>request_review</c>. A host
/// without an implementation has no review tool, exactly as it has no <c>todo</c> tool without a store.
/// </summary>
public interface IReviewChangeSetSource
{
    /// <summary>
    /// Builds the change set of <paramref name="request"/>. Expected failures (not a repository, unknown revision, a diff
    /// over git's capture limit) come back as <see cref="ReviewChangeSetResult.Error"/>; the reviewer must never receive a
    /// silently empty change set in their place.
    /// </summary>
    Task<ReviewChangeSetResult> BuildAsync(ReviewChangeSetRequest request, CancellationToken ct);
}
