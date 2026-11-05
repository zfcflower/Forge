namespace Forge.Abstractions;

public enum SessionStatus { Active, Archived }

/// <summary>
/// A stored conversation. <see cref="WorkspacePath"/> is the agent's working directory (a worktree, the repository
/// root, or a general-area scratch folder); sub-agent sessions carry <see cref="ParentSessionId"/>.
/// </summary>
public sealed record SessionInfo(string Id, string WorkspacePath, string Title, DateTimeOffset CreatedAt)
{
    public string? ProjectId { get; init; }

    public string? WorktreePath { get; init; }

    public string? Branch { get; init; }

    public string? ParentSessionId { get; init; }

    /// <summary>Agent kind of a sub-agent session (e.g. "Explore"); null for a main session.</summary>
    public string? AgentKind { get; init; }

    /// <summary>Full task text of a sub-agent session; null for a main session.</summary>
    public string? Task { get; init; }

    public SessionStatus Status { get; init; } = SessionStatus.Active;

    public DateTimeOffset LastActiveAt { get; init; }
}

public sealed record SessionCreate(string WorkspacePath, string Title)
{
    public string? ProjectId { get; init; }

    public string? WorktreePath { get; init; }

    public string? Branch { get; init; }

    public string? ParentSessionId { get; init; }

    public string? AgentKind { get; init; }

    public string? Task { get; init; }
}

/// <summary>Filter for <see cref="ISessionCatalog.QueryAsync"/>; null members do not filter.</summary>
public sealed record SessionQuery
{
    public string? ProjectId { get; init; }

    public string? WorktreePath { get; init; }

    public SessionStatus? Status { get; init; }

    /// <summary>Null = any; empty string = main sessions only (no parent); otherwise children of that session.</summary>
    public string? ParentSessionId { get; init; }
}

/// <summary>
/// Session metadata beyond the message log: project/worktree binding, parent links, archive state and activity.
/// Implemented by the same store as <see cref="ISessionStore"/>; results are ordered by <see cref="SessionInfo.LastActiveAt"/> descending.
/// </summary>
public interface ISessionCatalog
{
    Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct);

    Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct);

    Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct);

    Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct);

    Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct);
}

public sealed record StoredMessage(long Seq, ChatMessage Message, TokenUsage? Usage, DateTimeOffset At);

/// <summary>Summary layers: 1 = stage summaries, 2 = session-level summary. Layer 0 (recent raw) is not stored as a summary.</summary>
public sealed record SummaryRecord(string SessionId, int Layer, long FromSeq, long ToSeq, string Text, DateTimeOffset At);

public sealed record SearchHit(long Seq, string Snippet, double Score);

public sealed record ArtifactInfo(string Id, string SessionId, string Name, long SizeBytes, DateTimeOffset At);

public interface ISessionStore
{
    Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct);
    Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct);

    /// <summary>Appends one message and returns its sequence number (monotonic per session, starting at 1).</summary>
    Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct);
    /// <summary>Reads messages with fromSeq &lt;= Seq &lt;= toSeq (toSeq null = to the end), in order.</summary>
    IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct);
    Task<long> LastSeqAsync(string sessionId, CancellationToken ct);

    Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct);
    Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct);

    /// <summary>Full-text search over the session's messages, including ones already compacted away.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct);
}

public interface IArtifactStore
{
    Task<ArtifactInfo> PutAsync(string sessionId, string name, ReadOnlyMemory<byte> content, CancellationToken ct);
    Task<ArtifactInfo?> GetInfoAsync(string artifactId, CancellationToken ct);
    /// <summary>Reads a byte range; length null = to the end.</summary>
    Task<ReadOnlyMemory<byte>> ReadAsync(string artifactId, long offset, int? length, CancellationToken ct);
}
