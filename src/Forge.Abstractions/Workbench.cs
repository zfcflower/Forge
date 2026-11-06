namespace Forge.Abstractions;

public enum ProjectKind { Repository, General }

/// <summary>
/// A registered project. <see cref="ProjectKind.Repository"/> roots are git repository roots; the single
/// <see cref="ProjectKind.General"/> project is the general workspace for sessions unrelated to any repository.
/// </summary>
public sealed record ProjectInfo(string Id, string Name, string RootPath, ProjectKind Kind, int SortOrder, DateTimeOffset AddedAt);

public interface IProjectRegistry
{
    Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct);

    /// <summary>Registers <paramref name="rootPath"/> (full path, compared case-insensitively on Windows); returns the existing entry when already registered.</summary>
    Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct);

    /// <summary>Unregisters the project only; sessions, worktrees and files are kept.</summary>
    Task<bool> RemoveAsync(string projectId, CancellationToken ct);

    Task<bool> RenameAsync(string projectId, string name, CancellationToken ct);

    Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct);
}

public enum TodoStatus { Pending, InProgress, Done }

/// <summary>
/// One checklist item. The trailing step fields belong to step commits: <see cref="CommitSubject"/>, <see cref="Summary"/>
/// and <see cref="Verification"/> are written by the agent (or the user); <see cref="CommitId"/> and
/// <see cref="CommitError"/> are the outcome of the last step commit, at most one of them non-null.
/// </summary>
public sealed record TodoItem(
    string Id,
    string SessionId,
    string Title,
    TodoStatus Status,
    string? Note,
    int Order,
    DateTimeOffset UpdatedAt,
    string? CommitSubject = null,
    string? Summary = null,
    string? Verification = null,
    string? CommitId = null,
    string? CommitError = null);

/// <summary>
/// The agent-written step fields of a checklist item. A null property leaves the stored value unchanged; an empty string
/// clears it.
/// </summary>
public sealed record TodoStepFields(string? CommitSubject = null, string? Summary = null, string? Verification = null);

/// <summary>The session-level plan summary (Summary / Changes / Testing sections) that becomes the PR body.</summary>
public sealed record PlanSummary(string SessionId, string Text, DateTimeOffset UpdatedAt);

/// <summary>Per-session checklist edited by both the user and the agent; <see cref="Changed"/> carries the session id.</summary>
public interface ITodoStore
{
    event Action<string>? Changed;

    Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct);

    /// <summary>Appends, or inserts right after <paramref name="afterId"/> when given.</summary>
    Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct);

    /// <summary>Null arguments leave the field unchanged; returns null when the item does not exist.</summary>
    Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct);

    /// <summary>
    /// <see cref="UpdateAsync(string, string?, TodoStatus?, string?, CancellationToken)"/> plus the step fields, in one
    /// write. A null <paramref name="step"/> (or a null property of it) leaves those fields unchanged.
    /// </summary>
    /// <para>The default body serves stores that predate step fields (test doubles): without step values it is the plain
    /// update, with them it throws <see cref="NotSupportedException"/>. <c>SqliteTodoStore</c> overrides every new member.</para>
    Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, TodoStepFields? step, CancellationToken ct) =>
        step is null || (step.CommitSubject is null && step.Summary is null && step.Verification is null)
            ? UpdateAsync(id, title, status, note, ct)
            : throw new NotSupportedException("This todo store does not keep step fields.");

    /// <summary>
    /// Records the outcome of a step commit: the commit sha, or the reason it failed (the other one is cleared); both null
    /// clears the outcome. Does not touch the status or any other field. Returns null when the item does not exist.
    /// </summary>
    Task<TodoItem?> SetCommitResultAsync(string id, string? commitId, string? commitError, CancellationToken ct) =>
        throw new NotSupportedException("This todo store does not keep step commit results.");

    Task<bool> RemoveAsync(string id, CancellationToken ct);

    Task<bool> MoveAsync(string id, int order, CancellationToken ct);

    /// <summary>The session's plan summary, or null when none was written.</summary>
    Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct) => Task.FromResult<PlanSummary?>(null);

    /// <summary>Replaces the session's plan summary and raises <see cref="Changed"/> with the session id.</summary>
    Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct) =>
        throw new NotSupportedException("This todo store does not keep plan summaries.");
}

public enum StepCommitOutcome
{
    /// <summary>The step's changes were committed; <see cref="StepCommitResult.CommitId"/> is the new commit.</summary>
    Committed,

    /// <summary>The worktree had no uncommitted changes, so no (empty) commit was made.</summary>
    NoChanges,

    /// <summary>Step commits are switched off, or the session is not in a git repository.</summary>
    Skipped,

    /// <summary>The commit failed (hook, conflict in progress, detached HEAD, ...); the item stays done.</summary>
    Failed,
}

/// <summary><paramref name="Message"/> is a short English sentence for the tool result and the TODO row.</summary>
public sealed record StepCommitResult(StepCommitOutcome Outcome, string? CommitId, string Message);

/// <summary>
/// Commits one finished checklist step: everything uncommitted in the session's worktree (<c>add -A</c> + <c>commit</c>),
/// with <see cref="TodoItem.CommitSubject"/> (or the title) as the subject and <see cref="TodoItem.Summary"/> as the body.
/// Called when an item goes from not-done to done, whether the agent or the user ticked it. Implementations persist the
/// outcome through <see cref="ITodoStore.SetCommitResultAsync"/> before returning, never throw for an expected failure
/// (only cancellation throws) and never roll the done status back.
/// </summary>
public interface IStepCommitter
{
    Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct);
}

/// <summary>Opaque UI state blobs (layout snapshot, open tabs) keyed by name, each with a schema version.</summary>
public sealed record UiStateEntry(string Key, int Version, string Json, DateTimeOffset UpdatedAt);

public interface IUiStateStore
{
    Task<UiStateEntry?> GetAsync(string key, CancellationToken ct);

    Task PutAsync(string key, int version, string json, CancellationToken ct);

    Task<bool> DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// Worktree tree snapshots around one main-session turn. Tree hashes come from a temporary git index, so the user's
/// index, branch and stash are untouched; the turn's changes are the diff between the two trees.
/// </summary>
public sealed record TurnCheckpoint(
    string SessionId,
    int Turn,
    string WorktreePath,
    string TreeBefore,
    string? TreeAfter,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public interface ITurnCheckpointStore
{
    Task BeginAsync(string sessionId, int turn, string worktreePath, string treeBefore, DateTimeOffset at, CancellationToken ct);

    Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct);

    /// <summary>Ordered by turn ascending.</summary>
    Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct);
}
