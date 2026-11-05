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

public sealed record TodoItem(string Id, string SessionId, string Title, TodoStatus Status, string? Note, int Order, DateTimeOffset UpdatedAt);

/// <summary>Per-session checklist edited by both the user and the agent; <see cref="Changed"/> carries the session id.</summary>
public interface ITodoStore
{
    event Action<string>? Changed;

    Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct);

    /// <summary>Appends, or inserts right after <paramref name="afterId"/> when given.</summary>
    Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct);

    /// <summary>Null arguments leave the field unchanged; returns null when the item does not exist.</summary>
    Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct);

    Task<bool> RemoveAsync(string id, CancellationToken ct);

    Task<bool> MoveAsync(string id, int order, CancellationToken ct);
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
