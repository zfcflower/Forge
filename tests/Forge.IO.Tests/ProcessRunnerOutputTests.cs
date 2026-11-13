using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// <c>ProcessRequest.OnOutput</c> through a real child process: the live tail carries both bursts of a slow writer and
/// stderr, never exceeds its character bound, leaves the captured result exactly as it was, survives a throwing listener,
/// and goes quiet once the run has returned (including a timed-out run). The tail's own timing rules are covered by
/// <see cref="ProcessOutputTailTests"/>.
/// </summary>
public class ProcessRunnerOutputTests
{
    /// <summary>A shell command line that prints <c>one</c>, pauses about a second, then prints <c>two</c>.</summary>
    private static ProcessRequest TwoBursts(string workingDirectory) =>
        Shell(
            workingDirectory,
            windows: "echo one& ping -n 2 127.0.0.1 >nul& echo two",
            unix: "echo one; sleep 1; echo two");

    private static ProcessRequest Shell(string workingDirectory, string windows, string unix) => new()
    {
        FileName = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } command ? command : "cmd.exe") : "/bin/sh",
        Arguments = OperatingSystem.IsWindows() ? ["/c", windows] : ["-c", unix],
        WorkingDirectory = workingDirectory,
        Timeout = TestLimit,
    };

    private sealed class Collector
    {
        private readonly object gate = new();
        private readonly List<string> calls = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public void Add(string tail)
        {
            lock (gate)
            {
                calls.Add(tail);
            }
        }
    }

    [Fact]
    public async Task Output_written_in_two_bursts_ends_up_in_the_last_snapshot()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = TwoBursts(workspace.Root) with
        {
            OnOutput = collector.Add,
            OutputInterval = TimeSpan.FromMilliseconds(50),
            OutputTailChars = 4096,
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var calls = collector.Calls;
        Assert.NotEmpty(calls);
        Assert.Contains("one", calls[0], StringComparison.Ordinal);
        Assert.All(calls, tail => Assert.True(tail.Length <= 4096));
        Assert.Contains("one", calls[^1], StringComparison.Ordinal);
        Assert.Contains("two", calls[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standard_error_is_part_of_the_tail()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = Shell(workspace.Root, windows: "echo out& echo err>&2", unix: "echo out; echo err >&2") with
        {
            OnOutput = collector.Add,
            OutputInterval = TimeSpan.Zero,
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("err", result.Stderr, StringComparison.Ordinal);
        var all = string.Concat(collector.Calls);
        Assert.Contains("out", all, StringComparison.Ordinal);
        Assert.Contains("err", all, StringComparison.Ordinal);
        Assert.Contains("err", collector.Calls[^1], StringComparison.Ordinal);
        Assert.Contains("out", collector.Calls[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tail_never_exceeds_the_requested_character_bound_and_keeps_the_newest_text()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = ChildProcess.Request(ChildProcess.WriteStdoutMode, workspace.Root, count: 100_000, timeout: TestLimit) with
        {
            OnOutput = collector.Add,
            OutputInterval = TimeSpan.Zero,
            OutputTailChars = 1000,
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(100_000, result.Stdout.Length);
        var calls = collector.Calls;
        Assert.NotEmpty(calls);
        Assert.All(calls, tail => Assert.True(tail.Length <= 1000));
        Assert.Equal(new string('x', 1000), calls[^1]);
    }

    [Fact]
    public async Task A_run_without_a_listener_captures_exactly_what_a_run_with_one_captures()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var plain = ChildProcess.Request(ChildProcess.WriteBothMode, workspace.Root, count: 50_000, bytes: 7_000, timeout: TestLimit);
        var watched = plain with { OnOutput = static _ => { }, OutputInterval = TimeSpan.Zero };

        var without = await runner.ExecuteAsync(plain, CancellationToken.None);
        var with = await runner.ExecuteAsync(watched, CancellationToken.None);

        Assert.Equal(50_000, without.Stdout.Length);
        Assert.Equal(7_000, without.Stderr.Length);
        Assert.Equal(without.ExitCode, with.ExitCode);
        Assert.Equal(without.Stdout, with.Stdout);
        Assert.Equal(without.Stderr, with.Stderr);
        Assert.Equal(without.StdoutBytes, with.StdoutBytes);
        Assert.Equal(without.StderrBytes, with.StderrBytes);
        Assert.Equal(without.EndReason, with.EndReason);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_fail_the_run_or_change_the_captured_result()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var plain = ChildProcess.Request(ChildProcess.WriteBothMode, workspace.Root, count: 50_000, bytes: 7_000, timeout: TestLimit);
        var calls = 0;
        var throwing = plain with
        {
            OnOutput = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("listener failed");
            },
            OutputInterval = TimeSpan.Zero,
        };

        var baseline = await runner.ExecuteAsync(plain, CancellationToken.None);
        var result = await runner.ExecuteAsync(throwing, CancellationToken.None);

        Assert.True(Volatile.Read(ref calls) > 0);
        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.Equal(baseline.ExitCode, result.ExitCode);
        Assert.Equal(baseline.Stdout, result.Stdout);
        Assert.Equal(baseline.Stderr, result.Stderr);
        Assert.Equal(baseline.StdoutBytes, result.StdoutBytes);
    }

    [Fact]
    public async Task Text_still_unreported_when_the_run_returns_is_dropped_and_nothing_is_reported_afterwards()
    {
        // A long interval means the first chunk reports at once and anything after it is parked on the timer; the run
        // ends long before the interval, so the tail's disposal must drop it instead of reporting after the return.
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = Shell(workspace.Root, windows: "echo one& echo two", unix: "echo one; echo two") with
        {
            OnOutput = collector.Add,
            OutputInterval = TimeSpan.FromSeconds(5),
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Contains("two", result.Stdout, StringComparison.Ordinal);
        var atReturn = collector.Calls;
        Assert.Single(atReturn);
        Assert.StartsWith("one", atReturn[0], StringComparison.Ordinal);

        await Task.Delay(300);

        Assert.Single(collector.Calls);
    }

    [Fact]
    public async Task A_timed_out_run_returns_normally_and_reports_nothing_after_it_returned()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = ChildProcess.Request(ChildProcess.SleepAfterOutputMode, workspace.Root, timeout: TimeSpan.FromSeconds(4)) with
        {
            OnOutput = collector.Add,
            OutputInterval = TimeSpan.FromMilliseconds(100),
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.TimedOut, result.EndReason);
        var atReturn = collector.Calls.Count;

        await Task.Delay(400);

        Assert.Equal(atReturn, collector.Calls.Count);
        Assert.All(collector.Calls, tail => Assert.Contains("started", tail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_run_throws_and_reports_nothing_after_the_throw()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        using var cancellation = new CancellationTokenSource();
        var request = ChildProcess.Request(ChildProcess.SleepAfterOutputMode, workspace.Root, timeout: TestLimit) with
        {
            OnOutput = tail =>
            {
                collector.Add(tail);
                cancellation.Cancel();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().ExecuteAsync(request, cancellation.Token));
        var atThrow = collector.Calls.Count;

        await Task.Delay(300);

        Assert.Equal(atThrow, collector.Calls.Count);
        Assert.NotEmpty(collector.Calls);
    }

    [Fact]
    public async Task A_start_failure_with_a_listener_is_still_a_result_and_reports_nothing()
    {
        using var workspace = new TemporaryWorkspace();
        var collector = new Collector();
        var request = new ProcessRequest
        {
            FileName = Path.Combine(workspace.Root, "no-such-program"),
            Arguments = [],
            WorkingDirectory = workspace.Root,
            OnOutput = collector.Add,
        };

        var result = await new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunner.StartFailedExitCode, result.ExitCode);
        Assert.Empty(collector.Calls);
    }
}
