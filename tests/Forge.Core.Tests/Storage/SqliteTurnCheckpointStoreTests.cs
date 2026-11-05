using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins SqliteTurnCheckpointStore: begin/complete lifecycle, turn ordering and the
/// overwrite rules for uncompleted versus completed turns.</summary>
public sealed class SqliteTurnCheckpointStoreTests
{
    private static string Tree(char ch) => new(ch, 40);

    [Fact]
    public async Task Begin_complete_and_list_in_turn_order()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTurnCheckpointStore(directory.Path);

        var started = DateTimeOffset.UtcNow;
        await store.BeginAsync("sess-a", 2, "D:/wt", Tree('c'), started, default);
        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('a'), started, default);
        await store.BeginAsync("sess-a", 1, "D:/wt", Tree('b'), started, default);

        var ended = started.AddMinutes(1);
        Assert.True(await store.CompleteAsync("sess-a", 0, Tree('A'), ended, default));

        var list = await store.ListAsync("sess-a", default);
        Assert.Equal([0, 1, 2], list.Select(c => c.Turn));
        Assert.Equal(Tree('A'), list[0].TreeAfter);
        Assert.Equal(ended, list[0].EndedAt);
        Assert.Null(list[1].TreeAfter);
        Assert.Null(list[1].EndedAt);
        Assert.Equal(started, list[2].StartedAt);
        Assert.Equal("D:/wt", list[2].WorktreePath);
        Assert.Empty(await store.ListAsync("sess-other", default));
    }

    [Fact]
    public async Task Complete_without_begin_returns_false()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTurnCheckpointStore(directory.Path);

        Assert.False(await store.CompleteAsync("sess-a", 0, Tree('x'), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task Double_complete_returns_false_the_second_time()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTurnCheckpointStore(directory.Path);

        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('a'), DateTimeOffset.UtcNow, default);
        Assert.True(await store.CompleteAsync("sess-a", 0, Tree('b'), DateTimeOffset.UtcNow, default));
        Assert.False(await store.CompleteAsync("sess-a", 0, Tree('c'), DateTimeOffset.UtcNow, default));

        var checkpoint = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(Tree('b'), checkpoint.TreeAfter);
    }

    [Fact]
    public async Task Begin_on_an_uncompleted_turn_overwrites_tree_before()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTurnCheckpointStore(directory.Path);

        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('a'), DateTimeOffset.UtcNow, default);
        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('b'), DateTimeOffset.UtcNow, default);

        var checkpoint = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(Tree('b'), checkpoint.TreeBefore);
        Assert.Null(checkpoint.TreeAfter);
    }

    [Fact]
    public async Task Begin_on_a_completed_turn_leaves_the_recorded_diff_untouched()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTurnCheckpointStore(directory.Path);

        var started = DateTimeOffset.UtcNow;
        var ended = started.AddMinutes(2);
        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('a'), started, default);
        Assert.True(await store.CompleteAsync("sess-a", 0, Tree('b'), ended, default));

        await store.BeginAsync("sess-a", 0, "D:/wt", Tree('z'), ended.AddMinutes(1), default);

        var checkpoint = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(Tree('a'), checkpoint.TreeBefore);
        Assert.Equal(Tree('b'), checkpoint.TreeAfter);
        Assert.Equal(started, checkpoint.StartedAt);
        Assert.Equal(ended, checkpoint.EndedAt);
    }
}
