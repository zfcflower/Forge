namespace Forge.IO;

/// <summary>One running Forge per data root: an exclusive <see cref="FileLock"/> on a lock file inside the data
/// root (never in a user workspace). The OS releases it if the process dies; there is no marker file to go stale.</summary>
public static class SingleInstanceLock
{
    public const string LockFileName = "forge.instance.lock";

    /// <summary>Returns the held lock, or <see langword="null"/> when another instance holds it. Keep the result
    /// for the lifetime of the process and dispose it on exit.</summary>
    public static IDisposable? TryAcquire(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        try
        {
            return FileLock.Acquire(Path.Combine(dataRoot, LockFileName), TimeSpan.Zero);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
