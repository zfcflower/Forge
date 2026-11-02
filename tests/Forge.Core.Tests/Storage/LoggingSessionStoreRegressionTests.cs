using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class LoggingSessionStoreRegressionTests
{
    [Fact]
    public async Task Log_write_failure_on_a_disposed_log_keeps_the_commit_and_raises_log_failed()
    {
        using var directory = new TempDirectory();
        var store = new SqliteSessionStore(directory.Path);
        var log = new JsonlEventLog(directory.Combine("logs"));
        await log.DisposeAsync();
        await using var logging = new LoggingSessionStore(store, log);
        var session = await logging.CreateSessionAsync("D:/ws", "t", default);
        var failures = new List<Exception>();
        logging.LogFailed += failures.Add;

        var first = await logging.AppendAsync(session.Id, Text("kept"), null, default);
        var second = await logging.AppendAsync(session.Id, Text("kept too"), null, default);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure => Assert.IsType<ObjectDisposedException>(failure));
        Assert.Equal(new long[] { 1, 2 }, (await CollectAsync(store.ReadAsync(session.Id, 1, null, default))).Select(m => m.Seq));
    }

    [Fact]
    public async Task Log_directory_that_cannot_be_created_keeps_the_commit_and_raises_log_failed()
    {
        using var directory = new TempDirectory();
        var blocker = directory.Combine("not-a-directory");
        await File.WriteAllTextAsync(blocker, "occupied");
        await using var store = new SqliteSessionStore(directory.Path);
        var log = new JsonlEventLog(blocker);
        await using var logging = new LoggingSessionStore(store, log);
        var session = await logging.CreateSessionAsync("D:/ws", "t", default);
        var failures = new List<Exception>();
        logging.LogFailed += failures.Add;

        var seq = await logging.AppendAsync(session.Id, Text("committed"), null, default);

        Assert.Equal(1, seq);
        Assert.Single(failures);
        Assert.Equal(1, await store.LastSeqAsync(session.Id, default));
    }

    [Fact]
    public async Task Successful_log_write_does_not_raise_log_failed()
    {
        using var directory = new TempDirectory();
        var store = new SqliteSessionStore(directory.Path);
        await using var log = new JsonlEventLog(directory.Combine("logs"));
        await using var logging = new LoggingSessionStore(store, log);
        var session = await logging.CreateSessionAsync("D:/ws", "t", default);
        var failures = 0;
        logging.LogFailed += _ => failures++;

        await logging.AppendAsync(session.Id, Text("fine"), null, default);

        Assert.Equal(0, failures);
    }
}
