namespace Forge.IO;

/// <summary>macOS: moves into <c>~/.Trash</c>; a name that is already there gets a numeric suffix. A move that
/// cannot be done by rename (another volume) fails with the original exception; it is never turned into a delete.</summary>
public sealed class MacTrash(string trashDirectory) : ITrashService
{
    public Task MoveToTrashAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => Move(fullPath), CancellationToken.None);
    }

    private void Move(string fullPath)
    {
        var isDirectory = TrashNames.Classify(fullPath);
        Directory.CreateDirectory(trashDirectory);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (var attempt = 1; ; attempt++)
        {
            var target = Path.Combine(trashDirectory, TrashNames.WithSuffix(name, attempt));
            if (TrashNames.Occupied(target))
            {
                continue;
            }

            try
            {
                if (isDirectory)
                {
                    Directory.Move(fullPath, target);
                }
                else
                {
                    File.Move(fullPath, target, overwrite: false);
                }

                return;
            }
            catch (IOException) when (TrashNames.Occupied(target))
            {
                // Lost a race for the name; try the next suffix.
            }
        }
    }
}
