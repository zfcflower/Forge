namespace Forge.IO;

/// <summary>Moves a file or directory to the platform trash so the user can still get it back. Never deletes: a
/// failure (missing source, user cancelled the system dialog, cross-volume move, no permission, a Windows location
/// without a Recycle Bin such as a UNC path or a non-fixed drive) surfaces as an exception and leaves the source where it
/// was.</summary>
public interface ITrashService
{
    /// <summary>Moves <paramref name="path"/> to the trash. Throws when the path does not exist or the move fails.</summary>
    Task MoveToTrashAsync(string path, CancellationToken ct);
}

/// <summary>Chooses the trash implementation of the running platform.</summary>
public static class TrashService
{
    public static ITrashService CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRecycleBinTrash();
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return new MacTrash(Path.Combine(home, ".Trash"));
        }

        return XdgTrash.FromEnvironment(Environment.GetEnvironmentVariable, home);
    }
}
