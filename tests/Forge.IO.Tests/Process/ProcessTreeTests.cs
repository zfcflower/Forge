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

    /// <summary>The smallest bound the timeout test keeps, so a machine that starts the child quickly still exercises a
    /// deadline that is clearly shorter than the child's sleep.</summary>
    private static readonly TimeSpan MinimumDeadline = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task A_timeout_kills_the_grandchild_the_child_started()
    {
        using var workspace = new TemporaryWorkspace();
        var pidFile = workspace.Full("grandchild.pid");
        var runner = new ProcessRunner();
        // The deadline counts from the child's own start, so it has to cover loading the test assembly and spawning the
        // grandchild before it can prove anything: a fixed bound fails on a loaded machine for a reason that has nothing
        // to do with the kill. Measuring one launch keeps the fast case fast and the bound honest.
        var deadline = DeadlineFor(await MeasureStartupAsync(runner, workspace.Root));
        var request = ChildProcess.Request(
            ChildProcess.SpawnGrandchildMode,
            workspace.Root,
            path: pidFile,
            timeout: deadline);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.TimedOut, result.EndReason);
        Assert.True(
            File.Exists(pidFile),
            $"the child did not start its grandchild within the {deadline.TotalMilliseconds:F0} ms deadline; the measured "
                + "startup cost is what sets that deadline (see MeasureStartupAsync)");
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
            timeout: TestLimit);
        using var cancellation = new CancellationTokenSource();
        var run = runner.ExecuteAsync(request, cancellation.Token);

        // Cancelled only once the grandchild is on record. A cancellation armed on a fixed head start fires while the
        // child is still loading the test assembly on a busy machine, and then the test has no grandchild to miss and
        // reports a tree-kill bug that did not happen.
        var grandchild = await WaitForGrandchildAsync(pidFile);
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => run);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(
            await WaitForExitAsync(grandchild, GrandchildGoneLimit),
            $"process {grandchild} started by the child is still running after the cancellation kill");
    }

    /// <summary>How long the child needs to load the test assembly and exit, measured once on this machine.</summary>
    private static async Task<TimeSpan> MeasureStartupAsync(ProcessRunner runner, string directory)
    {
        var start = Stopwatch.GetTimestamp();
        var probe = await runner.ExecuteAsync(
            ChildProcess.Request(ChildProcess.ExitCodeMode, directory, timeout: TestLimit), CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.Equal(ProcessEndReason.Exited, probe.EndReason);
        Assert.Equal(0, probe.ExitCode);
        return elapsed;
    }

    /// <summary>Three times the measured startup, never below the floor and never near <see cref="TestLimit"/>.</summary>
    private static TimeSpan DeadlineFor(TimeSpan startup)
    {
        var deadline = TimeSpan.FromTicks(Math.Max(MinimumDeadline.Ticks, startup.Ticks * 3));
        return deadline < TestLimit ? deadline : TestLimit - TimeSpan.FromSeconds(1);
    }

    /// <summary>Waits until the child has recorded its grandchild, so a kill cannot land before the grandchild exists.</summary>
    private static async Task<int> WaitForGrandchildAsync(string pidFile)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TestLimit)
        {
            if (TryReadGrandchildId(pidFile) is { } recorded)
            {
                return recorded;
            }

            await Task.Delay(20);
        }

        return ReadGrandchildId(pidFile);
    }

    /// <summary>The id the child wrote before it started sleeping.</summary>
    private static int ReadGrandchildId(string pidFile)
    {
        Assert.True(File.Exists(pidFile), $"the child did not record the grandchild id in '{pidFile}'");
        var id = TryReadGrandchildId(pidFile);
        Assert.True(
            id is > 0,
            $"the recorded grandchild id in '{pidFile}' is '{File.ReadAllText(pidFile).Trim()}'");
        return id!.Value;
    }

    /// <summary>The recorded id, or null while the file is missing or still being written.</summary>
    private static int? TryReadGrandchildId(string pidFile)
    {
        try
        {
            var text = File.ReadAllText(pidFile).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        }
        catch (IOException)
        {
            return null;
        }
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
