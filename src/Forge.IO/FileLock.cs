namespace Forge.IO;

/// <summary>Cross-process mutex: exclusive open of a dedicated lock file; released by the OS if the process dies.
/// The lock file is never deleted by this type.</summary>
public sealed class FileLock : IDisposable
{
    /// <summary>Acquires the lock with jittered exponential backoff. Throws <see cref="TimeoutException"/>
    /// when <paramref name="timeout"/> elapses and <see cref="OperationCanceledException"/> on cancellation.
    /// Only sharing/lock violations are retried; other IO errors propagate immediately.</summary>
    public static FileLock Acquire(string lockPath, TimeSpan timeout, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public void Dispose() { }
}
