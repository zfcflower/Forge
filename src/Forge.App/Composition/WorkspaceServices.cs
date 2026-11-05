using Forge.Workspace;

namespace Forge.App.Composition;

/// <summary>
/// Workspace-layer services: Git, worktrees, the spec graph and review comments. Construction is cheap and does no I/O;
/// the spec graph is only scanned when someone asks for it.
/// </summary>
internal sealed class WorkspaceServices : IDisposable
{
    private WorkspaceServices(
        string workspaceRoot,
        GitCli git,
        WorktreeManager worktrees,
        SpecGraph specs,
        ReviewStore reviews)
    {
        WorkspaceRoot = workspaceRoot;
        Git = git;
        Worktrees = worktrees;
        Specs = specs;
        Reviews = reviews;
    }

    public string WorkspaceRoot { get; }

    public GitCli Git { get; }

    public WorktreeManager Worktrees { get; }

    public SpecGraph Specs { get; }

    public ReviewStore Reviews { get; }

    public static WorkspaceServices Create(AppPaths paths)
    {
        var git = new GitCli();
        return new WorkspaceServices(
            paths.WorkspaceDirectory,
            git,
            new WorktreeManager(git),
            new SpecGraph(paths.WorkspaceDirectory),
            new ReviewStore(Path.Combine(paths.UserDataDirectory, "reviews")));
    }

    /// <summary>
    /// Nothing here can be disposed directly: Git runs one child process per call and waits for it, the spec graph holds
    /// an in-memory index, and <see cref="ReviewStore"/> implements neither <see cref="IDisposable"/> nor
    /// <see cref="IAsyncDisposable"/>. Its pooled SQLite connections are released by the process-wide pool clear the
    /// forge.db stores perform during their own teardown (<see cref="AppComposition.DisposeAsync"/>), so the empty
    /// method is the complete teardown at this layer.
    /// </summary>
    public void Dispose()
    {
    }
}
