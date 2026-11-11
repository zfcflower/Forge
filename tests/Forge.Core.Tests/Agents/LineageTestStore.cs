using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// In-memory session store + catalog that knows the lifecycle columns (inheritance, lineage, origin, fingerprint).
/// Messages round-trip through <see cref="MessageJson"/> like the SQLite store, so a reload is a real deserialization.
/// </summary>
internal sealed class LineageTestStore : ISessionStore, ISessionCatalog
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, SessionInfo> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(long Seq, string Json, DateTimeOffset At)>> messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SummaryRecord>> summaries = new(StringComparer.Ordinal);
    private readonly List<(string SessionId, string Fingerprint)> fingerprintCalls = [];
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    /// <summary>When true, <see cref="SetPrefixFingerprintAsync"/> behaves like the interface default.</summary>
    public bool FingerprintNotSupported { get; set; }

    /// <summary>When true, <see cref="GetSessionAsync"/> throws, standing in for a broken store.</summary>
    public bool FailGetSession { get; set; }

    public IReadOnlyList<(string SessionId, string Fingerprint)> FingerprintCalls
    {
        get
        {
            lock (gate)
            {
                return [.. fingerprintCalls];
            }
        }
    }

    /// <summary>Replaces a stored row as is, for shapes no real API produces (a corrupt, cyclic lineage).</summary>
    public void Overwrite(SessionInfo info)
    {
        lock (gate)
        {
            sessions[info.Id] = info;
        }
    }

    /// <summary>Advances the store clock (starting at the real time, one second per write); every write stamps the current value, so ordering in time is explicit.</summary>
    public DateTimeOffset Tick(TimeSpan? by = null)
    {
        lock (gate)
        {
            now += by ?? TimeSpan.FromSeconds(1);
            return now;
        }
    }

    public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        var at = Tick();
        var info = new SessionInfo(id, request.WorkspacePath, request.Title, at)
        {
            ProjectId = request.ProjectId,
            WorktreePath = request.WorktreePath,
            Branch = request.Branch,
            ParentSessionId = request.ParentSessionId,
            AgentKind = request.AgentKind,
            Task = request.Task,
            LastActiveAt = at,
            ForkOfBranch = request.ForkOfBranch,
            ForkBaseSha = request.ForkBaseSha,
            InheritsFromSessionId = request.InheritsFromSessionId,
            InheritsUpToSeq = request.InheritsUpToSeq,
            LineageRootId = request.LineageRootId ?? id,
            Origin = request.Origin,
        };
        lock (gate)
        {
            sessions[id] = info;
        }

        return Task.FromResult(info);
    }

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
        CreateAsync(new SessionCreate(workspacePath, title), ct);

    /// <summary>A child that fully inherits <paramref name="parent"/> as it stands now (its current last seq).</summary>
    public async Task<SessionInfo> InheritAsync(SessionInfo parent, CancellationToken ct = default)
    {
        var upTo = await LastSeqAsync(parent.Id, ct);
        return await CreateAsync(
            new SessionCreate(parent.WorkspacePath, "child of " + parent.Title)
            {
                InheritsFromSessionId = parent.Id,
                InheritsUpToSeq = upTo,
                LineageRootId = parent.LineageRootId ?? parent.Id,
                Origin = SessionOrigin.Full,
            },
            ct);
    }

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (FailGetSession)
        {
            throw new IOException("session row unavailable");
        }

        lock (gate)
        {
            return Task.FromResult(sessions.GetValueOrDefault(sessionId));
        }
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<SessionInfo>>([.. sessions.Values]);
        }
    }

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        var json = MessageJson.Serialize(message);
        var at = Tick();
        lock (gate)
        {
            if (!messages.TryGetValue(sessionId, out var list))
            {
                list = [];
                messages[sessionId] = list;
            }

            var seq = list.Count + 1L;
            list.Add((seq, json, at));
            return Task.FromResult(seq);
        }
    }

    public async IAsyncEnumerable<StoredMessage> ReadAsync(
        string sessionId,
        long fromSeq,
        long? toSeq,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        (long Seq, string Json, DateTimeOffset At)[] snapshot;
        lock (gate)
        {
            snapshot = messages.TryGetValue(sessionId, out var list)
                ? [.. list.Where(entry => entry.Seq >= fromSeq && entry.Seq <= (toSeq ?? long.MaxValue))]
                : [];
        }

        foreach (var entry in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return new StoredMessage(entry.Seq, MessageJson.Deserialize(entry.Json), null, entry.At);
        }
    }

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(messages.TryGetValue(sessionId, out var list) ? (long)list.Count : 0L);
        }
    }

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct)
    {
        lock (gate)
        {
            if (!summaries.TryGetValue(summary.SessionId, out var list))
            {
                list = [];
                summaries[summary.SessionId] = list;
            }

            list.Add(summary);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<SummaryRecord>>(summaries.TryGetValue(sessionId, out var list) ? [.. list] : []);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>>([]);

    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) => ListSessionsAsync(null, ct);

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> SetPrefixFingerprintAsync(string sessionId, string fingerprint, CancellationToken ct)
    {
        if (FingerprintNotSupported)
        {
            throw new NotSupportedException(nameof(SetPrefixFingerprintAsync));
        }

        lock (gate)
        {
            fingerprintCalls.Add((sessionId, fingerprint));
            var current = sessions.GetValueOrDefault(sessionId);
            if (current is null || string.Equals(current.PrefixFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            sessions[sessionId] = current with { PrefixFingerprint = fingerprint };
            return Task.FromResult(true);
        }
    }
}
