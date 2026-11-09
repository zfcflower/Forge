using Xunit;

namespace Forge.Workspace.Tests.Review;

public sealed class ReviewMarksTests
{
    private const string Ws = "/work/alpha";

    [Fact]
    public async Task Set_is_idempotent_and_normalizes_paths()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "src\\a.cs", "h1");
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "src/a.cs", "h1");

        var mark = Assert.Single(await store.GetMarksAsync(Ws, "s1"));
        Assert.Equal("src/a.cs", mark.FilePath);
        Assert.Equal("h1", mark.ItemHash);
        Assert.Equal(ReviewMarkKind.Viewed, mark.Kind);
        Assert.Equal(Ws, mark.WorkspacePath);
        Assert.Equal("s1", mark.SessionId);
    }

    [Fact]
    public async Task Kinds_and_hashes_are_distinct_marks()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1");
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Kept, "a.cs", "h1");
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h2");

        Assert.Equal(3, (await store.GetMarksAsync(Ws, "s1")).Count);
    }

    [Fact]
    public async Task Clear_removes_only_the_requested_mark_and_reports_absence()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1");
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "b.cs", "h1");

        Assert.True(await store.ClearMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1"));
        Assert.False(await store.ClearMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1"));
        Assert.False(await store.ClearMarkAsync(Ws, "s1", ReviewMarkKind.Kept, "b.cs", "h1"));

        var left = Assert.Single(await store.GetMarksAsync(Ws, "s1"));
        Assert.Equal("b.cs", left.FilePath);
    }

    [Fact]
    public async Task Marks_are_isolated_by_session_and_workspace()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1");
        await store.SetMarkAsync(Ws, "s2", ReviewMarkKind.Viewed, "a.cs", "h1");
        await store.SetMarkAsync("/work/beta", "s1", ReviewMarkKind.Viewed, "a.cs", "h1");

        Assert.True(await store.ClearMarkAsync(Ws, "s2", ReviewMarkKind.Viewed, "a.cs", "h1"));

        Assert.Empty(await store.GetMarksAsync(Ws, "s2"));
        Assert.Single(await store.GetMarksAsync(Ws, "s1"));
        Assert.Single(await store.GetMarksAsync("/work/beta", "s1"));
        Assert.Empty(await store.GetMarksAsync(Ws, "unknown"));
    }

    [Fact]
    public async Task Batch_inserts_all_items_and_tolerates_duplicates_and_empty_input()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);

        await store.SetMarksAsync(Ws, "s1", ReviewMarkKind.Viewed, []);
        Assert.Empty(await store.GetMarksAsync(Ws, "s1"));

        await store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1");
        await store.SetMarksAsync(Ws, "s1", ReviewMarkKind.Viewed, [("a.cs", "h1"), ("b\\c.cs", "h2"), ("b/c.cs", "h2"), ("d.cs", "h3")]);

        var marks = await store.GetMarksAsync(Ws, "s1");
        Assert.Equal(["a.cs", "b/c.cs", "d.cs"], marks.Select(static m => m.FilePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Marks_survive_a_new_store_instance_on_the_same_directory()
    {
        using var sandbox = new TempDirectory();
        await new ReviewStore(sandbox.Path).SetMarkAsync(Ws, "s1", ReviewMarkKind.Kept, "a.cs", "h1");

        var mark = Assert.Single(await new ReviewStore(sandbox.Path).GetMarksAsync(Ws, "s1"));
        Assert.Equal(ReviewMarkKind.Kept, mark.Kind);
    }

    [Fact]
    public async Task Cancelled_token_aborts_without_writing()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetMarkAsync(Ws, "s1", ReviewMarkKind.Viewed, "a.cs", "h1", cts.Token));
        Assert.Empty(await store.GetMarksAsync(Ws, "s1"));
    }
}
