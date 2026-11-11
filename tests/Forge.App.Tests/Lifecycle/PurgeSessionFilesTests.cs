using Forge.Abstractions;
using Forge.App.Composition;
using Forge.Core.Storage;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// The permanent-delete file cleanup (7.5): the session's artifact rows and folder and its review marks and comments go,
/// another session's stay; one part failing does not stop the other, and every failure is reported.
/// </summary>
public sealed class PurgeSessionFilesTests : IDisposable
{
    private const string Workspace = "D:/ws";
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-purge-files-tests", Guid.NewGuid().ToString("n"));

    public PurgeSessionFilesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not a test failure.
        }
    }

    [Fact]
    public async Task Removes_the_sessions_artifacts_and_reviews_and_nothing_of_another_session()
    {
        await using var artifacts = FileArtifactStore.ForDataDirectory(root);
        var reviews = new ReviewStore(Path.Combine(root, "reviews"));
        var gone = await artifacts.PutAsync("s1", "a", new byte[] { 1 }, default);
        var kept = await artifacts.PutAsync("s2", "b", new byte[] { 2 }, default);
        await SeedReviewsAsync(reviews, "s1");
        await SeedReviewsAsync(reviews, "s2");

        await AppComposition.PurgeSessionFilesAsync(artifacts, reviews, "s1", default);

        Assert.Null(await artifacts.GetInfoAsync(gone.Id, default));
        Assert.False(Directory.Exists(Path.Combine(root, "artifacts", "s1")));
        Assert.Empty(await reviews.GetMarksAsync(Workspace, "s1"));
        Assert.Empty(await reviews.GetDraftsAsync(Workspace, "s1"));
        Assert.NotNull(await artifacts.GetInfoAsync(kept.Id, default));
        Assert.Single(await reviews.GetMarksAsync(Workspace, "s2"));
        Assert.Single(await reviews.GetDraftsAsync(Workspace, "s2"));

        // Repeating the cleanup is harmless.
        await AppComposition.PurgeSessionFilesAsync(artifacts, reviews, "s1", default);
        Assert.NotNull(await artifacts.GetInfoAsync(kept.Id, default));
    }

    [Fact]
    public async Task An_artifact_failure_still_clears_the_reviews_and_is_reported()
    {
        var reviews = new ReviewStore(Path.Combine(root, "reviews"));
        await SeedReviewsAsync(reviews, "s1");

        var failure = await Assert.ThrowsAsync<IOException>(
            () => AppComposition.PurgeSessionFilesAsync(new FailingArtifacts("artifact disk gone"), reviews, "s1", default));

        Assert.Contains("artifact disk gone", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await reviews.GetMarksAsync(Workspace, "s1"));
        Assert.Empty(await reviews.GetDraftsAsync(Workspace, "s1"));
    }

    [Fact]
    public async Task A_review_failure_still_clears_the_artifacts_and_both_failures_are_reported()
    {
        await using var artifacts = FileArtifactStore.ForDataDirectory(root);
        var gone = await artifacts.PutAsync("s1", "a", new byte[] { 1 }, default);
        // A file where the review folder should be: the review store cannot open its database.
        var blocked = Path.Combine(root, "reviews-blocked");
        await File.WriteAllTextAsync(blocked, "not a folder");
        var reviews = new ReviewStore(blocked);

        await Assert.ThrowsAnyAsync<Exception>(() => AppComposition.PurgeSessionFilesAsync(artifacts, reviews, "s1", default));
        Assert.Null(await artifacts.GetInfoAsync(gone.Id, default));
        Assert.False(Directory.Exists(Path.Combine(root, "artifacts", "s1")));

        var both = await Assert.ThrowsAsync<IOException>(
            () => AppComposition.PurgeSessionFilesAsync(new FailingArtifacts("artifact disk gone"), reviews, "s1", default));
        Assert.Contains("artifact disk gone", both.Message, StringComparison.Ordinal);
        Assert.Equal(2, Assert.IsType<AggregateException>(both.InnerException).InnerExceptions.Count);
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_a_partial_failure()
    {
        var reviews = new ReviewStore(Path.Combine(root, "reviews"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppComposition.PurgeSessionFilesAsync(
            new FailingArtifacts(null), reviews, "s1", default));
    }

    private static async Task SeedReviewsAsync(ReviewStore reviews, string sessionId)
    {
        await reviews.SetMarkAsync(Workspace, sessionId, ReviewMarkKind.Viewed, "a.cs", "hash-" + sessionId);
        await reviews.AddAsync(new NewReviewComment
        {
            WorkspacePath = Workspace,
            SessionId = sessionId,
            FilePath = "a.cs",
            NewLineStart = 1,
            Body = "note " + sessionId,
        });
    }

    /// <summary>An artifact store whose delete fails with the given message, or is cancelled when it is null.</summary>
    private sealed class FailingArtifacts(string? message) : IArtifactStore
    {
        public Task<ArtifactInfo> PutAsync(string sessionId, string name, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ArtifactInfo?> GetInfoAsync(string artifactId, CancellationToken ct) => throw new NotSupportedException();

        public Task<ReadOnlyMemory<byte>> ReadAsync(string artifactId, long offset, int? length, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> DeleteSessionAsync(string sessionId, CancellationToken ct) => message is null
            ? Task.FromCanceled<int>(new CancellationToken(canceled: true))
            : Task.FromException<int>(new IOException(message));
    }
}
