using System.Diagnostics;
using Forge.Ipc;

namespace Forge.Integration.Tests;

/// <summary>
/// Waits until a host or worker accepts a connection on its pipe. The condition is the connection itself; the bound is
/// a safety net (<see cref="TestBudgets.HostStart" />), so a process that never listens fails with a message naming the
/// pipe, the time waited and whatever the caller can say about the process - never with the cancellation of the whole
/// test, which only says that something was slow.
/// </summary>
internal static class PipeWait
{
    /// <summary>Retries the connection on <paramref name="pipe" /> until it is accepted or the host start bound
    /// expires. <paramref name="describe" /> is only called when the wait fails, so it may read the process state and
    /// the captured output.</summary>
    public static async Task<NamedPipeClientTransport> ConnectAsync(
        string pipe,
        Func<string> describe,
        CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var limit = TestBudgets.HostStart;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            // One attempt carries the whole bound: the pipe layer abandons a connect that outlives its own timeout but
            // leaves it retrying, and a connect that lands after the attempt was given up on would take the host's only
            // connection and close it, which the host reads as its main program leaving.
            var transport = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = limit });
            try
            {
                await transport.StartAsync(deadline.Token).ConfigureAwait(false);
                TestBudgets.NoteHostStart(Stopwatch.GetElapsedTime(start));
                return transport;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                await DisposeQuietlyAsync(transport).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(transport).ConfigureAwait(false);
                throw new TimeoutException(Describe(pipe, limit, start, describe), exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Describe(pipe, limit, start, describe));
            }
        }
    }

    private static string Describe(string pipe, TimeSpan limit, long start, Func<string> describe) =>
        $"nothing accepted a connection on pipe '{pipe}' within {limit.TotalSeconds:0} s " +
        $"({Stopwatch.GetElapsedTime(start).TotalSeconds:0.###} s waited). {describe()}";

    private static async ValueTask DisposeQuietlyAsync(NamedPipeClientTransport transport)
    {
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
