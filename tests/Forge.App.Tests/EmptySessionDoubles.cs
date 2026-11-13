using Forge.Abstractions;

namespace Forge.App.Tests;

/// <summary>
/// A session catalog that stores nothing: every write reports success and every read is empty, so a test can build a
/// session-backed view model without a database.
/// </summary>
internal sealed class EmptySessionCatalog(DateTimeOffset createdAt) : ISessionCatalog
{
    public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
        Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, createdAt));

    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>([]);

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A todo store that keeps nothing: the list is always empty, an added item comes back as pending under
/// <paramref name="itemId"/>, and every other write fails to find its item.
/// </summary>
internal sealed class EmptyTodoStore(string itemId, DateTimeOffset at) : ITodoStore
{
    public event Action<string>? Changed
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodoItem>>([]);

    public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
        Task.FromResult(new TodoItem(itemId, sessionId, title, TodoStatus.Pending, note, 0, at));

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
        Task.FromResult<TodoItem?>(null);

    public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// A message log that keeps nothing: every read is empty, every write reports success, and a created session is named
/// "created" under <paramref name="at"/>. The fakes of the turn-routing suites only need the store to behave like one.
/// </summary>
internal sealed class EmptySessionStore(DateTimeOffset at) : ISessionStore
{
    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
        Task.FromResult(new SessionInfo("created", workspacePath, title, at));

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>([]);

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
        Task.FromResult(1L);

    public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
        Empty();

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Task.FromResult(0L);

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>>([]);

    private static async IAsyncEnumerable<StoredMessage> Empty()
    {
        await Task.Yield();
        yield break;
    }
}

/// <summary>A checkpoint store that records nothing: a begin/complete reports success and the list is empty.</summary>
internal sealed class EmptyCheckpointStore : ITurnCheckpointStore
{
    public Task BeginAsync(string sessionId, int turn, string worktreePath, string treeBefore, DateTimeOffset at, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TurnCheckpoint>>([]);
}
