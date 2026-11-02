using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class FileArtifactStoreTests
{
    [Fact]
    public async Task Put_and_read_round_trip_content_and_metadata()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var content = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();

        var info = await store.PutAsync("session1", "build.log", content, default);

        Assert.Equal("session1", info.SessionId);
        Assert.Equal("build.log", info.Name);
        Assert.Equal(content.Length, info.SizeBytes);
        Assert.InRange(info.Id.Length, 8, 64);
        Assert.Equal(info, await store.GetInfoAsync(info.Id, default));

        var full = await store.ReadAsync(info.Id, 0, null, default);
        Assert.Equal(content, full.ToArray());

        Assert.Null(await store.GetInfoAsync("0123456789ab", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync("0123456789ab", 0, null, default));
    }

    [Fact]
    public async Task Read_returns_requested_ranges_without_touching_the_rest()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var content = Enumerable.Range(0, 1000).Select(value => (byte)(value % 256)).ToArray();
        var info = await store.PutAsync("session1", "blob", content, default);

        Assert.Equal(content.AsSpan(10, 20).ToArray(), (await store.ReadAsync(info.Id, 10, 20, default)).ToArray());
        Assert.Equal(content.AsSpan(900).ToArray(), (await store.ReadAsync(info.Id, 900, null, default)).ToArray());
        Assert.Equal(content.AsSpan(0, 1).ToArray(), (await store.ReadAsync(info.Id, 0, 1, default)).ToArray());
        Assert.Equal(content.AsSpan(995, 5).ToArray(), (await store.ReadAsync(info.Id, 995, 500, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 1000, 10, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 5000, null, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 0, 0, default)).ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(info.Id, -1, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(info.Id, 0, -1, default));
    }

    [Fact]
    public async Task Artifacts_are_persisted_per_session_and_reopen_cleanly()
    {
        using var directory = new TempDirectory();
        string id;
        await using (var store = FileArtifactStore.ForDataDirectory(directory.Path))
        {
            id = (await store.PutAsync("session1", "one", new byte[] { 1, 2, 3 }, default)).Id;
            await store.PutAsync("session2", "two", new byte[] { 4, 5, 6 }, default);
        }

        Assert.True(File.Exists(Path.Combine(directory.Path, "artifacts", "session1", id)));

        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await reopened.GetInfoAsync(id, default);
        Assert.NotNull(info);
        Assert.Equal("session1", info.SessionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await reopened.ReadAsync(id, 0, null, default)).ToArray());
    }

    [Fact]
    public async Task Put_assigns_distinct_ids_and_handles_empty_content()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        var first = await store.PutAsync("session1", "a", ReadOnlyMemory<byte>.Empty, default);
        var second = await store.PutAsync("session1", "b", new byte[] { 9 }, default);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, first.SizeBytes);
        Assert.Empty((await store.ReadAsync(first.Id, 0, null, default)).ToArray());
        Assert.Equal(new byte[] { 9 }, (await store.ReadAsync(second.Id, 0, null, default)).ToArray());
    }

    [Fact]
    public async Task Artifact_metadata_shares_the_session_database()
    {
        using var directory = new TempDirectory();
        await using var sessions = new SqliteSessionStore(directory.Path);
        await using var artifacts = new FileArtifactStore(directory.Combine("artifacts"), sessions.ConnectionString);
        var session = await sessions.CreateSessionAsync("D:/ws", "t", default);

        var info = await artifacts.PutAsync(session.Id, "out.txt", "hello"u8.ToArray(), default);

        Assert.Equal("hello"u8.ToArray(), (await artifacts.ReadAsync(info.Id, 0, null, default)).ToArray());
        Assert.Equal(0, await sessions.LastSeqAsync(session.Id, default));
        Assert.NotNull(await sessions.GetSessionAsync(session.Id, default));
    }
}
