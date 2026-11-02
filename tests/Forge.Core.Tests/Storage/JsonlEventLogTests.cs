using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class JsonlEventLogTests
{
    [Fact]
    public async Task Append_writes_one_line_per_message_with_the_assigned_seq()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        await using var log = new JsonlEventLog(directory.Combine("logs"));
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        var at = DateTimeOffset.UtcNow;

        await log.AppendAsync(session.Id, 1, Text("first"), new TokenUsage(1, 2), at, default);
        await log.AppendAsync(session.Id, 2, ChatMessage.Assistant("second"), null, at.AddSeconds(1), default);
        await log.FlushAsync(default);

        var path = log.GetPath(session.Id);
        Assert.True(File.Exists(path));
        Assert.Equal(2, (await ReadAllLinesAsync(path)).Length);

        var entries = await CollectAsync(log.ReadAsync(session.Id, default));
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Seq);
        Assert.Equal(at, entries[0].At);
        Assert.Equal(new TokenUsage(1, 2), entries[0].Usage);
        Assert.Equal(MessageRole.User, entries[0].Message.Role);
        Assert.Equal(new ContentBlock[] { new TextBlock("first") }, entries[0].Message.Content);
        Assert.Equal(MessageRole.Assistant, entries[1].Message.Role);
        Assert.Null(entries[1].Usage);
    }

    [Fact]
    public async Task Append_from_concurrent_writers_keeps_lines_intact()
    {
        using var directory = new TempDirectory();
        await using var log = new JsonlEventLog(directory.Combine("logs"));
        const string sessionId = "session1";

        var tasks = Enumerable.Range(1, 200).Select(index => Task.Run(
            async () => await log.AppendAsync(sessionId, index, Text($"message {index}"), null, DateTimeOffset.UtcNow, default)));
        await Task.WhenAll(tasks);
        await log.FlushAsync(default);

        var entries = await CollectAsync(log.ReadAsync(sessionId, default));
        Assert.Equal(200, entries.Count);
        Assert.Equal(Enumerable.Range(1, 200).Select(value => (long)value), entries.Select(e => e.Seq).Order());
    }

    [Fact]
    public async Task Read_skips_a_truncated_tail_written_by_a_crash()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        await using var log = new JsonlEventLog(logDirectory);
        const string sessionId = "session1";

        await log.AppendAsync(sessionId, 1, Text("complete"), null, DateTimeOffset.UtcNow, default);
        await log.DisposeAsync();
        await File.AppendAllTextAsync(log.GetPath(sessionId), """{"seq":2,"at":"2024""", default);

        await using var reopened = new JsonlEventLog(logDirectory);
        var entries = await CollectAsync(reopened.ReadAsync(sessionId, default));
        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.Seq);
        Assert.Empty(await CollectAsync(reopened.ReadAsync("missing-session", default)));
    }

    [Fact]
    public async Task Logging_store_writes_both_the_database_and_the_log()
    {
        using var directory = new TempDirectory();
        var store = new SqliteSessionStore(directory.Path);
        var log = new JsonlEventLog(directory.Combine("logs"));
        await using var logging = new LoggingSessionStore(store, log);
        var session = await logging.CreateSessionAsync("D:/ws", "t", default);

        Assert.Equal(1, await logging.AppendAsync(session.Id, Text("one"), null, default));
        Assert.Equal(2, await logging.AppendAsync(session.Id, ChatMessage.Assistant("two"), new TokenUsage(3, 4), default));
        await log.FlushAsync(default);

        var stored = await CollectAsync(logging.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(2, stored.Count);
        var logged = await CollectAsync(log.ReadAsync(session.Id, default));
        Assert.Equal(2, logged.Count);
        Assert.Equal(stored.Select(m => m.Seq), logged.Select(e => e.Seq));
        Assert.Equal(stored[1].Message.Content, logged[1].Message.Content);
        Assert.Equal(new TokenUsage(3, 4), logged[1].Usage);
    }

    [Fact]
    public async Task Recover_reimports_log_entries_lost_by_a_crash()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        string sessionId;
        List<StoredMessage> before;
        var store = new SqliteSessionStore(directory.Path);
        var log = new JsonlEventLog(logDirectory);
        await using (var logging = new LoggingSessionStore(store, log))
        {
            var session = await logging.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            for (var index = 1; index <= 5; index++)
            {
                await logging.AppendAsync(sessionId, Text($"message {index}"), new TokenUsage(index, index), default);
            }

            await log.FlushAsync(default);
            before = await CollectAsync(logging.ReadAsync(sessionId, 1, null, default));
        }

        await ExecuteAsync(store.ConnectionString, $"DELETE FROM messages WHERE session_id = '{sessionId}' AND seq >= 4");

        await using var reopenedStore = new SqliteSessionStore(directory.Path);
        await using var reopenedLog = new JsonlEventLog(logDirectory);
        Assert.Equal(3, await reopenedStore.LastSeqAsync(sessionId, default));

        var recovered = await reopenedLog.RecoverAsync(sessionId, reopenedStore, default);

        Assert.Equal(2, recovered);
        Assert.Equal(5, await reopenedStore.LastSeqAsync(sessionId, default));
        var after = await CollectAsync(reopenedStore.ReadAsync(sessionId, 1, null, default));
        Assert.Equal(before.Select(m => m.Seq), after.Select(m => m.Seq));
        Assert.Equal(
            before.Select(m => ((TextBlock)m.Message.Content[0]).Text),
            after.Select(m => ((TextBlock)m.Message.Content[0]).Text));
        Assert.Equal(0, await reopenedLog.RecoverAsync(sessionId, reopenedStore, default));
    }

    [Fact]
    public async Task Recovery_through_the_logging_store_leaves_the_log_untouched()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        string sessionId;
        var store = new SqliteSessionStore(directory.Path);
        var log = new JsonlEventLog(logDirectory);
        var logging = new LoggingSessionStore(store, log);
        var session = await logging.CreateSessionAsync("D:/ws", "t", default);
        sessionId = session.Id;
        for (var index = 1; index <= 3; index++)
        {
            await logging.AppendAsync(sessionId, Text($"message {index}"), null, default);
        }

        await log.FlushAsync(default);
        var lineCount = (await ReadAllLinesAsync(log.GetPath(sessionId))).Length;
        Assert.Equal(0, await logging.RecoverAsync(sessionId, default));
        Assert.Equal(0, await logging.RecoverAsync(sessionId, default));
        await log.FlushAsync(default);
        Assert.Equal(lineCount, (await ReadAllLinesAsync(log.GetPath(sessionId))).Length);
        await logging.DisposeAsync();
    }
}
