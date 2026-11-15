using Forge.App.Git;
using Forge.Core.Tools;
using Forge.Workspace;
using CoreOutcome = Forge.Core.Tools.ReviewConclusionOutcome;
using StoredOutcome = Forge.Workspace.ReviewConclusionOutcome;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The Core↔Workspace mapping of <see cref="ReviewConclusionStore"/>: the app keeps no SQL of its own, so a review recorded
/// through Core's <see cref="IReviewConclusionStore"/> must land in <c>reviews.db</c> with every field mapped one for one.
/// The outcome is mapped by name, never by a numeric cast, so an unknown value on either side falls back to
/// <c>Unstructured</c> instead of silently passing a number that a future enum would reinterpret.
/// </summary>
public sealed class ReviewConclusionStoreTests : IDisposable
{
    private readonly string directory;

    public ReviewConclusionStoreTests() => directory = TestTempFiles.NewDirectory("forge-review-conclusion-");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestTempFiles.DeleteTree(directory);
    }

    private static Forge.Core.Tools.NewReviewConclusion New(
        CoreOutcome outcome,
        string? stepId = null,
        string? baseCommit = null,
        string? headCommit = null,
        string text = "the reviewer's own text") => new()
    {
        WorkspacePath = "/work/alpha",
        SessionId = "session-1",
        StepId = stepId,
        Base = baseCommit,
        HeadCommit = headCommit,
        Outcome = outcome,
        Text = text,
    };

    [Theory]
    [InlineData(CoreOutcome.Unstructured, StoredOutcome.Unstructured)]
    [InlineData(CoreOutcome.Approve, StoredOutcome.Approve)]
    [InlineData(CoreOutcome.RequestChanges, StoredOutcome.RequestChanges)]
    [InlineData(CoreOutcome.Stopped, StoredOutcome.Stopped)]
    [InlineData(CoreOutcome.Failed, StoredOutcome.Failed)]
    public async Task Every_outcome_maps_by_name_through_the_store(CoreOutcome core, StoredOutcome stored)
    {
        var reviews = new ReviewStore(directory);
        var store = new ReviewConclusionStore(reviews);

        var returned = await store.AddAsync(New(core));

        Assert.Equal(core, returned.Outcome);
        Assert.Equal(stored, Assert.Single(await reviews.GetConclusionsAsync("session-1")).Outcome);
    }

    [Fact]
    public async Task A_record_round_trips_every_field()
    {
        var reviews = new ReviewStore(directory);
        var store = new ReviewConclusionStore(reviews);

        var returned = await store.AddAsync(New(
            CoreOutcome.RequestChanges,
            stepId: "step-7",
            baseCommit: "0987654321fedcba",
            headCommit: "1234567890abcdef"));

        Assert.True(returned.Id > 0);
        Assert.Equal("/work/alpha", returned.WorkspacePath);
        Assert.Equal("session-1", returned.SessionId);
        Assert.Equal("step-7", returned.StepId);
        Assert.Equal("0987654321fedcba", returned.Base);
        Assert.Equal("1234567890abcdef", returned.HeadCommit);
        Assert.Equal(CoreOutcome.RequestChanges, returned.Outcome);
        Assert.Equal("the reviewer's own text", returned.Text);
        Assert.NotEqual(default, returned.CreatedAt);
    }

    [Fact]
    public async Task Listing_maps_the_sessions_conclusions_newest_first()
    {
        var store = new ReviewConclusionStore(new ReviewStore(directory));

        await store.AddAsync(New(CoreOutcome.Approve, text: "first"));
        await store.AddAsync(New(CoreOutcome.RequestChanges, text: "second"));
        await store.AddAsync(New(CoreOutcome.Failed, text: "elsewhere") with { SessionId = "other" });

        var listed = await store.ListAsync("session-1");

        Assert.Equal(["second", "first"], listed.Select(static row => row.Text).ToArray());
        Assert.Equal(
            [CoreOutcome.RequestChanges, CoreOutcome.Approve],
            listed.Select(static row => row.Outcome).ToArray());
    }

    [Fact]
    public async Task An_unknown_outcome_value_falls_back_to_unstructured_instead_of_passing_the_number_through()
    {
        var reviews = new ReviewStore(directory);
        var store = new ReviewConclusionStore(reviews);

        var returned = await store.AddAsync(New((CoreOutcome)99));

        Assert.Equal(CoreOutcome.Unstructured, returned.Outcome);
        Assert.Equal(StoredOutcome.Unstructured, Assert.Single(await reviews.GetConclusionsAsync("session-1")).Outcome);
    }
}
