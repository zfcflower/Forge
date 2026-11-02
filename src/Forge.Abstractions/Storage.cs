namespace Forge.Abstractions;

public sealed record SessionInfo(string Id, string WorkspacePath, string Title, DateTimeOffset CreatedAt);

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
