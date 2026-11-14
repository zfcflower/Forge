using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Forge.IO.Tools;

namespace Forge.IO;

/// <summary>
/// Starts one process and captures both output streams as bytes, with a hard cap per stream, a wall clock bound, and a
/// killed process tree when the bound is hit. Output above a cap is counted and dropped while still being read, so a child
/// that writes a gigabyte neither grows this process's heap nor blocks on a full pipe; the bytes are decoded once at the end.
/// <para>A start failure (a missing executable, a missing working directory, an access denial) is a result with
/// <see cref="StartFailedExitCode"/> and the reason in <see cref="ProcessResult.Stderr"/>, not an exception: callers of this
/// class are tool implementations that must answer the model with an error result instead of unwinding the agent loop.
/// Cancellation is the one exception that escapes, and only after the process tree is gone; a timeout returns
/// <see cref="ProcessEndReason.TimedOut"/> with whatever output was captured. A run that ends in the same instant its
/// timeout fires is reported as timed out, because “was the kill too late or exactly on time” has no observable answer.</para>
/// <para>Standard input is redirected and closed as soon as the process starts: a command that waits for input ends at
/// once instead of hanging until the timeout, which is what an agent's non-interactive call must do.</para>
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The exit code of a run that never started; <see cref="ProcessResult.EndReason"/> is <see cref="ProcessEndReason.Exited"/>.
    /// A real process that exits with <c>0xFFFFFFFF</c> looks the same, which the tool layer accepts: a start failure is the
    /// case the model has to see, and a five-billion-code exit is not something a command line does by accident.</summary>
    public const int StartFailedExitCode = -1;

    private const int ReadChunkBytes = 64 * 1024;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PumpGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PumpCancelGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Repository-location and discovery variables never inherited by a child: each one would point git at a repository,
    /// index, object store or discovery boundary other than the working directory's. The list is deliberately the same as
    /// <c>GitCli</c>'s (that class cannot be referenced from here — the dependency runs the other way) so a <c>bash</c>
    /// command running git sees the worktree, not the harness that launched Forge. A caller that needs one of them passes
    /// it explicitly through <see cref="ProcessRequest.Environment"/>, which is applied afterwards.
    /// </summary>
    private static readonly string[] InheritedRepositoryVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_PREFIX",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    ];

    private readonly SemaphoreSlim gate;

    /// <summary>A runner that lets <see cref="ToolOptions.Default"/>'s number of processes run at once.</summary>
    public ProcessRunner()
        : this(ToolOptions.Default.MaxConcurrentProcesses)
    {
    }

    /// <summary>A runner with its own process-wide concurrency bound.</summary>
    /// <param name="maxConcurrentProcesses">How many runs may be in flight; the rest wait, and the wait counts as duration.</param>
    public ProcessRunner(int maxConcurrentProcesses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentProcesses, 1);
        gate = new SemaphoreSlim(maxConcurrentProcesses, maxConcurrentProcesses);
    }

    /// <inheritdoc />
    public async Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Started before the gate so the duration a caller sees includes time spent queued behind other processes.
        var stopwatch = Stopwatch.StartNew();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(request, stopwatch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ProcessResult> RunAsync(ProcessRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(CreateStartInfo(request))
                ?? throw new InvalidOperationException($"'{request.FileName}' did not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or ArgumentException or NotSupportedException or IOException)
        {
            return StartFailure(request, exception, stopwatch);
        }

        var stdout = new ProcessOutputCollector(request.MaxStdoutBytes);
        var stderr = new ProcessOutputCollector(request.MaxStderrBytes);
        using (process)
        using (var pumpStop = new CancellationTokenSource())
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            CloseInput(process);

            if (request.Timeout != Timeout.InfiniteTimeSpan)
            {
                // A zero or negative bound means "do not wait"; CancelAfter rejects anything below -1 ms.
                linked.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.Zero);
            }

            using var tail = request.OnOutput is { } onOutput
                ? new ProcessOutputTail(onOutput, request.OutputTailChars, request.OutputInterval)
                : null;
            var pumps = Task.WhenAll(
                PumpAsync(process.StandardOutput.BaseStream, stdout, tail, pumpStop.Token),
                PumpAsync(process.StandardError.BaseStream, stderr, tail, pumpStop.Token));
            // The registration kills the tree as soon as the timeout or the caller fires, which also unblocks a wait that
            // is already in progress; the wait below then reports the cancellation.
            using var killOnCancel = linked.Token.Register(static state => TryKill((Process)state!), process);

            var killed = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                killed = true;
            }

            // The registration can kill the process before WaitForExitAsync observes the token, so the token decides.
            killed |= linked.IsCancellationRequested;
            if (killed)
            {
                // Wait until the exit is really visible; the tree kill was already requested above.
                TryKill(process);
                await WaitForExitQuietlyAsync(process, ExitGrace).ConfigureAwait(false);
            }

            // A child that leaves a grandchild holding the pipe keeps stdout open past its own exit; the grace period is
            // what stops that from hanging the tool call. When a pump did outlive both budgets its collector is abandoned
            // first: that stops the pump from writing into the buffer, which is what makes returning the pooled array safe
            // even though the pump task may still be unwinding.
            var pumpsDone = await FinishPumpsAsync(pumps, pumpStop).ConfigureAwait(false);

            try
            {
                var capturedOut = stdout.Capture();
                var capturedError = stderr.Capture();
                if (!pumpsDone)
                {
                    stdout.Abandon();
                    stderr.Abandon();
                }

                // The caller's cancellation wins over the timeout: after the tree is gone the call is simply cancelled.
                cancellationToken.ThrowIfCancellationRequested();

                var exitCode = ExitCodeOf(process);
                return new ProcessResult(
                    exitCode,
                    capturedOut.Text,
                    capturedError.Text,
                    capturedOut.TotalBytes,
                    capturedError.TotalBytes,
                    capturedOut.Truncated,
                    capturedError.Truncated,
                    killed ? ProcessEndReason.TimedOut : ProcessEndReason.Exited,
                    stopwatch.Elapsed);
            }
            finally
            {
                stdout.Release();
                stderr.Release();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in InheritedRepositoryVariables)
        {
            info.Environment.Remove(name);
        }

        if (request.Environment is { } environment)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
        }

        return info;
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
        }
    }

    private static ProcessResult StartFailure(ProcessRequest request, Exception exception, Stopwatch stopwatch)
    {
        var message = $"Could not start '{request.FileName}': {exception.Message}";
        return new ProcessResult(
            StartFailedExitCode,
            string.Empty,
            message,
            0,
            Encoding.UTF8.GetByteCount(message),
            StdoutTruncated: false,
            StderrTruncated: false,
            ProcessEndReason.Exited,
            stopwatch.Elapsed);
    }

    /// <summary>Reads a stream to its end into <paramref name="capture"/>; never throws, because a killed process makes the
    /// pipe fail in platform specific ways and a failed pump must not turn into a failed tool call.</summary>
    private static async Task PumpAsync(Stream stream, ProcessOutputCollector capture, ProcessOutputTail? tail, CancellationToken cancellationToken)
    {
        var decoder = tail is null ? null : Encoding.UTF8.GetDecoder();
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, ReadChunkBytes), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException
                    or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    return;
                }

                if (read == 0)
                {
                    return;
                }

                capture.Append(buffer.AsSpan(0, read));
                tail?.Append(decoder!, buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Waits a bounded time for both pumps; on expiry cancels their reads (an orphaned grandchild can hold the
    /// pipe open forever) and waits once more. Returns false when a pump outlived both bounds.</summary>
    private static async Task<bool> FinishPumpsAsync(Task pumps, CancellationTokenSource pumpStop)
    {
        if (pumps.IsCompleted)
        {
            return true;
        }

        try
        {
            await pumps.WaitAsync(PumpGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
        }

        try
        {
            pumpStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await pumps.WaitAsync(PumpCancelGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process, TimeSpan grace)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (InvalidOperationException)
        {
            // The process never started or has been disposed; there is nothing left to wait for.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or Win32Exception or ObjectDisposedException or AggregateException)
        {
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return StartFailedExitCode;
        }
    }
}
