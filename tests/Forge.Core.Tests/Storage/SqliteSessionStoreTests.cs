using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class SqliteSessionStoreTests
{
    [Fact]
    public async Task Sessions_round_trip_and_filter_by_workspace()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);

        var first = await store.CreateSessionAsync("D:/ws/a", "first", default);
        var second = await store.CreateSessionAsync("D:/ws/b", "second", default);

        var loaded = await store.GetSessionAsync(first.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(first, loaded);
        Assert.Equal("first", loaded.Title);

        var filtered = await store.ListSessionsAsync("D:/ws/a", default);
        Assert.Single(filtered);
        Assert.Equal(first.Id, filtered[0].Id);

        var all = await store.ListSessionsAsync(null, default);
        Assert.Equal(2, all.Count);
        Assert.Contains(second.Id, all.Select(s => s.Id));
        Assert.Null(await store.GetSessionAsync(Guid.NewGuid().ToString("n"), default));
    }

    [Fact]
    public async Task Append_assigns_monotonic_sequences_starting_at_one()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        Assert.Equal(0, await store.LastSeqAsync(session.Id, default));

        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(index + 1, await store.AppendAsync(session.Id, Text($"m{index}"), null, default));
        }

        Assert.Equal(5, await store.LastSeqAsync(session.Id, default));
        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, stored.Select(m => m.Seq));
    }

    [Fact]
    public async Task Append_sequences_are_unique_under_concurrency()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        const int workers = 8;
        const int perWorker = 25;
        var sequences = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            for (var index = 0; index < perWorker; index++)
            {
                var seq = await store.AppendAsync(session.Id, Text($"w{worker}-{index}"), null, default);
                sequences.Add(seq);
            }
        }));

        await Task.WhenAll(tasks);

        var ordered = sequences.OrderBy(value => value).ToArray();
        Assert.Equal(workers * perWorker, ordered.Length);
        Assert.Equal(Enumerable.Range(1, workers * perWorker).Select(value => (long)value), ordered);
        Assert.Equal(workers * perWorker, await store.LastSeqAsync(session.Id, default));
        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(workers * perWorker, stored.Count);
        Assert.Equal(Enumerable.Range(1, workers * perWorker).Select(value => (long)value), stored.Select(m => m.Seq));
    }

    [Fact]
    public async Task Append_round_trips_every_block_type_and_usage()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        var message = new ChatMessage(MessageRole.Assistant,
        [
            new TextBlock("hello \"world\"\nline2"),
            new ThinkingBlock("reasoning", "sig-1"),
            new ToolCallBlock("call-1", "read_file", """{"path":"a.cs"}"""),
            new ToolResultBlock("call-1", "file contents", IsError: true),
            new ArtifactRefBlock("artifact-1", "large output", 1234),
        ]);
        var usage = new TokenUsage(11, 22, 33, 44);

        await store.AppendAsync(session.Id, message, usage, default);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("plain"), null, default);

        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(2, stored.Count);

        Assert.Equal(MessageRole.Assistant, stored[0].Message.Role);
        Assert.Equal(message.Content, stored[0].Message.Content);
        Assert.Equal(usage, stored[0].Usage);
        Assert.Equal(new ThinkingBlock("reasoning", "sig-1"), stored[0].Message.Content[1]);
        Assert.Equal(new ToolResultBlock("call-1", "file contents", true), stored[0].Message.Content[3]);
        Assert.Equal(new ArtifactRefBlock("artifact-1", "large output", 1234), stored[0].Message.Content[4]);

        Assert.Equal(MessageRole.Assistant, stored[1].Message.Role);
        Assert.Null(stored[1].Usage);
        Assert.Equal(new ContentBlock[] { new TextBlock("plain") }, stored[1].Message.Content);
    }

    [Fact]
    public async Task Append_preserves_every_role()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        foreach (var role in Enum.GetValues<MessageRole>())
        {
            await store.AppendAsync(session.Id, new ChatMessage(role, [new TextBlock(role.ToString())]), null, default);
        }

        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(Enum.GetValues<MessageRole>(), stored.Select(m => m.Message.Role));
    }

    [Fact]
    public async Task Read_returns_inclusive_ranges()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        for (var index = 0; index < 10; index++)
        {
            await store.AppendAsync(session.Id, Text($"m{index}"), null, default);
        }

        var middle = await CollectAsync(store.ReadAsync(session.Id, 3, 5, default));
        Assert.Equal(new long[] { 3, 4, 5 }, middle.Select(m => m.Seq));

        var tail = await CollectAsync(store.ReadAsync(session.Id, 8, null, default));
        Assert.Equal(new long[] { 8, 9, 10 }, tail.Select(m => m.Seq));

        var single = await CollectAsync(store.ReadAsync(session.Id, 4, 4, default));
        Assert.Single(single);
        Assert.Equal("m3", ((TextBlock)single[0].Message.Content[0]).Text);

        Assert.Empty(await CollectAsync(store.ReadAsync(session.Id, 11, null, default)));
        Assert.Empty(await CollectAsync(store.ReadAsync(session.Id, 6, 5, default)));
    }

    [Fact]
    public async Task Summaries_round_trip_in_layer_order()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        var at = DateTimeOffset.UtcNow;

        await store.PutSummaryAsync(new SummaryRecord(session.Id, 2, 1, 10, "session", at), default);
        await store.PutSummaryAsync(new SummaryRecord(session.Id, 1, 11, 20, "stage-b", at.AddMinutes(2)), default);
        await store.PutSummaryAsync(new SummaryRecord(session.Id, 1, 21, 30, "stage-c", at.AddMinutes(3)), default);

        var summaries = await store.GetSummariesAsync(session.Id, default);
        Assert.Equal(3, summaries.Count);
        Assert.Equal(2, summaries[0].Layer);
        Assert.Equal("session", summaries[0].Text);
        Assert.Equal(new SummaryRecord(session.Id, 2, 1, 10, "session", summaries[0].At), summaries[0]);
        Assert.Equal(new[] { "stage-b", "stage-c" }, summaries.Skip(1).Select(s => s.Text));
        Assert.Empty(await store.GetSummariesAsync(Guid.NewGuid().ToString("n"), default));
    }

    [Fact]
    public async Task Search_finds_text_of_compacted_away_messages()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        var other = await store.CreateSessionAsync("D:/ws/other", "other", default);

        await store.AppendAsync(session.Id, Text("the kumquat harvest happened early"), null, default);
        await store.AppendAsync(session.Id, new ChatMessage(MessageRole.Tool,
            [new ToolResultBlock("call-9", "kumquat output line")]), null, default);
        await store.AppendAsync(other.Id, Text("kumquat belongs to another session"), null, default);
        await store.AppendAsync(session.Id, Text("nothing relevant"), null, default);

        await ExecuteAsync(store.ConnectionString, $"DELETE FROM messages WHERE session_id = '{session.Id}' AND seq <= 2");

        var hits = await store.SearchAsync(session.Id, "kumquat", 10, default);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new long[] { 1, 2 }, hits.Select(h => h.Seq).Order());
        Assert.All(hits, hit => Assert.Contains("kumquat", hit.Snippet, StringComparison.OrdinalIgnoreCase));
        Assert.All(hits, hit => Assert.True(hit.Score > 0));

        Assert.Empty(await store.SearchAsync(session.Id, "kumquat", 0, default));
        Assert.Empty(await store.SearchAsync(session.Id, "  ", 10, default));
        Assert.Empty(await store.SearchAsync(session.Id, "!!", 10, default));
        Assert.Single(await store.SearchAsync(session.Id, "kumquat", 1, default));
        Assert.Empty(await store.SearchAsync(Guid.NewGuid().ToString("n"), "kumquat", 10, default));
    }

    [Fact]
    public async Task Search_matches_all_terms_and_reports_fts_support()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        await store.AppendAsync(session.Id, Text("alpha beta gamma"), null, default);
        await store.AppendAsync(session.Id, Text("alpha delta"), null, default);

        var both = await store.SearchAsync(session.Id, "alpha beta", 10, default);
        Assert.Single(both);
        Assert.Equal(1, both[0].Seq);

        var either = await store.SearchAsync(session.Id, "gamma", 10, default);
        Assert.Single(either);

        Assert.True(store.Fts5Available);
    }

    [Fact]
    public async Task State_survives_reopening_the_store()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var store = new SqliteSessionStore(directory.Path))
        {
            var session = await store.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            await store.AppendAsync(sessionId, Text("persisted"), new TokenUsage(1, 2), default);
            await store.PutSummaryAsync(new SummaryRecord(sessionId, 1, 1, 1, "sum", DateTimeOffset.UtcNow), default);
        }

        await using var reopened = new SqliteSessionStore(directory.Path);
        Assert.Equal(1, await reopened.LastSeqAsync(sessionId, default));
        var stored = await CollectAsync(reopened.ReadAsync(sessionId, 1, null, default));
        Assert.Single(stored);
        Assert.Equal(new TokenUsage(1, 2), stored[0].Usage);
        Assert.Single(await reopened.GetSummariesAsync(sessionId, default));
        Assert.NotNull(await reopened.GetSessionAsync(sessionId, default));
    }

    [Fact]
    public async Task Connection_string_constructor_uses_the_given_database()
    {
        using var directory = new TempDirectory();
        var builder = directory.ConnectionString;
        builder.Pooling = true;
        await using (var store = new SqliteSessionStore(builder))
        {
            var session = await store.CreateSessionAsync("D:/ws", "t", default);
            await store.AppendAsync(session.Id, Text("x"), null, default);
            Assert.Equal(1, await store.LastSeqAsync(session.Id, default));
        }

        Assert.True(File.Exists(Path.Combine(directory.Path, "forge.db")));
    }

    [Fact]
    public async Task Disposed_store_rejects_writes()
    {
        using var directory = new TempDirectory();
        var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        await store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.AppendAsync(session.Id, Text("x"), null, default));
    }

    [Fact]
    public async Task Cancelled_append_does_not_assign_a_sequence()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AppendAsync(session.Id, Text("x"), null, cancellation.Token));
        Assert.Equal(0, await store.LastSeqAsync(session.Id, default));
    }
}
