namespace Forge.Abstractions;

/// <summary>How a session's context began (<c>sessions.origin_kind</c>).</summary>
public enum SessionOrigin
{
    /// <summary>A fresh session with no carried-over context (also every session stored before v9).</summary>
    Cold,

    /// <summary>Full inheritance: the history of <see cref="SessionInfo.InheritsFromSessionId"/> by reference.</summary>
    Full,

    /// <summary>Started from a persisted handover summary message.</summary>
    Summary,

    /// <summary>Handover / fork with an empty context.</summary>
    Blank,

    /// <summary>An existing session that was resumed (continue / restore); recorded for diagnostics only.</summary>
    Resume,
}

/// <summary>An Active root session already holds the occupancy key (worktree) the operation needed.</summary>
public sealed class WorktreeOccupiedException : InvalidOperationException
{
    public WorktreeOccupiedException(string holderSessionId)
        : base($"The workspace is already occupied by session {holderSessionId}.")
    {
        HolderSessionId = holderSessionId;
    }

    public string HolderSessionId { get; }
}

/// <summary>A handover found its old session no longer Active (another path already archived or removed it).</summary>
public sealed class SessionNotActiveException : InvalidOperationException
{
    public SessionNotActiveException(string sessionId)
        : base($"Session {sessionId} is no longer active.")
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
}

/// <summary>A purge was refused because other sessions inherit history from the purged ones.</summary>
public sealed class SessionPurgeRefusedException : InvalidOperationException
{
    public SessionPurgeRefusedException(IReadOnlyList<string> inheritingSessionIds)
        : base($"{inheritingSessionIds.Count} session(s) still inherit from this session.")
    {
        InheritingSessionIds = inheritingSessionIds;
    }

    public IReadOnlyList<string> InheritingSessionIds { get; }
}

/// <summary>What <see cref="ISessionCatalog.HandoverAsync"/> makes Active after archiving the old session.</summary>
public abstract record HandoverTarget
{
    private HandoverTarget()
    {
    }

    /// <summary>
    /// A new root session. The store copies <c>fork_of_branch</c>, <c>fork_base_sha</c> and <c>lineage_root_id</c>
    /// from the old session (the values in <see cref="Create"/> are ignored for those three) and writes
    /// <see cref="InitialMessages"/> (0-2 messages: handover summary and / or inheritance note) in the same transaction.
    /// <see cref="CopyTodosAndPlans"/> copies todos, todo groups, the plan summary of the old session and the history of
    /// plan summaries it replaced (newest first, the same order the panel reads them in).
    /// </summary>
    public sealed record NewSession(SessionCreate Create) : HandoverTarget
    {
        public IReadOnlyList<ChatMessage> InitialMessages { get; init; } = [];

        public bool CopyTodosAndPlans { get; init; }
    }

    /// <summary>An existing Archived / Deleted session becomes Active again; nothing is copied (same row).</summary>
    public sealed record Resume(string SessionId) : HandoverTarget;
}

/// <summary>Kinds of <see cref="StorageNotice"/>.</summary>
public static class StorageNoticeKinds
{
    /// <summary>The v5 migration archived duplicate Active root sessions of one workspace.</summary>
    public const string DedupeArchived = "dedupe-archived";

    /// <summary>Startup self-heal soft-deleted sessions whose directory is gone.</summary>
    public const string HealDeleted = "heal-deleted";
}

/// <summary>
/// One row of <c>storage_notices</c>: something the storage layer changed on its own that the user must hear about.
/// <paramref name="PayloadJson"/> is <c>{"sessionIds":[...]}</c> for both current kinds; <paramref name="SessionIds"/>
/// is that list, parsed.
/// </summary>
public sealed record StorageNotice(
    long Id,
    string Kind,
    IReadOnlyList<string> SessionIds,
    string PayloadJson,
    DateTimeOffset CreatedAt);
