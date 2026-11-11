using Forge.Core.Storage;

namespace Forge.App.Composition;

/// <summary>
/// The process-level workspace states of section 2, keyed by the occupancy key of the workspace path (so a trailing
/// separator or a case variant on Windows is the same workspace): "operation in progress" (merge back, sync, remove,
/// branch rename), set and cleared by the flow that runs it, and "merge conflict", a snapshot the tree refresh writes
/// from its background read of <c>MERGE_HEAD</c>. Readers (the turn-start check, the tree's menus) only read the snapshot;
/// nothing here runs git. Thread-safe: the refresh writes from the thread pool, the UI reads.
/// </summary>
internal sealed class WorkspaceStates
{
    private readonly Lock gate = new();
    private readonly HashSet<string> operating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private HashSet<string> conflicted = new(StringComparer.Ordinal);

    /// <summary>Marks the workspace as busy with an operation; false when one is already running there.</summary>
    public bool TryBeginOperation(string workspacePath)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            if (!operating.Add(key))
            {
                return false;
            }

            Bump(key);
            return true;
        }
    }

    public void EndOperation(string workspacePath)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            operating.Remove(key);
        }
    }

    public bool IsOperating(string workspacePath)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            return operating.Contains(key);
        }
    }

    public bool IsConflicted(string workspacePath)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            return conflicted.Contains(key);
        }
    }

    /// <summary>Sets one workspace's merge-conflict flag (a refresh of that workspace, or a flow that just hit a conflict).</summary>
    public void SetConflicted(string workspacePath, bool value)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            Bump(key);
            Apply(key, value);
        }
    }

    /// <summary>
    /// The workspace's state generation: bumped by every operation start and every flow-written conflict flag. A refresh
    /// takes it before its background <c>MERGE_HEAD</c> read and hands it to <see cref="TrySetConflictedFromRead"/>.
    /// </summary>
    public long Epoch(string workspacePath)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            return epochs.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Writes a refresh's read of the conflict flag unless it may be stale: an operation is running in the workspace, or
    /// one started or a flow set the flag since <paramref name="epoch"/> was taken. False when the read was discarded.
    /// </summary>
    public bool TrySetConflictedFromRead(string workspacePath, bool value, long epoch)
    {
        var key = Key(workspacePath);
        lock (gate)
        {
            if (operating.Contains(key) || epochs.GetValueOrDefault(key) != epoch)
            {
                return false;
            }

            Apply(key, value);
            return true;
        }
    }

    /// <summary>Replaces the whole merge-conflict snapshot with the workspaces a full refresh found in a merge.</summary>
    public void ReplaceConflicts(IEnumerable<string> workspacePaths)
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in workspacePaths)
        {
            next.Add(Key(path));
        }

        lock (gate)
        {
            conflicted = next;
        }
    }

    private void Bump(string key) => epochs[key] = epochs.GetValueOrDefault(key) + 1;

    private void Apply(string key, bool value)
    {
        if (value)
        {
            conflicted.Add(key);
        }
        else
        {
            conflicted.Remove(key);
        }
    }

    private static string Key(string workspacePath) => OccupancyKey.Compute(null, workspacePath);
}
