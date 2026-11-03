using System.Diagnostics;

namespace Forge.IO;

/// <summary>Cross-process mutex: exclusive open of a dedicated lock file; released by the OS if the process dies.
/// The lock file is never deleted by this type.</summary>
public sealed class FileLock : IDisposable
{
    private const int InitialDelayMilliseconds = 5;
    private const int MaximumDelayMilliseconds = 100;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int UnixWouldBlock = 11;
    private const int UnixMacWouldBlock = 35;

    private FileStream? stream;

    private FileLock(FileStream stream) => this.stream = stream;

    /// <summary>Acquires the lock with jittered exponential backoff. Throws <see cref="TimeoutException"/>
    /// when <paramref name="timeout"/> elapses and <see cref="OperationCanceledException"/> on cancellation.
    /// Only sharing/lock violations are retried; a lock path that can never be opened (a directory, a read-only
    /// file) and other IO errors propagate immediately.</summary>
    public static FileLock Acquire(string lockPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var fullPath = Path.GetFullPath(lockPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var start = Stopwatch.GetTimestamp();
        var delay = InitialDelayMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locked = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return new FileLock(locked);
            }
            catch (Exception exception) when (IsContention(exception) && !IsPermanentlyUnavailable(fullPath))
            {
                var elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed >= timeout)
                {
                    throw new TimeoutException(
                        $"Timed out after {timeout.TotalMilliseconds:F0} ms waiting for lock file '{fullPath}'.", exception);
                }

                Wait(Jitter(delay, timeout - elapsed), cancellationToken);
                delay = Math.Min(delay * 2, MaximumDelayMilliseconds);
            }
        }
    }

    public void Dispose()
    {
        var locked = Interlocked.Exchange(ref stream, null);
        locked?.Dispose();
    }

    private static bool IsContention(Exception exception)
    {
        if (exception is IOException io)
        {
            var code = io.HResult & 0xFFFF;
            return OperatingSystem.IsWindows()
                ? code is SharingViolation or LockViolation
                : code is UnixWouldBlock or UnixMacWouldBlock;
        }

        return OperatingSystem.IsWindows() && exception is UnauthorizedAccessException;
    }

    /// <summary>True when the lock path is a directory or a read-only file.</summary>
    private static bool IsPermanentlyUnavailable(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReadOnly)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static int Jitter(int delay, TimeSpan remaining)
    {
        var wait = Random.Shared.Next(delay / 2, delay + 1);
        var left = (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);
        return Math.Min(wait, left);
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        if (cancellationToken.CanBeCanceled)
        {
            if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return;
        }

        Thread.Sleep(milliseconds);
    }
}
