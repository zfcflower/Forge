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

    /// <summary>
    /// Hard-deletes the registration row; sessions, worktrees and files are kept. Superseded by <see cref="CloseAsync"/>
    /// and no longer reachable from the UI.
    /// </summary>
    Task<bool> RemoveAsync(string projectId, CancellationToken ct);

    /// <summary>
    /// Soft close: sets <c>closed_at</c>. A closed project is left out of <see cref="ListAsync"/>; <see cref="AddAsync"/>
    /// with the same root reopens it (clears <c>closed_at</c>) and returns the original id, so its sessions come back.
    /// Session states are not touched.
    /// </summary>
    Task<bool> CloseAsync(string projectId, CancellationToken ct) =>
        throw new NotSupportedException(nameof(CloseAsync));

    Task<bool> RenameAsync(string projectId, string name, CancellationToken ct);

    Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct);
}

/// <summary>
/// A checklist item's state. One step at a time is <see cref="InProgress"/>: a store that starts a step returns the
/// session's previous in-progress step to <see cref="Pending"/> in the same write. <see cref="Paused"/> means the step
/// waits for something outside the agent (a review, an answer, a dependency) and never commits.
/// </summary>
public enum TodoStatus { Pending, InProgress, Paused, Done }

/// <summary>Who put the item on the checklist: the agent's plan, or the user's queue in the TODO panel.</summary>
public enum TodoOrigin { Agent, User }

/// <summary>
/// One checklist item: either a step of a group (<see cref="GroupId"/> non-null) or a loose item of the session's queue.
/// <see cref="Order"/> is dense and zero-based inside its own layer - the loose queue, or one group. The trailing step
/// fields belong to step commits: <see cref="CommitSubject"/>, <see cref="Summary"/> and <see cref="Verification"/> are
/// written by the agent; <see cref="CommitId"/> and <see cref="CommitError"/> are the outcome of the last step commit, at
/// most one of them non-null.
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
    string? CommitError = null,
    string? GroupId = null,
    TodoOrigin Origin = TodoOrigin.Agent);

/// <summary>
/// A named plan in the session's TODO panel: a title plus the steps that belong to it (<c>TodoItem.GroupId</c>). A group
/// has no status of its own - the panel derives it from the steps (all done, one in progress, one paused, else pending).
/// <see cref="Order"/> is dense and zero-based across the session's groups.
/// </summary>
public sealed record TodoGroup(string Id, string SessionId, string Title, int Order, DateTimeOffset UpdatedAt);

/// <summary>
/// One add either appends to the session's loose queue (<paramref name="GroupId"/> null), or - with a group id - to that
/// group's steps; <paramref name="AfterId"/> inserts right after an item of the same layer, whichever layer that is.
/// </summary>
public sealed record TodoAddRequest(
    string SessionId,
    string Title,
    string? Note = null,
    string? AfterId = null,
    string? GroupId = null,
    TodoOrigin Origin = TodoOrigin.Agent);

/// <summary>
/// The agent-written step fields of a checklist item. A null property leaves the stored value unchanged; an empty string
/// clears it.
/// </summary>
public sealed record TodoStepFields(string? CommitSubject = null, string? Summary = null, string? Verification = null);

/// <summary>The session-level plan summary (Summary / Changes / Testing sections) that becomes the PR body.</summary>
public sealed record PlanSummary(string SessionId, string Text, DateTimeOffset UpdatedAt);

/// <summary>
/// One replaced plan summary: the text the session's plan summary held before it was overwritten, and the moment it was
/// replaced. A store keeps the newest revisions of a session and drops older ones, so the list is a bounded edit history
/// of the plan rather than a log that grows with every rewrite.
/// </summary>
public sealed record PlanRevision(string SessionId, string Text, DateTimeOffset ReplacedAt);

/// <summary>Per-session checklist edited by both the user and the agent; <see cref="Changed"/> carries the session id.</summary>
public interface ITodoStore
{
    event Action<string>? Changed;

    Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct);

    /// <summary>Appends, or inserts right after <paramref name="afterId"/> when given.</summary>
    Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct);

    /// <summary>
    /// Null arguments leave the field unchanged; returns null when the item does not exist. A status of
    /// <see cref="TodoStatus.InProgress"/> also returns the session's other in-progress item to
    /// <see cref="TodoStatus.Pending"/> in the same write.
    /// </summary>
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

    /// <summary>
    /// Adds an item to the loose queue or to a group, recording who added it. The default body serves stores that predate
    /// groups (test doubles): a loose agent item goes to the plain <see cref="AddAsync(string, string, string?, string?, CancellationToken)"/>
    /// and everything else throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<TodoItem> AddAsync(TodoAddRequest request, CancellationToken ct) =>
        request.GroupId is null && request.Origin == TodoOrigin.Agent
            ? AddAsync(request.SessionId, request.Title, request.Note, request.AfterId, ct)
            : throw new NotSupportedException("This todo store does not keep groups or item origins.");

    /// <summary>The session's groups, ordered. A store without group support returns none.</summary>
    Task<IReadOnlyList<TodoGroup>> ListGroupsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodoGroup>>([]);

    /// <summary>
    /// The session's group with this title (trimmed, ordinal), or a new empty group appended after the existing ones.
    /// </summary>
    Task<TodoGroup> FindOrCreateGroupAsync(string sessionId, string title, CancellationToken ct) =>
        throw new NotSupportedException("This todo store does not keep groups.");

    /// <summary>
    /// Turns a loose item into a group of its own: the group inherits the item's id and title (<paramref name="title"/> only
    /// when it should change), and the item leaves the queue. Returns null when the item does not exist (or is a step).
    /// </summary>
    Task<TodoGroup?> PromoteAsync(string itemId, string? title, CancellationToken ct) =>
        throw new NotSupportedException("This todo store does not promote loose items into groups.");

    Task<bool> RemoveAsync(string id, CancellationToken ct);

    /// <summary>Reorders the item inside its own layer (its group, or the loose queue); the order is clamped to the layer.</summary>
    Task<bool> MoveAsync(string id, int order, CancellationToken ct);

    /// <summary>The session's plan summary, or null when none was written.</summary>
    Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct) => Task.FromResult<PlanSummary?>(null);

    /// <summary>
    /// Replaces the session's plan summary and raises <see cref="Changed"/> with the session id. A store that keeps plan
    /// history also records the replaced text as a <see cref="PlanRevision"/> in the same write, unless the new text equals
    /// the stored one.
    /// </summary>
    Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct) =>
        throw new NotSupportedException("This todo store does not keep plan summaries.");

    /// <summary>
    /// The session's replaced plan summaries, newest first, at most <paramref name="limit"/>. A store without plan history
    /// returns none; the default body is that answer, so a test double keeps working.
    /// </summary>
    Task<IReadOnlyList<PlanRevision>> ListPlanRevisionsAsync(string sessionId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlanRevision>>([]);
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
/// Called when the agent marks an item done. Implementations persist the
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
