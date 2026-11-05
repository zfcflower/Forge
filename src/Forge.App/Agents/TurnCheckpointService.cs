using System.Diagnostics;
using Forge.Abstractions;
using Forge.Workspace;

namespace Forge.App.Agents;

/// <summary>
/// Records the tree hash before and after every main-session turn through <see cref="GitCli.SnapshotTreeAsync"/>, so a
/// turn's changes are the diff between two trees. The snapshot uses a temporary index inside the workspace layer and
/// therefore never touches the user's index, HEAD or stash. A directory that is not a git repository, or whose snapshot
/// fails, is skipped with a diagnostic line instead of failing the turn.
/// </summary>
internal sealed class TurnCheckpointService
{
    private readonly GitCli git;
    private readonly ITurnCheckpointStore store;
    private readonly Action<string> diagnostic;

    public TurnCheckpointService(GitCli git, ITurnCheckpointStore store, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(store);
        this.git = git;
        this.store = store;
        this.diagnostic = diagnostic ?? (static message => Trace.WriteLine("[checkpoint] " + message));
    }

    /// <summary>Snapshots the tree the turn starts from; cheap enough to await before the model call.</summary>
    public async Task BeginAsync(string sessionId, int turn, string workingDirectory, CancellationToken ct)
    {
        var tree = await SnapshotAsync(workingDirectory, ct).ConfigureAwait(false);
        if (tree is null)
        {
            diagnostic("skipped turn " + turn.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": no git tree in " + workingDirectory);
            return;
        }

        await store.BeginAsync(sessionId, turn, workingDirectory, tree, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The next turn number that has no checkpoint yet. A reopened session continues the numbering of the stored
    /// checkpoints instead of starting over, because a completed turn is never rewritten.
    /// </summary>
    public async Task<int> NextTurnNumberAsync(string sessionId, CancellationToken ct)
    {
        var stored = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        var highest = 0;
        foreach (var checkpoint in stored)
        {
            highest = Math.Max(highest, checkpoint.Turn);
        }

        return highest + 1;
    }

    /// <summary>Snapshots the tree after the turn ended, on success, failure and cancellation alike.</summary>
    public async Task CompleteAsync(string sessionId, int turn, string workingDirectory, CancellationToken ct)
    {
        var tree = await SnapshotAsync(workingDirectory, ct).ConfigureAwait(false);
        if (tree is null)
        {
            return;
        }

        await store.CompleteAsync(sessionId, turn, tree, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    private async Task<string?> SnapshotAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
            {
                return null;
            }

            return await git.SnapshotTreeAsync(workingDirectory, ct).ConfigureAwait(false);
        }
        catch (GitException exception)
        {
            diagnostic("snapshot failed in " + workingDirectory + ": " + exception.Message);
            return null;
        }
    }
}
