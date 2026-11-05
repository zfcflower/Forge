using System.Diagnostics;

namespace Forge.IO;

/// <summary>Crash-safe whole-file replacement for files other programs may have open. No locking:
/// it is safe for user workspace files. A read-only destination is refused before any rename, and on Unix a
/// replacement keeps the destination's file mode. See <c>SPEC.md</c> for the retry policy.</summary>
public static class AtomicFile
{
    private const int BufferSize = 4096;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int UnixAlreadyExists = 17;
    private const int FileExists = 80;
    private const int AlreadyExists = 183;
    private const int RetryDelayMilliseconds = 1;
    private const int MissingRecheckLimit = 16;

    private static readonly TimeSpan SpinPhase = TimeSpan.FromMilliseconds(10);

    /// <summary>Writes <paramref name="data"/> to a temporary file in the destination directory, flushes it to disk,
    /// then replaces <paramref name="path"/>. The destination is either the old or the new content, never partial.
    /// Only the confirmed Windows file-in-use replace race is retried, within <paramref name="options"/>' budget;
    /// when it is exhausted the last original exception is rethrown. On failure or cancellation the temporary file
    /// is removed and the destination is untouched. A read-only destination fails immediately with
    /// <see cref="UnauthorizedAccessException" /> and keeps its bytes and attributes.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var budget = ResolveBudget(options);
        var destination = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory(destination);

        var temporary = TemporaryPath(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                stream.Write(data);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        ReplaceFileCore(temporary, destination, budget, cancellationToken);
    }

    /// <summary>Async variant of <see cref="WriteAllBytes"/>; the retry wait is cancellable.</summary>
    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var budget = ResolveBudget(options);
        return WriteAllBytesCoreAsync(Path.GetFullPath(path), data, budget, cancellationToken);
    }

    /// <summary>Publishes an already-written and already-flushed temporary file onto <paramref name="destinationPath"/>
    /// with the same retry policy, read-only refusal and cleanup as <see cref="WriteAllBytes"/>. On failure or
    /// cancellation <paramref name="tempPath"/> is deleted and the destination keeps its previous content.</summary>
    public static void ReplaceFile(string tempPath, string destinationPath, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var budget = ResolveBudget(options);
        ReplaceFileCore(Path.GetFullPath(tempPath), Path.GetFullPath(destinationPath), budget, cancellationToken);
    }

    /// <summary>Async variant of <see cref="ReplaceFile"/>; the retry wait is cancellable.</summary>
    public static ValueTask ReplaceFileAsync(string tempPath, string destinationPath, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var budget = ResolveBudget(options);
        return ReplaceFileCoreAsync(Path.GetFullPath(tempPath), Path.GetFullPath(destinationPath), budget, cancellationToken);
    }

    /// <summary>Reads a file with <c>FileShare.ReadWrite | FileShare.Delete</c> so it never blocks a writer's replace.
    /// Returns <c>null</c> only when the file definitely does not exist after bounded re-checks; a transient
    /// disappearance during a replace is retried, never reported as missing data.</summary>
    public static byte[]? ReadAllBytesShared(string path, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var budget = ResolveBudget(options);
        var fullPath = Path.GetFullPath(path);
        var start = Stopwatch.GetTimestamp();

        for (var missingAttempts = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
                return ReadAll(stream);
            }
            catch (Exception exception) when (IsMissing(exception))
            {
                if (++missingAttempts >= MissingRecheckLimit || Stopwatch.GetElapsedTime(start) >= budget)
                {
                    return null;
                }

                Wait(start, cancellationToken);
            }
            catch (Exception exception) when (IsReplaceRace(exception, fullPath))
            {
                if (Stopwatch.GetElapsedTime(start) >= budget)
                {
                    throw;
                }

                Wait(start, cancellationToken);
            }
        }
    }

    private static async Task WriteAllBytesCoreAsync(string destination, ReadOnlyMemory<byte> data, TimeSpan budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory(destination);

        var temporary = TemporaryPath(destination);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        await ReplaceFileCoreAsync(temporary, destination, budget, cancellationToken).ConfigureAwait(false);
    }

    private static void ReplaceFileCore(string temporary, string destination, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectory(destination);
            Publish(temporary, destination, budget, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async ValueTask ReplaceFileCoreAsync(string temporary, string destination, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectory(destination);
            await PublishAsync(temporary, destination, budget, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task PublishAsync(string temporary, string destination, TimeSpan budget, CancellationToken cancellationToken)
    {
        PreparePublication(temporary, destination);
        var start = Stopwatch.GetTimestamp();
        if (!File.Exists(destination))
        {
            try
            {
                File.Move(temporary, destination, overwrite: false);
                return;
            }
            catch (IOException exception) when (IsDestinationAppeared(exception, destination))
            {
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (IsReplaceRace(exception, destination))
            {
                if (Stopwatch.GetElapsedTime(start) >= budget)
                {
                    throw;
                }

                if (Stopwatch.GetElapsedTime(start) < SpinPhase)
                {
                    Thread.Yield();
                }
                else
                {
                    await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static void Publish(string temporary, string destination, TimeSpan budget, CancellationToken cancellationToken)
    {
        PreparePublication(temporary, destination);
        var start = Stopwatch.GetTimestamp();
        if (!File.Exists(destination))
        {
            try
            {
                File.Move(temporary, destination, overwrite: false);
                return;
            }
            catch (IOException exception) when (IsDestinationAppeared(exception, destination))
            {
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (IsReplaceRace(exception, destination))
            {
                if (Stopwatch.GetElapsedTime(start) >= budget)
                {
                    throw;
                }

                Wait(start, cancellationToken);
            }
        }
    }

    private static void Wait(long start, CancellationToken cancellationToken)
    {
        if (Stopwatch.GetElapsedTime(start) < SpinPhase)
        {
            Thread.Yield();
            return;
        }

        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.WaitHandle.WaitOne(RetryDelayMilliseconds);
            return;
        }

        Thread.Sleep(RetryDelayMilliseconds);
    }

    private static bool IsReplaceRace(Exception exception, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var isRace = exception is UnauthorizedAccessException ||
            (exception is IOException io && (io.HResult & 0xFFFF) is SharingViolation or LockViolation);
        return isRace && IsReplaceableTarget(destination);
    }

    /// <summary>True when <paramref name="path"/> exists, is not a directory and is not read-only.</summary>
    private static bool IsReplaceableTarget(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReadOnly)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Refuses a destination that rejects writes before any rename, and carries the destination's Unix
    /// mode onto <paramref name="temporary"/> so the replacement keeps the file's permissions. See <c>SPEC.md</c>.</summary>
    private static void PreparePublication(string temporary, string destination)
    {
        if (!File.Exists(destination))
        {
            return;
        }

        if (IsReadOnlyTarget(destination))
        {
            throw new UnauthorizedAccessException($"Could not replace '{destination}': the destination is read-only.");
        }

        PreserveUnixMode(temporary, destination);
    }

    /// <summary>True when <paramref name="path"/> rejects writes: the <c>ReadOnly</c> attribute on every platform,
    /// plus a write-refusing Unix mode or a refused write open on Unix. A path that cannot be inspected (it vanished
    /// during the replace) is not treated as read-only; the rename below decides.</summary>
    private static bool IsReadOnlyTarget(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return IsReadOnlyOnUnix(path);
    }

    private static bool IsReadOnlyOnUnix(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const UnixFileMode Writable = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        try
        {
            if ((File.GetUnixFileMode(path) & Writable) == 0)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void PreserveUnixMode(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static bool IsDestinationAppeared(IOException exception, string destination) =>
        ((exception.HResult & 0xFFFF) is AlreadyExists or FileExists or UnixAlreadyExists) && File.Exists(destination);
    private static bool IsMissing(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException;

    private static byte[] ReadAll(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return [];
        }

        if (length > Array.MaxLength)
        {
            throw new IOException($"'{stream.Name}' is too large to read into memory.");
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static TimeSpan ResolveBudget(AtomicFileOptions? options)
    {
        var budget = (options ?? new AtomicFileOptions()).RetryBudget;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero, nameof(options));
        return budget;
    }

    private static void EnsureDirectory(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string TemporaryPath(string destination) => $"{destination}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Retry budget for <see cref="AtomicFile"/>.</summary>
public sealed record AtomicFileOptions
{
    /// <summary>Total time the replace/read retry may take. Default 5 s. Must be positive.</summary>
    public TimeSpan RetryBudget { get; init; } = TimeSpan.FromSeconds(5);
}
