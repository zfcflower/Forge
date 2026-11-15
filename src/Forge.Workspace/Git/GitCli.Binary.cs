using System.Buffers;
using System.Diagnostics;

namespace Forge.Workspace;

/// <summary>
/// The binary half of <see cref="GitCli"/>: reading a blob's bytes as they are. Everything that captures git's output as
/// text (<see cref="RunAsync(string, IReadOnlyList{string}, CancellationToken)"/>) decodes UTF-8 on the way in, which turns a
/// PNG into replacement characters; comparing two versions of an image needs the exact bytes, so that one read has its own
/// pump.
/// </summary>
public sealed partial class GitCli
{
    /// <summary>
    /// Bytes of <paramref name="path"/> at <paramref name="revision"/> (a commit, a tree or a branch), exactly as stored;
    /// null when the object is absent there, when it is larger than <paramref name="maxBytes"/>, or when git cannot read it.
    /// The path is repository-root relative with <c>/</c> separators, the same shape the diff reports.
    /// </summary>
    public async Task<byte[]?> GetFileBytesAtRevisionAsync(
        string workingDirectory,
        string revision,
        string path,
        int maxBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Reference(revision, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var arguments = Args("show", "--end-of-options", $"{revision}:{path}");
        using var process = Start(workingDirectory, arguments);
        using var timeoutSource = CreateTimeoutSource(ct, DefaultTimeout);
        using var killOnCancel = timeoutSource.Token.Register(static state => TryKill((Process)state!), process);

        // stderr is drained the normal way so a git that complains cannot fill the pipe and stall; the text is only used for
        // the log-less "absent" answer, so its own capture cap is irrelevant here.
        var stderrTask = PumpAsync(process.StandardError.BaseStream, null, ErrorCaptureCharacters, keepTail: true, carriageReturnEndsLine: false, timeoutSource.Token);
        var (data, tooLarge) = await ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, timeoutSource.Token).ConfigureAwait(false);
        if (tooLarge)
        {
            // Stop the writer before waiting: an oversized blob leaves stdout unread, git fills the pipe buffer and never
            // exits, so a plain WaitForExit would sit there until DefaultTimeout (two minutes per side). Killing first is
            // what turns "too large" into an immediate null.
            TryKill(process);
        }

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitPumpAsync(stderrTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return null;
        }

        await AwaitPumpAsync(stderrTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return tooLarge || ExitCodeOf(process) != 0 ? null : data;
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes; the caller learns whether more arrived, so an oversized blob is never
    /// buffered to the end just to be thrown away.
    /// </summary>
    private static async Task<(byte[] Data, bool TooLarge)> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        var captured = new MemoryStream(Math.Min(limit, 1024 * 1024));
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    break;
                }

                if (read == 0)
                {
                    return (captured.ToArray(), false);
                }

                if (captured.Length + read > limit)
                {
                    return ([], true);
                }

                captured.Write(buffer, 0, read);
            }

            return (captured.ToArray(), false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            captured.Dispose();
        }
    }
}