using Forge.App.Composition;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins the composition root's shutdown contract: every OS handle it holds is released by <see cref="AppComposition.DisposeAsync"/>.
/// The review store writes to a pooled SQLite connection (<c>reviews.db</c>); a pooled handle still open after teardown
/// would keep the temporary data root locked, and on Windows a locked file makes the recursive delete throw.
/// </summary>
public sealed class CompositionDisposeTests
{
    [Fact]
    public async Task DisposeAsync_releases_the_reviews_database_handle()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-composition-");
        var workspace = Path.Combine(root, "workspace");
        try
        {
            var composition = AppComposition.Create();
            await composition.Initialization;
            await composition.Workspace!.Reviews.AddAsync(
                new NewReviewComment
                {
                    WorkspacePath = workspace,
                    SessionId = "session",
                    FilePath = "alpha.txt",
                    NewLineStart = 1,
                    Body = "body",
                },
                CancellationToken.None);

            await composition.DisposeAsync();
        }
        finally
        {
            previous.Restore();
        }

        DeleteTree(root);
    }

    /// <summary>Resets git's read-only objects, then demands a full recursive delete that a live handle would block.</summary>
    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

}
