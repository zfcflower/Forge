using System.Diagnostics;
using System.Globalization;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// Killing a process has to take the tree with it. The child under test starts a grandchild that outlives a kill of the
/// child alone (a ping on Windows, a sleep on Unix), records its id in a file, and then sleeps; both the timeout path and
/// the cancellation path must leave no such process behind.
/// </summary>
public class ProcessTreeTests
{
    private static readonly TimeSpan GrandchildGoneLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_timeout_kills_the_grandchild_the_child_started()
    {
        using var workspace = new TemporaryWorkspace();
        var pidFile = workspace.Full("grandchild.pid");
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(
            ChildProcess.SpawnGrandchildMode,
            workspace.Root,
            path: pidFile,
            timeout: TimeSpan.FromMilliseconds(1500));

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.TimedOut, result.EndReason);
        var grandchild = ReadGrandchildId(pidFile);
        Assert.True(
            await WaitForExitAsync(grandchild, GrandchildGoneLimit),
            $"process {grandchild} started by the child is still running after the timeout kill");
    }

    [Fact]
    public async Task Cancellation_kills_the_grandchild_the_child_started()
    {
        using var workspace = new TemporaryWorkspace();
        var pidFile = workspace.Full("grandchild.pid");
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(
            ChildProcess.SpawnGrandchildMode,
            workspace.Root,
            path: pidFile,
            timeout: TimeSpan.FromSeconds(60));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        var exception = await Record.ExceptionAsync(() => runner.ExecuteAsync(request, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        var grandchild = ReadGrandchildId(pidFile);
        Assert.True(
            await WaitForExitAsync(grandchild, GrandchildGoneLimit),
            $"process {grandchild} started by the child is still running after the cancellation kill");
    }

    /// <summary>Reads the id the child wrote before it started sleeping.</summary>
    private static int ReadGrandchildId(string pidFile)
    {
        Assert.True(File.Exists(pidFile), $"the child did not record the grandchild id in '{pidFile}'");
        var text = File.ReadAllText(pidFile).Trim();
        Assert.True(
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0,
            $"the recorded grandchild id is '{text}'");
        return id;
    }

    /// <summary>Waits until <paramref name="id"/> is gone, killing it when the wait runs out so a failure leaves nothing behind.</summary>
    private static async Task<bool> WaitForExitAsync(int id, TimeSpan limit)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < limit)
        {
            if (!IsRunning(id))
            {
                return true;
            }

            await Task.Delay(50);
        }

        if (IsRunning(id))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            return false;
        }

        return true;
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
