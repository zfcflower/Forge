using System.Buffers;
using System.Diagnostics;

namespace Forge.Bench;

/// <summary>
/// The baseline the process and output scenarios compare <see cref="Forge.IO.ProcessRunner"/> against: the shortest direct
/// use of the BCL - <see cref="Process.Start"/> plus a pump on each <c>BaseStream</c> that counts bytes and keeps none.
/// No gate, no timeout, no linked token, no pooled capture buffer; anything the runner adds on top shows up as its
/// overhead.
/// </summary>
internal static class DirectProcess
{
    private const int ReadChunkBytes = 64 * 1024;

    public static async Task<OpResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"'{fileName}' did not start.");
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
        }

        var stdout = DrainAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderr = DrainAsync(process.StandardError.BaseStream, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdoutBytes = await stdout.ConfigureAwait(false);
        var stderrBytes = await stderr.ConfigureAwait(false);

        return new OpResult(
            Items: 0,
            Signature: stdoutBytes,
            Observation: $"exit={process.ExitCode} stdout={stdoutBytes}B stderr={stderrBytes}B");
    }

    /// <summary>Reads a pipe to its end without keeping a byte: the cheapest a caller can do and still avoid blocking the child.</summary>
    private static async Task<long> DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            var total = 0L;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, ReadChunkBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return total;
                }

                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
