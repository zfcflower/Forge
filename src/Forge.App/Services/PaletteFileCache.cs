using Forge.Workspace;

namespace Forge.App.Services;

/// <summary>
/// The palette's file list, kept per workspace. A refresh is a <c>git ls-files</c> when the directory is a repository and
/// a bounded walk otherwise (<see cref="FileIndex"/> owns both), so the palette opens on the list the previous open left
/// behind and the refresh lands a moment later - no wait, and no list belonging to the workspace the user just left.
/// The snapshot is swapped atomically: a reader on the UI thread sees either the old array or the new one, never a mix.
/// </summary>
internal sealed class PaletteFileCache
{
    private IReadOnlyList<string> files = [];
    private string? workspace;

    /// <summary>The list cached for <paramref name="forWorkspace"/>; empty when it belongs to another workspace.</summary>
    public IReadOnlyList<string> Cached(string? forWorkspace) =>
        string.Equals(workspace, forWorkspace, StringComparison.Ordinal) ? Volatile.Read(ref files) : [];

    /// <summary>Re-reads the workspace and returns the new list; the caller keeps showing <see cref="Cached"/> meanwhile.</summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(string? forWorkspace, GitCli git, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (string.IsNullOrEmpty(forWorkspace) || !Directory.Exists(forWorkspace))
        {
            workspace = forWorkspace;
            Volatile.Write(ref files, []);
            return [];
        }

        var index = await Task.Run(() => FileIndex.BuildAsync(forWorkspace, git, ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        workspace = forWorkspace;
        Volatile.Write(ref files, index.Files);
        return index.Files;
    }
}
