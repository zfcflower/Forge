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
        var root = Path.Combine(Path.GetTempPath(), "forge-composition-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var general = Path.Combine(root, "general");
        foreach (var directory in new[] { data, workspace, general })
        {
            Directory.CreateDirectory(directory);
        }

        var previous = EnvironmentSnapshot.Capture();
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);

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

    /// <summary>The data-root variables the composition root reads, captured so a test leaves no trace.</summary>
    private readonly record struct EnvironmentSnapshot(string DataRoot, string Workspace, string GeneralWorkspace)
    {
        public static EnvironmentSnapshot Capture() =>
            new(
                Environment.GetEnvironmentVariable(AppPaths.DataVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.GeneralVariable) ?? string.Empty);

        public void Restore()
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, DataRoot);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, GeneralWorkspace);
        }
    }
}
