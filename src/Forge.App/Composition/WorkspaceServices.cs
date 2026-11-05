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
    /// Nothing at this layer owns an OS handle: Git runs one child process per call and waits for it, the spec graph
    /// holds an in-memory index and the review store opens a pooled connection per call.
    /// </summary>
    public void Dispose()
    {
    }
}
