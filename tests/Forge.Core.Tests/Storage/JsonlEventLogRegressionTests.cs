using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class JsonlEventLogRegressionTests
{
    private const string SessionId = "s1";
    private static readonly DateTimeOffset At = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Torn_tail_is_cut_before_appending_so_the_new_entry_stays_readable()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, SessionId + ".jsonl");
        File.WriteAllText(path, await LineFor(1, "one") + """{"seq":2,"at":"2024-05-01T1""");

        await using (var log = new JsonlEventLog(logDirectory))
        {
            await log.AppendAsync(SessionId, 3, ChatMessage.User("three"), null, At, default);
            await log.FlushAsync(default);
        }

        Assert.Equal(2, (await ReadAllLinesAsync(path)).Length);
        await using var reader = new JsonlEventLog(logDirectory);
        var entries = await CollectAsync(reader.ReadAsync(SessionId, default));
        Assert.Equal(new long[] { 1, 3 }, entries.Select(e => e.Seq));
        Assert.Equal(new ContentBlock[] { new TextBlock("three") }, entries[1].Message.Content);
    }

    [Fact]
    public async Task Complete_final_line_missing_only_its_newline_is_returned_by_read()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        var second = (await LineFor(2, "two")).TrimEnd('\n');
        File.WriteAllText(Path.Combine(logDirectory, SessionId + ".jsonl"), await LineFor(1, "one") + second);

        await using var log = new JsonlEventLog(logDirectory);
        var entries = await CollectAsync(log.ReadAsync(SessionId, default));

        Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.Seq));
        Assert.Equal(new ContentBlock[] { new TextBlock("two") }, entries[1].Message.Content);
    }

    [Fact]
    public async Task Reopening_for_append_keeps_a_complete_entry_that_lacks_its_final_newline()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        var second = (await LineFor(2, "two")).TrimEnd('\n');
        File.WriteAllText(Path.Combine(logDirectory, SessionId + ".jsonl"), await LineFor(1, "one") + second);

        await using (var log = new JsonlEventLog(logDirectory))
        {
            await log.AppendAsync(SessionId, 3, ChatMessage.User("three"), null, At, default);
            await log.FlushAsync(default);
        }

        await using var reader = new JsonlEventLog(logDirectory);
        var entries = await CollectAsync(reader.ReadAsync(SessionId, default));
        Assert.Equal(new long[] { 1, 2, 3 }, entries.Select(e => e.Seq));
    }

    [Fact]
    public async Task Recovery_with_a_sequence_gap_throws_and_appends_nothing_to_the_store()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, SessionId + ".jsonl"),
            await LineFor(1, "one") + await LineFor(2, "two") + await LineFor(4, "four"));
        var store = new RecordingSessionStore();
        store.Seed(1, ChatMessage.User("one"));

        await using var log = new JsonlEventLog(logDirectory);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => log.RecoverAsync(SessionId, store, default));

        Assert.Contains("expected 3", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.AppendCount);
        Assert.Equal(1, await store.LastSeqAsync(SessionId, default));
    }

    [Fact]
    public async Task Recovery_with_a_gap_at_the_start_appends_nothing()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, SessionId + ".jsonl"),
            await LineFor(1, "one") + await LineFor(3, "three"));
        var store = new RecordingSessionStore();

        await using var log = new JsonlEventLog(logDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => log.RecoverAsync(SessionId, store, default));
        Assert.Equal(0, store.AppendCount);
    }

    [Fact]
    public async Task Repeated_recovery_appends_nothing_after_the_first_pass()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, SessionId + ".jsonl"),
            await LineFor(1, "one") + await LineFor(2, "two") + await LineFor(3, "three"));
        var store = new RecordingSessionStore();

        await using var log = new JsonlEventLog(logDirectory);

        Assert.Equal(3, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(new long[] { 1, 2, 3 }, store.AppendedSeqs);
        Assert.Equal(0, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(0, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(new long[] { 1, 2, 3 }, store.AppendedSeqs);
    }

    [Fact]
    public async Task Log_lines_already_persisted_are_not_appended_again_when_duplicated()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, SessionId + ".jsonl"),
            await LineFor(1, "one") + await LineFor(2, "two") + await LineFor(2, "two") + await LineFor(3, "three"));
        var store = new RecordingSessionStore();
        store.Seed(1, ChatMessage.User("one"));
        store.Seed(2, ChatMessage.User("two"));

        await using var log = new JsonlEventLog(logDirectory);

        Assert.Equal(1, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(new long[] { 3 }, store.AppendedSeqs);
    }

    [Fact]
    public async Task Duplicated_sequence_line_in_the_unrecovered_tail_is_not_appended_twice()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(
            Path.Combine(logDirectory, SessionId + ".jsonl"),
            await LineFor(1, "one") + await LineFor(2, "two") + await LineFor(3, "three") + await LineFor(3, "three"));
        var store = new RecordingSessionStore();
        store.Seed(1, ChatMessage.User("one"));

        await using var log = new JsonlEventLog(logDirectory);

        Assert.Equal(2, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(new long[] { 2, 3 }, store.AppendedSeqs);
    }

    [Fact]
    public async Task Recovery_restores_the_contiguous_tail_exactly_once_and_in_order()
    {
        using var directory = new TempDirectory();
        var logDirectory = directory.Combine("logs");
        Directory.CreateDirectory(logDirectory);
        var lines = new List<string>();
        for (var seq = 1; seq <= 5; seq++)
        {
            lines.Add(await LineFor(seq, $"message {seq}"));
        }

        File.WriteAllText(Path.Combine(logDirectory, SessionId + ".jsonl"), string.Concat(lines));
        var store = new RecordingSessionStore();
        store.Seed(1, ChatMessage.User("message 1"));
        store.Seed(2, ChatMessage.User("message 2"));

        await using var log = new JsonlEventLog(logDirectory);

        Assert.Equal(3, await log.RecoverAsync(SessionId, store, default));
        Assert.Equal(new long[] { 3, 4, 5 }, store.AppendedSeqs);
        var restored = await CollectAsync(store.ReadAsync(SessionId, 1, null, default));
        Assert.Equal(
            Enumerable.Range(1, 5).Select(i => new ContentBlock[] { new TextBlock($"message {i}") }),
            restored.Select(m => m.Message.Content.ToArray()));
    }

    private static async Task<string> LineFor(long seq, string text)
    {
        using var directory = new TempDirectory();
        await using (var log = new JsonlEventLog(directory.Path))
        {
            await log.AppendAsync("line", seq, ChatMessage.User(text), null, At, default);
        }

        return await File.ReadAllTextAsync(Path.Combine(directory.Path, "line.jsonl"));
    }
}
