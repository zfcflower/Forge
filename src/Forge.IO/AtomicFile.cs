namespace Forge.IO;

/// <summary>Crash-safe whole-file replacement for files other programs may have open. No locking:
/// it is safe for user workspace files. See <c>SPEC.md</c> for the retry policy.</summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="data"/> to a temporary file in the destination directory, flushes it to disk,
    /// then replaces <paramref name="path"/>. The destination is either the old or the new content, never partial.
    /// Only the confirmed Windows file-in-use replace race is retried, within <paramref name="options"/>' budget;
    /// when it is exhausted the last original exception is rethrown. On failure or cancellation the temporary file
    /// is removed and the destination is untouched.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>Async variant of <see cref="WriteAllBytes"/>; the retry wait is cancellable.</summary>
    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>Reads a file with <c>FileShare.ReadWrite | FileShare.Delete</c> so it never blocks a writer's replace.
    /// Returns <c>null</c> only when the file definitely does not exist after bounded re-checks; a transient
    /// disappearance during a replace is retried, never reported as missing data.</summary>
    public static byte[]? ReadAllBytesShared(string path, AtomicFileOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

/// <summary>Retry budget for <see cref="AtomicFile"/>.</summary>
public sealed record AtomicFileOptions
{
    /// <summary>Total time the replace/read retry may take. Default 5 s. Must be positive.</summary>
    public TimeSpan RetryBudget { get; init; } = TimeSpan.FromSeconds(5);
}
