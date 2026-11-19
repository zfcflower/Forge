using System.Diagnostics;
using Forge.Abstractions;
using Forge.Core.Storage;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

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
    private readonly ITurnMetricsStore metrics;
    private readonly Action<string>? diagnostic;

    public TurnCheckpointService(
        GitCli git,
        ITurnCheckpointStore store,
        ITurnMetricsStore metrics,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(metrics);
        this.git = git;
        this.store = store;
        this.metrics = metrics;
        this.diagnostic = diagnostic;
    }

    /// <summary>An injected diagnostic gets every line; otherwise a lost checkpoint is a Warning and a skip is Information.</summary>
    private void Report(bool failure, string message)
    {
        if (diagnostic is not null)
        {
            diagnostic(message);
        }
        else if (failure)
        {
            AppLogs.Checkpoint.ZLogWarning($"{message}");
        }
        else
        {
            AppLogs.Checkpoint.ZLogInformation($"{message}");
        }
    }

    /// <summary>Snapshots the tree the turn starts from; cheap enough to await before the model call.</summary>
    public async Task BeginAsync(string sessionId, int turn, string workingDirectory, CancellationToken ct)
    {
        var tree = await SnapshotAsync(workingDirectory, ct).ConfigureAwait(false);
        if (tree is null)
        {
            Report(false, "skipped turn " + turn.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": no git tree in " + workingDirectory);
            return;
        }

        await store.BeginAsync(sessionId, turn, workingDirectory, tree, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The next turn number neither table has yet: the highest recorded turn of <c>turn_checkpoints</c> and of
    /// <c>turn_metrics</c>, plus one. A session without a git worktree records no checkpoint at all, so its reopened
    /// numbering continues from the metrics table instead of starting over at 1 — and a session with both continues from
    /// whichever got further (a crashed turn can leave a metrics row with no checkpoint, and a turn cancelled before its
    /// first request the other way round).
    /// </summary>
    public async Task<int> NextTurnNumberAsync(string sessionId, CancellationToken ct)
    {
        var stored = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        var highest = 0;
        foreach (var checkpoint in stored)
        {
            highest = Math.Max(highest, checkpoint.Turn);
        }

        highest = Math.Max(highest, await metrics.MaxTurnAsync(sessionId, ct).ConfigureAwait(false));
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
            Report(true, "snapshot failed in " + workingDirectory + ": " + exception.Message);
            return null;
        }
    }
}
