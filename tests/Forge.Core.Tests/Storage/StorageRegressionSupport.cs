using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.Core.Tests.Storage;

/// <summary>In-memory <see cref="ISessionStore"/> that records every append so tests can assert what was written.</summary>
internal sealed class RecordingSessionStore : ISessionStore
{
    private readonly List<StoredMessage> _messages = [];

    public List<long> AppendedSeqs { get; } = [];

    public int AppendCount => AppendedSeqs.Count;

    public void Seed(long seq, ChatMessage message) =>
        _messages.Add(new StoredMessage(seq, message, null, DateTimeOffset.UnixEpoch));

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        var seq = _messages.Count == 0 ? 1 : _messages[^1].Seq + 1;
        _messages.Add(new StoredMessage(seq, message, usage, DateTimeOffset.UnixEpoch));
        AppendedSeqs.Add(seq);
        return Task.FromResult(seq);
    }

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(_messages.Count == 0 ? 0L : _messages[^1].Seq);

    public async IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var message in _messages.Where(m => m.Seq >= fromSeq && m.Seq <= (toSeq ?? long.MaxValue)))
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
        }

        await Task.CompletedTask;
    }

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
        throw new NotSupportedException();
}
