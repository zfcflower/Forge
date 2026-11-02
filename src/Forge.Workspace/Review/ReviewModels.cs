namespace Forge.Workspace;

public enum ReviewCommentStatus { Draft, Sent }

/// <summary>
/// A review comment on one file of a workspace: a line or block range on the new side of the diff, tied to the
/// base commit it was written against and, when known, a <see cref="DiffAnchor"/> that survives later diffs.
/// </summary>
public sealed record ReviewComment(
    long Id,
    string WorkspacePath,
    string SessionId,
    string FilePath,
    int NewLineStart,
    int NewLineEnd,
    string Body,
    ReviewCommentStatus Status,
    bool IsResolved,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    string? BaseCommit,
    DiffAnchor? Anchor);

public sealed record NewReviewComment
{
    public required string WorkspacePath { get; init; }

    public required string SessionId { get; init; }

    public required string FilePath { get; init; }

    public required int NewLineStart { get; init; }

    /// <summary>Last line of the commented block; defaults to <see cref="NewLineStart"/>.</summary>
    public int? NewLineEnd { get; init; }

    public required string Body { get; init; }

    public string? BaseCommit { get; init; }

    public DiffAnchor? Anchor { get; init; }
}

public sealed record ReviewQuery
{
    public string? WorkspacePath { get; init; }

    public string? SessionId { get; init; }

    public string? FilePath { get; init; }

    public ReviewCommentStatus? Status { get; init; }

    public bool? IsResolved { get; init; }

    public int Limit { get; init; } = 500;

    public int Offset { get; init; }
}
