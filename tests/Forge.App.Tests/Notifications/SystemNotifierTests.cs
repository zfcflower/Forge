using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Forge.App.Notifications;
using Forge.App.Tests.Git;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins <see cref="SystemNotifier"/>: the exact process each platform is asked to start (file, argument list, the
/// Windows environment variables), that title and body never become part of a script or of a command line a shell would
/// parse (hostile text included), that a missing tool, a failing tool and an unsupported platform end
/// quietly with a <c>[notify]</c> trace line, that the 5 second limit and the caller's cancellation are told apart, and that
/// only cancellation by the caller ever throws. A recording runner replaces the process start, so nothing is launched and no
/// test depends on the machine having PowerShell, osascript or notify-send.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SystemNotifierTests
{
    private const string Title = "Forge: pull request #42";
    private const string Body = "All checks passed.";

    /// <summary>Text that would break out of a quoted script, a shell line, an AppleScript string or toast XML.</summary>
    public static TheoryData<string> HostileTexts() => new()
    {
        "\"; do shell script \"touch /tmp/pwned\"; \"",
        "$(touch /tmp/pwned)",
        "`touch /tmp/pwned`",
        "'; rm -rf / #",
        "line one\nline two\r\nline three",
        "<toast launch=\"x\"><text>&amp;</text></toast> & <b>",
        "-e 'beep' --help",
        "通知 ✓ \U0001F514 é̈",
        "$env:USERPROFILE %PATH% ${HOME} {0} {1}",
        "back\\slash \\\" \\\\",
    };

    // ------------------------------------------------------------ exact process per platform

    [Fact]
    public async Task Windows_starts_powershell_with_the_fixed_script_and_the_text_in_two_environment_variables()
    {
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Windows);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        var info = Assert.Single(recorder.Started);
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-Command", SystemNotifier.WindowsScript],
            info.ArgumentList.ToArray());
        Assert.Equal(string.Empty, info.Arguments);
        Assert.Equal(Title, info.Environment[SystemNotifier.TitleVariable]);
        Assert.Equal(Body, info.Environment[SystemNotifier.BodyVariable]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void The_windows_variable_names_and_the_script_that_reads_them_are_pinned()
    {
        Assert.Equal("FORGE_NOTIFY_TITLE", SystemNotifier.TitleVariable);
        Assert.Equal("FORGE_NOTIFY_BODY", SystemNotifier.BodyVariable);

        // The script is fixed text that reads the variables; it puts them in with CreateTextNode (XML escaping is the
        // platform's job) and never builds a string out of them.
        Assert.Contains("$env:FORGE_NOTIFY_TITLE", SystemNotifier.WindowsScript, StringComparison.Ordinal);
        Assert.Contains("$env:FORGE_NOTIFY_BODY", SystemNotifier.WindowsScript, StringComparison.Ordinal);
        Assert.Contains("CreateTextNode($env:FORGE_NOTIFY_TITLE)", SystemNotifier.WindowsScript, StringComparison.Ordinal);
        Assert.Contains("CreateTextNode($env:FORGE_NOTIFY_BODY)", SystemNotifier.WindowsScript, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', SystemNotifier.WindowsScript);
        Assert.DoesNotContain("LoadXml", SystemNotifier.WindowsScript, StringComparison.Ordinal);
        Assert.DoesNotContain("InnerXml", SystemNotifier.WindowsScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacOS_starts_osascript_with_a_run_handler_that_receives_title_and_body_as_arguments()
    {
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.MacOS);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        var info = Assert.Single(recorder.Started);
        Assert.Equal("osascript", info.FileName);
        Assert.Equal(
            [
                "-e", "on run argv",
                "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                "-e", "end run",
                Title,
                Body,
            ],
            info.ArgumentList.ToArray());
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);

        // No notification variables on this platform: the text travels as arguments only.
        Assert.False(info.Environment.ContainsKey(SystemNotifier.TitleVariable));
        Assert.False(info.Environment.ContainsKey(SystemNotifier.BodyVariable));
    }

    [Fact]
    public async Task Linux_starts_notify_send_with_a_double_dash_before_title_and_body()
    {
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Linux);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        var info = Assert.Single(recorder.Started);
        Assert.Equal("notify-send", info.FileName);
        Assert.Equal(["--app-name=Forge", "--", Title, Body], info.ArgumentList.ToArray());
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.Environment.ContainsKey(SystemNotifier.TitleVariable));
    }

    [Fact]
    public void The_default_platform_is_the_one_the_process_runs_on()
    {
        var info = new SystemNotifier().CreateStartInfo(Title, Body);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("powershell.exe", info!.FileName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("osascript", info!.FileName);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("notify-send", info!.FileName);
        }
        else
        {
            Assert.Null(info);
        }
    }

    // ------------------------------------------------------------ hostile text never reaches a script

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void Hostile_text_is_never_part_of_the_windows_command_line(string hostile)
    {
        var notifier = new SystemNotifier(platform: NotifyPlatform.Windows);

        var info = notifier.CreateStartInfo(hostile, hostile + " body")!;

        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-Command", SystemNotifier.WindowsScript],
            info.ArgumentList.ToArray());
        foreach (var argument in info.ArgumentList)
        {
            Assert.DoesNotContain(hostile, argument, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(hostile, info.Arguments, StringComparison.Ordinal);
        Assert.Equal(hostile, info.Environment[SystemNotifier.TitleVariable]);
        Assert.Equal(hostile + " body", info.Environment[SystemNotifier.BodyVariable]);
    }

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void Hostile_text_is_never_part_of_the_applescript(string hostile)
    {
        var benign = new SystemNotifier(platform: NotifyPlatform.MacOS).CreateStartInfo("t", "b")!;
        var info = new SystemNotifier(platform: NotifyPlatform.MacOS).CreateStartInfo(hostile, "x" + hostile)!;

        // The six script arguments are byte-for-byte what a harmless notification gets; only the two trailing arguments differ.
        Assert.Equal(8, info.ArgumentList.Count);
        Assert.Equal(benign.ArgumentList.Take(6), info.ArgumentList.Take(6));
        foreach (var script in info.ArgumentList.Take(6))
        {
            Assert.DoesNotContain(hostile, script, StringComparison.Ordinal);
        }

        Assert.Equal(hostile, info.ArgumentList[6]);
        Assert.Equal("x" + hostile, info.ArgumentList[7]);
    }

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void Hostile_text_follows_the_double_dash_on_linux_and_stays_one_argument_each(string hostile)
    {
        var info = new SystemNotifier(platform: NotifyPlatform.Linux).CreateStartInfo(hostile, "x" + hostile)!;

        Assert.Equal(["--app-name=Forge", "--", hostile, "x" + hostile], info.ArgumentList.ToArray());
        Assert.Equal(1, info.ArgumentList.IndexOf("--"));
    }

    [Fact]
    public async Task Hostile_text_reaches_the_runner_unchanged_on_every_platform()
    {
        foreach (var platform in new[] { NotifyPlatform.Windows, NotifyPlatform.MacOS, NotifyPlatform.Linux })
        {
            var recorder = new RunnerRecorder();
            var notifier = new SystemNotifier(run: recorder.RunAsync, platform: platform);

            await notifier.NotifyAsync("$(id) `id` '; rm -rf /", "<&>\n\"", CancellationToken.None);

            var info = Assert.Single(recorder.Started);
            string[] carried = platform == NotifyPlatform.Windows
                ? [info.Environment[SystemNotifier.TitleVariable]!, info.Environment[SystemNotifier.BodyVariable]!]
                : info.ArgumentList.TakeLast(2).ToArray();
            Assert.Equal(["$(id) `id` '; rm -rf /", "<&>\n\""], carried);
        }
    }

    [Fact]
    public async Task Empty_title_and_body_are_passed_through_and_null_is_rejected()
    {
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Linux);

        await notifier.NotifyAsync(string.Empty, string.Empty, CancellationToken.None);
        Assert.Equal(["--app-name=Forge", "--", string.Empty, string.Empty], Assert.Single(recorder.Started).ArgumentList.ToArray());

        await Assert.ThrowsAsync<ArgumentNullException>(() => notifier.NotifyAsync(null!, "b", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => notifier.NotifyAsync("t", null!, CancellationToken.None));
        Assert.Single(recorder.Started);
    }

    // ------------------------------------------------------------ unsupported, failing

    [Fact]
    public async Task An_unsupported_platform_makes_no_call_and_is_traced_as_skipped()
    {
        using var trace = new NotifyTraceCapture();
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Unsupported);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Empty(recorder.Started);
        Assert.Null(notifier.CreateStartInfo(Title, Body));
        Assert.Contains(trace.Lines, static line => line.Contains("[notify] skipped: no notification tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runner_that_throws_does_not_throw_and_is_traced_with_the_tool_name()
    {
        using var trace = new NotifyTraceCapture();
        var notifier = new SystemNotifier(
            run: static (_, _) => throw new InvalidOperationException("boom-from-runner"),
            platform: NotifyPlatform.Linux);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] notify-send failed: boom-from-runner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_tool_is_a_traced_skip_not_an_exception()
    {
        // What Process.Start throws when the executable is not on PATH.
        using var trace = new NotifyTraceCapture();
        var notifier = new SystemNotifier(
            run: static (_, _) => throw new Win32Exception(2, "The system cannot find the file specified."),
            platform: NotifyPlatform.MacOS);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] osascript failed:", StringComparison.Ordinal)
                && line.Contains("cannot find the file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runner_that_faults_asynchronously_is_handled_the_same_way()
    {
        using var trace = new NotifyTraceCapture();
        var notifier = new SystemNotifier(
            run: static async (_, _) =>
            {
                await Task.Yield();
                throw new IOException("pipe-broke");
            },
            platform: NotifyPlatform.Windows);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] powershell.exe failed: pipe-broke", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task A_non_zero_exit_code_is_traced_and_never_throws(int exitCode)
    {
        using var trace = new NotifyTraceCapture();
        var notifier = new SystemNotifier(run: (_, _) => Task.FromResult(exitCode), platform: NotifyPlatform.Linux);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Contains(
            trace.Lines,
            line => line.Contains("[notify] notify-send exited with code " + exitCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_zero_exit_code_leaves_no_trace_line()
    {
        using var trace = new NotifyTraceCapture();
        var notifier = new SystemNotifier(run: static (_, _) => Task.FromResult(0), platform: NotifyPlatform.Linux);

        await notifier.NotifyAsync("quiet-title", "quiet-body", CancellationToken.None);

        Assert.DoesNotContain(trace.Lines, static line => line.StartsWith("[notify]", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ the 5 second limit and cancellation

    [Fact]
    public void The_limit_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SystemNotifier.Timeout);
    }

    [Fact]
    public async Task A_tool_that_hangs_is_abandoned_after_five_seconds_of_the_injected_clock()
    {
        using var trace = new NotifyTraceCapture();
        var time = new NotifyControlledTime();
        var recorder = new RunnerRecorder { WaitForToken = true };
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Windows, time: time);

        var pending = notifier.NotifyAsync(Title, Body, CancellationToken.None);
        await recorder.Entered.WhenAtLeastAsync(1, "the runner to start");

        // The limit is armed on the injected clock, for exactly 5 seconds, and the call is still waiting.
        Assert.Contains(SystemNotifier.Timeout, time.RequestedDueTimes);
        time.Advance(TimeSpan.FromSeconds(4.999));
        Assert.False(pending.IsCompleted);
        Assert.False(recorder.LastToken.IsCancellationRequested);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await pending.WaitAsync(NotifyBudget);

        Assert.True(recorder.LastToken.IsCancellationRequested);
        Assert.Contains(
            trace.Lines,
            static line => line.Contains("[notify] powershell.exe did not finish within 5 seconds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_that_finishes_in_time_leaves_no_armed_timer_behind()
    {
        var time = new NotifyControlledTime();
        var notifier = new SystemNotifier(run: static (_, _) => Task.FromResult(0), platform: NotifyPlatform.Linux, time: time);

        await notifier.NotifyAsync(Title, Body, CancellationToken.None);

        Assert.Equal(0, time.ActiveTimers);

        // Time passing afterwards cannot cancel anything or throw.
        time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_and_reaches_the_runner()
    {
        using var trace = new NotifyTraceCapture();
        var time = new NotifyControlledTime();
        var recorder = new RunnerRecorder { WaitForToken = true };
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Linux, time: time);
        using var cancellation = new CancellationTokenSource();

        var pending = notifier.NotifyAsync(Title, Body, cancellation.Token);
        await recorder.Entered.WhenAtLeastAsync(1, "the runner to start");
        Assert.False(pending.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(NotifyBudget));
        Assert.True(recorder.LastToken.IsCancellationRequested);

        // Told apart from the limit: no "did not finish" line for a user's cancellation.
        Assert.DoesNotContain(trace.Lines, static line => line.Contains("did not finish within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_throws_before_the_runner_is_called()
    {
        var recorder = new RunnerRecorder();
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Linux);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notifier.NotifyAsync(Title, Body, cancellation.Token));

        Assert.Empty(recorder.Started);
    }

    [Fact]
    public async Task A_runner_that_throws_the_cancellation_of_the_caller_propagates_it()
    {
        using var cancellation = new CancellationTokenSource();
        var notifier = new SystemNotifier(
            run: (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            },
            platform: NotifyPlatform.Linux);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notifier.NotifyAsync(Title, Body, cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_of_the_caller_wins_over_a_limit_that_expires_at_the_same_time()
    {
        var time = new NotifyControlledTime();
        var recorder = new RunnerRecorder { WaitForToken = true };
        var notifier = new SystemNotifier(run: recorder.RunAsync, platform: NotifyPlatform.Windows, time: time);
        using var cancellation = new CancellationTokenSource();

        var pending = notifier.NotifyAsync(Title, Body, cancellation.Token);
        await recorder.Entered.WhenAtLeastAsync(1, "the runner to start");
        await cancellation.CancelAsync();
        time.Advance(TimeSpan.FromSeconds(5));

        // The caller asked to stop, so the caller is told (the limit's quiet skip is only for a tool that overran).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(NotifyBudget));
    }

    /// <summary>Upper bound of every wait on a fake; reaching it fails the test instead of hanging.</summary>
    private static readonly TimeSpan NotifyBudget = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------ helpers

    /// <summary>A <c>run</c> seam that records the start info it is given and can wait for the token it is given.</summary>
    private sealed class RunnerRecorder
    {
        private readonly List<ProcessStartInfo> started = [];
        private readonly object gate = new();

        public NotifyCounter Entered { get; } = new();

        /// <summary>When set, the runner waits until its token is cancelled (a tool that hangs) and then throws.</summary>
        public bool WaitForToken { get; init; }

        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<ProcessStartInfo> Started
        {
            get
            {
                lock (gate)
                {
                    return [.. started];
                }
            }
        }

        public async Task<int> RunAsync(ProcessStartInfo info, CancellationToken ct)
        {
            lock (gate)
            {
                started.Add(info);
                LastToken = ct;
            }

            Entered.Hit();
            if (WaitForToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }

            return 0;
        }
    }
}

/// <summary>
/// A counter that can be awaited: <see cref="WhenAtLeastAsync"/> completes once <see cref="Hit"/> was called that often. It
/// lives here because the notifier's own tests no longer share a kit with a git-panel test file.
/// </summary>
internal sealed class NotifyCounter
{
    private readonly object gate = new();
    private readonly List<(int Target, TaskCompletionSource Source)> waiters = [];
    private int count;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    public int Hit()
    {
        List<TaskCompletionSource> ready = [];
        int total;
        lock (gate)
        {
            total = ++count;
            for (var index = waiters.Count - 1; index >= 0; index--)
            {
                if (count >= waiters[index].Target)
                {
                    ready.Add(waiters[index].Source);
                    waiters.RemoveAt(index);
                }
            }
        }

        foreach (var source in ready)
        {
            source.TrySetResult();
        }

        return total;
    }

    /// <summary>Completes when the counter reached <paramref name="target"/>; fails the test (not hangs) after the budget.</summary>
    public async Task WhenAtLeastAsync(int target, string what)
    {
        Task task;
        lock (gate)
        {
            if (count >= target)
            {
                return;
            }

            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((target, source));
            task = source.Task;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"timed out waiting for {what} (wanted {target}, saw {Count})");
        }
    }
}

/// <summary>Captures <see cref="Trace"/> output for the duration of a test and removes itself.</summary>
internal sealed class NotifyTraceCapture : TraceListener
{
    private readonly ConcurrentQueue<string> lines = new();

    public NotifyTraceCapture()
    {
        Trace.Listeners.Add(this);
    }

    public IReadOnlyList<string> Lines => [.. lines];

    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            lines.Enqueue(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Trace.Listeners.Remove(this);
        }

        base.Dispose(disposing);
    }
}

/// <summary>A clock a test moves by hand: timers fire only when the test advances past their due time.</summary>
internal sealed class NotifyControlledTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ControlledTimer> timers = [];
    private readonly List<TimeSpan> requested = [];
    private TimeSpan now;

    /// <summary>The due times of every timer that was created or changed to a finite delay.</summary>
    public IReadOnlyList<TimeSpan> RequestedDueTimes
    {
        get
        {
            lock (gate)
            {
                return [.. requested];
            }
        }
    }

    public int ActiveTimers
    {
        get
        {
            lock (gate)
            {
                return timers.Count(static timer => !timer.Disposed && !timer.Fired && timer.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero) + now;
        }
    }

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return now.Ticks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires, on the calling thread, every timer that became due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ControlledTimer> due = [];
        lock (gate)
        {
            now += by;
            foreach (var timer in timers)
            {
                if (!timer.Disposed && !timer.Fired && timer.Due is { } at && at <= now)
                {
                    timer.Fired = true;
                    due.Add(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Record(TimeSpan dueTime)
    {
        lock (gate)
        {
            requested.Add(dueTime);
        }
    }

    private TimeSpan Now()
    {
        lock (gate)
        {
            return now;
        }
    }

    private sealed class ControlledTimer(NotifyControlledTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? Due { get; private set; }

        public bool Disposed { get; private set; }

        public bool Fired { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Due = null;
                return true;
            }

            owner.Record(dueTime);
            Fired = false;
            Due = owner.Now() + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

