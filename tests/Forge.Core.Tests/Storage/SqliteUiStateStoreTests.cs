using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins SqliteUiStateStore: upsert with last-writer-wins, per-key versions, delete and missing keys.</summary>
public sealed class SqliteUiStateStoreTests
{
    [Fact]
    public async Task Put_and_get_round_trip()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteUiStateStore(directory.Path);

        Assert.Null(await store.GetAsync("missing", default));

        await store.PutAsync("layout", 3, """{"panels":["chat"]}""", default);
        var entry = await store.GetAsync("layout", default);
        Assert.NotNull(entry);
        Assert.Equal("layout", entry.Key);
        Assert.Equal(3, entry.Version);
        Assert.Equal("""{"panels":["chat"]}""", entry.Json);
        Assert.True(DateTimeOffset.UtcNow - entry.UpdatedAt < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Put_overwrites_with_last_writer_wins()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteUiStateStore(directory.Path);

        await store.PutAsync("tabs", 1, """{"open":[]}""", default);
        await store.PutAsync("tabs", 2, """{"open":["a"]}""", default);

        var entry = await store.GetAsync("tabs", default);
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Version);
        Assert.Equal("""{"open":["a"]}""", entry.Json);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteUiStateStore(directory.Path);

        await store.PutAsync("a", 1, "{}", default);
        await store.PutAsync("b", 7, "[]", default);

        Assert.Equal(1, (await store.GetAsync("a", default))?.Version);
        Assert.Equal(7, (await store.GetAsync("b", default))?.Version);
    }

    [Fact]
    public async Task Delete_removes_and_reports_missing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteUiStateStore(directory.Path);

        Assert.False(await store.DeleteAsync("missing", default));

        await store.PutAsync("layout", 1, "{}", default);
        Assert.True(await store.DeleteAsync("layout", default));
        Assert.Null(await store.GetAsync("layout", default));
        Assert.False(await store.DeleteAsync("layout", default));
    }
}
