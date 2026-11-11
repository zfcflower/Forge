namespace Forge.Core.Storage;

/// <summary>
/// The workspace occupancy key of a session row: <c>Normalize(COALESCE(worktree_path, workspace_path))</c>, upper-cased
/// on Windows. <c>Normalize</c> has the semantics of <c>Forge.IO.Paths.WorkspacePath.Normalize</c> (full path, trailing
/// separator removed, no link resolution, no case folding) - Core cannot reference Forge.IO, so the function lives here
/// and the v5 migration and every runtime write share it. The unique partial index
/// <c>ux_sessions_occupancy</c> enforces one Active root session per key.
/// </summary>
public static class OccupancyKey
{
    /// <summary>Key of a row with these paths; callers never pass a key of their own.</summary>
    public static string Compute(string? worktreePath, string workspacePath)
    {
        var path = string.IsNullOrWhiteSpace(worktreePath) ? workspacePath : worktreePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    /// <summary>Same result as <c>WorkspacePath.Normalize</c>.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full) is { Length: > 0 } trimmed ? trimmed : full;
    }
}
