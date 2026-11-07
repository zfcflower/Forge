using System.Buffers;
using System.Text;

namespace Forge.IO;

/// <summary>
/// Whole-file and append writes for text, always UTF-8 without a BOM. The whole-file write goes through
/// <see cref="AtomicFile"/> (temporary file in the same directory, fsync, rename), so a read-only destination is refused
/// and a failed write never leaves a half file; the append is a plain <see cref="FileStream"/> and is not atomic.
/// Callers serialize operations on the same path with <see cref="PathGate"/>.
/// </summary>
public static class TextFileWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="content"/>; returns the bytes written.</summary>
    public static async ValueTask<long> WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        EnsureDirectory(path);
        var byteCount = Utf8.GetByteCount(content);
        if (byteCount == 0)
        {
            await AtomicFile.WriteAllBytesAsync(path, ReadOnlyMemory<byte>.Empty, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Utf8.GetBytes(content, buffer);
            await AtomicFile.WriteAllBytesAsync(path, buffer.AsMemory(0, written), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Appends <paramref name="content"/> to <paramref name="path"/>; returns the bytes written.</summary>
    public static async ValueTask<long> AppendAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        EnsureDirectory(path);
        var byteCount = Utf8.GetByteCount(content);
        if (byteCount == 0)
        {
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Utf8.GetBytes(content, buffer);
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous,
                    BufferSize = 0,
                });
            await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
