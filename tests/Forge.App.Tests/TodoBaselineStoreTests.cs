using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

public sealed class TodoBaselineStoreTests : IDisposable
{
    private const string Session = "s1";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-todo-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<TodoItem> Reload(SqliteTodoStore store, string id) =>
        (await store.ListAsync(Session, CancellationToken.None)).Single(i => i.Id == id);

    [Fact]
    public async Task BaselineTree_round_trips_null_keeps_and_empty_clears()
    {
        await using var store = new SqliteTodoStore(dir);
        var item = await store.AddAsync(Session, "step", null, null, CancellationToken.None);
        Assert.Null(item.BaselineTree);

        await store.UpdateAsync(item.Id, null, TodoStatus.InProgress, null, new TodoStepFields(BaselineTree: "abc"), CancellationToken.None);
        Assert.Equal("abc", (await Reload(store, item.Id)).BaselineTree);

        await store.UpdateAsync(item.Id, "renamed", null, null, new TodoStepFields(Summary: "s"), CancellationToken.None);
        Assert.Equal("abc", (await Reload(store, item.Id)).BaselineTree);

        await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(BaselineTree: ""), CancellationToken.None);
        Assert.Null((await Reload(store, item.Id)).BaselineTree);
    }
}
