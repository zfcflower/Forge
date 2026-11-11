namespace Forge.Abstractions;

/// <summary>
/// Lifecycle of a stored session. <see cref="Deleted"/> is a soft delete: the row and its messages stay, the
/// directory went to the system trash (when Forge owned it), and only a purge removes the row.
/// </summary>
public enum SessionStatus { Active, Archived, Deleted }

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

    /// <summary>When the session was soft-deleted; null unless <see cref="Status"/> is <see cref="SessionStatus.Deleted"/>.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Branch this session's worktree was forked from (parallel fork); null for a session that is not a fork.</summary>
    public string? ForkOfBranch { get; init; }

    /// <summary>Commit the fork branch started from; set together with <see cref="ForkOfBranch"/>.</summary>
    public string? ForkBaseSha { get; init; }

    /// <summary>Session whose history this one continues by reference (full inheritance); never a sub-agent parent link.</summary>
    public string? InheritsFromSessionId { get; init; }

    /// <summary>Last parent message sequence number that belongs to this session's inherited prefix.</summary>
    public long? InheritsUpToSeq { get; init; }

    /// <summary>Root of the inheritance / handover / fork lineage; a store returns the session's own id when none is recorded.</summary>
    public string? LineageRootId { get; init; }

    /// <summary>How the session's context began.</summary>
    public SessionOrigin Origin { get; init; } = SessionOrigin.Cold;

    /// <summary>Hash of the last composed stable prefix (system prompt + tool definitions); null until first computed.</summary>
    public string? PrefixFingerprint { get; init; }
}

public sealed record SessionCreate(string WorkspacePath, string Title)
{
    public string? ProjectId { get; init; }

    public string? WorktreePath { get; init; }

    public string? Branch { get; init; }

    public string? ParentSessionId { get; init; }

    public string? AgentKind { get; init; }

    public string? Task { get; init; }

    /// <summary>Fork source branch; see <see cref="SessionInfo.ForkOfBranch"/>.</summary>
    public string? ForkOfBranch { get; init; }

    public string? ForkBaseSha { get; init; }

    /// <summary>Lineage root to record; null = the new session's own id.</summary>
    public string? LineageRootId { get; init; }

    public string? InheritsFromSessionId { get; init; }

    public long? InheritsUpToSeq { get; init; }

    public SessionOrigin Origin { get; init; } = SessionOrigin.Cold;
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

    // ---- Session / worktree lifecycle (TASK-session-worktree-lifecycle, section 4.3). ----
    //
    // Occupancy rule: at most one Active root session (no parent) per occupancy key, where the key is the normalized
    // COALESCE(worktree_path, workspace_path), upper-cased on Windows. The store computes it on every insert and path
    // update; callers never pass it. Every operation that would add an Active root session (CreateAsync,
    // ActivateAsync, HandoverAsync) is a conditional statement that affects only itself: on conflict it throws
    // WorktreeOccupiedException and does not fail unrelated writes batched with it. Regular queries (QueryAsync,
    // ListSessionsAsync) never return Deleted sessions; ListHistoryAsync and GetSessionAsync do.
    //
    // The defaults below exist so test doubles compile; real stores (and decorators, which must forward) override them.

    /// <summary>
    /// <see cref="SessionStatus.Archived"/> or <see cref="SessionStatus.Deleted"/> to <see cref="SessionStatus.Active"/>,
    /// clearing <c>deleted_at</c>; a Deleted session's Deleted descendants become Archived (readable, not resumed).
    /// Returns false when the session does not exist. Throws <see cref="WorktreeOccupiedException"/> when another Active
    /// root session holds the same key. The only way App code may make an existing session Active again.
    /// </summary>
    Task<bool> ActivateAsync(string sessionId, CancellationToken ct) =>
        throw new NotSupportedException(nameof(ActivateAsync));

    /// <summary>
    /// Single-transaction handover (section 4.4): archive <paramref name="oldSessionId"/> (must still be Active, else
    /// <see cref="SessionNotActiveException"/>), then create or activate the target. Any failure rolls back everything,
    /// leaving the old session Active. Returns the session that is now Active.
    /// </summary>
    Task<SessionInfo> HandoverAsync(string oldSessionId, HandoverTarget target, CancellationToken ct) =>
        throw new NotSupportedException(nameof(HandoverAsync));

    /// <summary>Sets Deleted + <c>deleted_at</c> on the sessions and all their descendants in one transaction; returns how many rows changed.</summary>
    Task<int> SoftDeleteAsync(IReadOnlyList<string> sessionIds, CancellationToken ct) =>
        throw new NotSupportedException(nameof(SoftDeleteAsync));

    /// <summary>
    /// Permanently deletes a Deleted session and its descendants: every <c>forge.db</c> row keyed by their session id
    /// (messages, search rows, summaries, todos, todo groups, plan summaries, turn checkpoints, the session rows).
    /// Throws <see cref="SessionPurgeRefusedException"/> when a session outside the purged set inherits from one of
    /// them, and <see cref="InvalidOperationException"/> when the session is not Deleted. Returns the purged ids (self
    /// first), so the caller can clean files kept outside the database.
    /// </summary>
    Task<IReadOnlyList<string>> PurgeAsync(string sessionId, CancellationToken ct) =>
        throw new NotSupportedException(nameof(PurgeAsync));

    /// <summary>Archived and Deleted sessions matching <paramref name="query"/> (its <see cref="SessionQuery.Status"/> is ignored); newest activity first.</summary>
    Task<IReadOnlyList<SessionInfo>> ListHistoryAsync(SessionQuery query, CancellationToken ct) =>
        throw new NotSupportedException(nameof(ListHistoryAsync));

    /// <summary>
    /// Startup self-heal (section 7.6): every Active/Archived root session whose directory
    /// (<c>COALESCE(worktree_path, workspace_path)</c>) does not exist while its project root does becomes Deleted
    /// (descendants too). Sessions without a registered project, or whose project root is missing, are skipped.
    /// Writes one <see cref="StorageNoticeKinds.HealDeleted"/> notice when anything changed; returns the healed root ids.
    /// </summary>
    Task<IReadOnlyList<string>> HealMissingDirectoriesAsync(
        Func<string, bool> directoryExists,
        Func<string, bool> projectRootExists,
        CancellationToken ct) =>
        throw new NotSupportedException(nameof(HealMissingDirectoriesAsync));

    /// <summary>Records the stable-prefix hash; writes only when it differs from the stored value. Returns true when written.</summary>
    Task<bool> SetPrefixFingerprintAsync(string sessionId, string fingerprint, CancellationToken ct) =>
        throw new NotSupportedException(nameof(SetPrefixFingerprintAsync));

    /// <summary>
    /// Branch rename bookkeeping (section 7.8), one transaction: <c>branch</c> of every session of
    /// <paramref name="projectId"/> whose worktree is <paramref name="worktreePath"/> (occupancy-key comparison), and
    /// <c>fork_of_branch</c> of every session of the project that pointed at <paramref name="oldBranch"/>. Returns rows changed.
    /// </summary>
    Task<int> RenameBranchAsync(string projectId, string worktreePath, string oldBranch, string newBranch, CancellationToken ct) =>
        throw new NotSupportedException(nameof(RenameBranchAsync));

    /// <summary>Unacknowledged storage notices, oldest first.</summary>
    Task<IReadOnlyList<StorageNotice>> ReadNoticesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StorageNotice>>([]);

    Task AcknowledgeNoticesAsync(IReadOnlyList<long> noticeIds, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>
    /// Deletes every artifact of one session: its metadata rows and its content files, nothing of any other session.
    /// Idempotent (a session without artifacts deletes nothing and returns 0). Both parts are attempted even when one
    /// fails; a failure of either is thrown afterwards, so the caller can report it and the call can be repeated.
    /// Returns how many metadata rows were deleted.
    /// </summary>
    Task<int> DeleteSessionAsync(string sessionId, CancellationToken ct) =>
        throw new NotSupportedException(nameof(DeleteSessionAsync));
}
