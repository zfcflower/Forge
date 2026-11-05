using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public sealed class LoggingSessionStorePinTests
{
    private static (TempDirectory Directory, string LogDir) Paths()
    {
        var directory = new TempDirectory();
        return (directory, directory.Combine("logs"));
    }

    [Fact]
    public async Task Forwards_session_lifecycle_to_the_inner_store()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            await using var inner = new SqliteSessionStore(paths.Path);
            await using var log = new JsonlEventLog(logDir);
            await using var store = new LoggingSessionStore(inner, log);

            var created = await store.CreateSessionAsync("D:/ws", "t", default);
            Assert.Equal(created, await store.GetSessionAsync(created.Id, default));
            Assert.Equal(created, Assert.Single(await store.ListSessionsAsync(null, default)));
            Assert.Equal(0, await store.LastSeqAsync(created.Id, default));
        }
    }

    [Fact]
    public async Task Forwards_appends_reads_summaries_and_search()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            await using var inner = new SqliteSessionStore(paths.Path);
            await using var log = new JsonlEventLog(logDir);
            await using var store = new LoggingSessionStore(inner, log);

            var session = await store.CreateSessionAsync("D:/ws", "t", default);
            Assert.Equal(1, await store.AppendAsync(session.Id, Text("hello world"), new TokenUsage(2, 3), default));
            Assert.Equal(1, await store.LastSeqAsync(session.Id, default));

            var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
            var message = Assert.Single(stored);
            Assert.Equal(new TokenUsage(2, 3), message.Usage);

            await store.PutSummaryAsync(new SummaryRecord(session.Id, 1, 1, 1, "summary", DateTimeOffset.UtcNow), default);
            Assert.Single(await store.GetSummariesAsync(session.Id, default));
            Assert.NotEmpty(await store.SearchAsync(session.Id, "hello", 10, default));
        }
    }

    [Fact]
    public async Task Recover_flushes_log_lines_into_the_inner_store()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            await using var inner = new SqliteSessionStore(paths.Path);
            await using var log = new JsonlEventLog(logDir);
            await using var store = new LoggingSessionStore(inner, log);

            var session = await store.CreateSessionAsync("D:/ws", "t", default);
            await store.AppendAsync(session.Id, Text("logged line"), null, default);
            Assert.Equal(0, await store.RecoverAsync(session.Id, default));

            // A log line that never reached the inner store (e.g. crash between the two
            // writes) is re-imported on recovery.
            await log.AppendAsync(session.Id, 2, Text("orphaned line"), null, DateTimeOffset.UtcNow, default);
            await log.FlushAsync(session.Id, default);
            Assert.Equal(1, await store.RecoverAsync(session.Id, default));
            var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
            Assert.Equal(2, stored.Count);
            Assert.Equal("orphaned line", ((TextBlock)stored[1].Message.Content[0]).Text);
        }
    }

    [Fact]
    public void Exposes_inner_and_log_and_disposes_both()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            var inner = new SqliteSessionStore(paths.Path);
            var log = new JsonlEventLog(logDir);
            var store = new LoggingSessionStore(inner, log);
            Assert.Same(inner, store.Inner);
            Assert.Same(log, store.Log);
            store.Dispose();
        }
    }

    [Fact]
    public void SupportsCatalog_is_true_over_a_catalog_inner()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            var store = new LoggingSessionStore(new SqliteSessionStore(paths.Path), new JsonlEventLog(logDir));
            try
            {
                Assert.True(store.SupportsCatalog);
                Assert.NotNull((store as object) as ISessionCatalog);
            }
            finally
            {
                store.Dispose();
            }
        }
    }

    [Fact]
    public async Task Catalog_members_forward_to_the_inner_store()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            await using var inner = new SqliteSessionStore(paths.Path);
            await using var log = new JsonlEventLog(logDir);
            await using var store = new LoggingSessionStore(inner, log);
            ISessionCatalog catalog = store;

            var created = await catalog.CreateAsync(new SessionCreate("D:/ws", "t")
            {
                ProjectId = "proj-1",
                WorktreePath = "D:/wt/1",
                Branch = "feature/x",
                AgentKind = "Explore",
                Task = "explore",
            }, default);
            Assert.Equal(created, await inner.GetSessionAsync(created.Id, default));
            Assert.Equal(created, Assert.Single(await catalog.QueryAsync(new SessionQuery { ProjectId = "proj-1" }, default)));

            var at = DateTimeOffset.UtcNow.AddMinutes(5);
            await catalog.TouchAsync(created.Id, at, default);
            Assert.Equal(at, (await inner.GetSessionAsync(created.Id, default))?.LastActiveAt);

            Assert.True(await catalog.SetStatusAsync(created.Id, SessionStatus.Archived, default));
            Assert.Equal(SessionStatus.Archived, (await inner.GetSessionAsync(created.Id, default))?.Status);

            Assert.True(await catalog.RenameAsync(created.Id, "renamed", default));
            Assert.Equal("renamed", (await inner.GetSessionAsync(created.Id, default))?.Title);

            Assert.False(await catalog.SetStatusAsync(Guid.NewGuid().ToString("n"), SessionStatus.Active, default));
            Assert.False(await catalog.RenameAsync(Guid.NewGuid().ToString("n"), "x", default));
        }
    }

    [Fact]
    public async Task Non_catalog_inner_reports_no_catalog_and_catalog_members_throw()
    {
        using var directory = new TempDirectory();
        var (paths, logDir) = Paths();
        using (paths)
        {
            await using var log = new JsonlEventLog(logDir);
            var store = new LoggingSessionStore(new RecordingSessionStore(), log);
            try
            {
                Assert.False(store.SupportsCatalog);
                Assert.Throws<InvalidOperationException>(() => _ = store.Catalog);

                ISessionCatalog catalog = store;
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.CreateAsync(new SessionCreate("D:/ws", "t"), default));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.QueryAsync(new SessionQuery(), default));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.SetStatusAsync("abc", SessionStatus.Active, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.RenameAsync("abc", "t", default));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.TouchAsync("abc", DateTimeOffset.UtcNow, default));
            }
            finally
            {
                store.Dispose();
            }
        }
    }
}
