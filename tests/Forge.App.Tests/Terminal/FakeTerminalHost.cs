using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Terminal;
using Forge.HostClient;
using Forge.Ipc;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// The fake transport behind the terminal UI tests: a recording <see cref="ITerminalHost"/> whose sessions expose a
/// writable output channel, a controllable <c>Exited</c> task and synchronized call logs. The background pump of
/// <see cref="TerminalViewModel"/> touches these from its own thread, so every log is lock-guarded and every wait pumps
/// the headless dispatcher instead of sleeping on a guess.
/// </summary>
internal sealed class FakeTerminalHost : ITerminalHost
{
    private readonly Lock gate = new();
    private readonly List<TerminalStartRequest> starts = [];

    /// <summary>When set, answers <see cref="StartAsync"/>; otherwise the shared <see cref="Session"/> is returned.</summary>
    public Func<TerminalStartRequest, CancellationToken, ValueTask<ITerminalSession>>? StartHandler { get; set; }

    public FakeTerminalSession Session { get; } = new();

    public IReadOnlyList<TerminalStartRequest> Starts
    {
        get
        {
            lock (gate)
            {
                return [.. starts];
            }
        }
    }

    public ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken ct)
    {
        lock (gate)
        {
            starts.Add(request);
        }

        return StartHandler?.Invoke(request, ct) ?? ValueTask.FromResult<ITerminalSession>(Session);
    }
}

/// <summary>
/// A recording <see cref="ITerminalSession"/>: output is a channel the test writes, <see cref="Exited"/> a task the
/// test completes, and every input/resize/probe/dispose call is logged for exact assertions.
/// </summary>
internal sealed class FakeTerminalSession : ITerminalSession
{
    private readonly Channel<ReadOnlyMemory<byte>> output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly List<byte[]> writes = [];
    private readonly List<TerminalSize> resizes = [];
    private int stopCalls;
    private int foregroundCalls;
    private int barriers;
    private TaskCompletionSource? writeSignal;

    public string Id { get; } = "term-" + Guid.NewGuid().ToString("N")[..8];

    public ChannelReader<ReadOnlyMemory<byte>> Output => output.Reader;

    public Task<int> Exited => exited.Task;

    /// <summary>When set, answers the foreground probe; otherwise <see cref="HasForeground"/> is returned.</summary>
    public Func<CancellationToken, ValueTask<bool>>? ForegroundProbe { get; set; }

    public bool HasForeground { get; set; }

    public int StopCalls => Volatile.Read(ref stopCalls);

    public int ForegroundCalls => Volatile.Read(ref foregroundCalls);

    /// <summary>The input written so far, as it stands right now. No waiting: use <see cref="WaitForWrittenAsync"/> or
    /// <see cref="TerminalUi.WrittenAfterBarrierAsync"/> when the bytes are still on their way through the view model's
    /// ordered writer task.</summary>
    public byte[] WrittenSnapshot()
    {
        lock (gate)
        {
            return ConcatLocked();
        }
    }

    /// <summary>Waits (signalled by each write, no polling) until the bytes written so far satisfy <paramref name="condition"/>
    /// and returns them. Throws <see cref="TimeoutException"/>, quoting what did arrive, when they never do.</summary>
    public async Task<byte[]> WaitForWrittenAsync(Func<byte[], bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            Task signal;
            lock (gate)
            {
                var snapshot = ConcatLocked();
                if (condition(snapshot))
                {
                    return snapshot;
                }

                signal = (writeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            try
            {
                await signal.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"The expected input never reached the session within {timeout.TotalSeconds:0} s; saw: {Convert.ToHexString(WrittenSnapshot())}.");
            }
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> bytes were written and returns everything written so far.</summary>
    public Task<byte[]> WaitForWrittenBytesAsync(int count, TimeSpan timeout) =>
        WaitForWrittenAsync(bytes => bytes.Length >= count, timeout);

    /// <summary>Hands out the next barrier number; see <see cref="TerminalUi.WrittenAfterBarrierAsync"/>.</summary>
    public int NextBarrier() => Interlocked.Increment(ref barriers);

    private byte[] ConcatLocked()
    {
        var total = 0;
        foreach (var chunk in writes)
        {
            total += chunk.Length;
        }

        var all = new byte[total];
        var offset = 0;
        foreach (var chunk in writes)
        {
            chunk.CopyTo(all, offset);
            offset += chunk.Length;
        }

        return all;
    }

    public int WrittenCount()
    {
        lock (gate)
        {
            return writes.Count;
        }
    }

    public void DropWritten()
    {
        lock (gate)
        {
            writes.Clear();
        }
    }

    public IReadOnlyList<TerminalSize> Resizes
    {
        get
        {
            lock (gate)
            {
                return [.. resizes];
            }
        }
    }

    public void Emit(string text) => Emit(Encoding.UTF8.GetBytes(text));

    public void Emit(byte[] bytes) => output.Writer.TryWrite(new ReadOnlyMemory<byte>(bytes));

    /// <summary>Drains the output cleanly, as a normally exiting process does.</summary>
    public void CompleteOutput() => output.Writer.Complete();

    /// <summary>Completes the output with the transport error, as a crashed host connection does.</summary>
    public void FailOutput(Exception error) => output.Writer.TryComplete(error);

    public void Exit(int code)
    {
        output.Writer.TryComplete();
        exited.TrySetResult(code);
    }

    public void FailExit(Exception error)
    {
        output.Writer.TryComplete();
        exited.TrySetException(error);
    }

    /// <summary>When set, every write awaits this before it is recorded - lets a test hold one write back and prove that
    /// later input does not overtake it.</summary>
    public Func<byte[], Task>? WriteInterceptor { get; set; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        var copy = input.ToArray();
        if (WriteInterceptor is { } hook)
        {
            await hook(copy).WaitAsync(ct).ConfigureAwait(false);
        }

        TaskCompletionSource? signal;
        lock (gate)
        {
            writes.Add(copy);
            signal = writeSignal;
            writeSignal = null;
        }

        signal?.TrySetResult();
    }

    public ValueTask ResizeAsync(TerminalSize size, CancellationToken ct)
    {
        lock (gate)
        {
            resizes.Add(size);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasForegroundProcessAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref foregroundCalls);
        return ForegroundProbe?.Invoke(ct) ?? ValueTask.FromResult(HasForeground);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref stopCalls);
        output.Writer.TryComplete();
        exited.TrySetResult(0);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Small headless driver shared by the terminal UI tests: single-dispatch test runs (so a stuck UI step fails
/// instead of hanging the run), a dispatcher+render pump, and an in-dispatch condition wait that keeps pumping
/// until the background terminal pump has done its work.
/// </summary>
internal static class TerminalUi
{
    private const int SettlePasses = 2;

    /// <summary>Drains the dispatcher and renders, twice: layout and data-template realization post follow-up work.</summary>
    public static void Settle(Window window)
    {
        for (var pass = 0; pass < SettlePasses; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Shows a window with <paramref name="content"/> and settles it. The caller closes the window
    /// (closing inside the session keeps the headless platform's window tracking consistent).
    /// </summary>
    public static Window Show(Window window)
    {
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>
    /// Runs a whole test's UI work inside ONE dispatch (the shape the existing workbench tests use): a shown window
    /// changes which dispatcher a later dispatch pumps, so a callback the terminal pump queued does not reliably
    /// survive into the next dispatch. Starting terminals, waiting, acting and cleaning up therefore share one
    /// dispatch; the caller asserts on the returned plain data outside. The budget (plus a margin) bounds the test.
    /// </summary>
    public static Task<T> RunTestAsync<T>(
        Avalonia.Headless.HeadlessUnitTestSession session,
        Func<Task<T>> body,
        TimeSpan budget) =>
        session.Dispatch(body, new CancellationTokenSource(budget + TimeSpan.FromSeconds(10)).Token);

    /// <summary>
    /// In-dispatch condition wait: call only on the UI thread inside <see cref="RunTestAsync"/>. Awaiting inside the
    /// dispatcher pumps a nested frame, which is what lets background pump callbacks and DispatcherTimer ticks run
    /// headless. The condition (not the delay) decides when the test proceeds.
    /// </summary>
    public static async Task<bool> WaitInsideAsync(Window? window, Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (window is not null)
            {
                Settle(window);
            }
            else
            {
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        if (window is not null)
        {
            Settle(window);
        }

        return condition();
    }

    private static readonly byte[] BarrierMark = [0xF8, 0xF9, 0xFA, 0xFB];

    /// <summary>
    /// Returns everything the session has received once all input produced so far has been written. The view model feeds
    /// the session through one ordered writer task, so a marker pushed behind the earlier input only shows up after it:
    /// waiting for the marker is a deterministic "everything before this has been written" - including the case where the
    /// expected answer is "nothing". The marker bytes (never valid UTF-8, never produced by the terminal) are stripped.
    /// </summary>
    public static async Task<byte[]> WrittenAfterBarrierAsync(TerminalViewModel viewModel, FakeTerminalSession session)
    {
        var expected = session.NextBarrier();
        viewModel.SendInput(BarrierMark);
        var snapshot = await session.WaitForWrittenAsync(bytes => CountMarks(bytes) >= expected, TimeSpan.FromSeconds(10));
        var result = new List<byte>(snapshot.Length);
        for (var index = 0; index < snapshot.Length;)
        {
            if (index + BarrierMark.Length <= snapshot.Length && snapshot.AsSpan(index, BarrierMark.Length).SequenceEqual(BarrierMark))
            {
                index += BarrierMark.Length;
                continue;
            }

            result.Add(snapshot[index++]);
        }

        return [.. result];
    }

    private static int CountMarks(byte[] bytes)
    {
        var count = 0;
        for (var index = 0; index + BarrierMark.Length <= bytes.Length;)
        {
            if (bytes.AsSpan(index, BarrierMark.Length).SequenceEqual(BarrierMark))
            {
                count++;
                index += BarrierMark.Length;
            }
            else
            {
                index++;
            }
        }

        return count;
    }

    public static T? FindByAutomationId<T>(Visual root, string automationId)
        where T : Visual
    {
        foreach (var candidate in root.GetVisualDescendants().OfType<T>())
        {
            if (Avalonia.Automation.AutomationProperties.GetAutomationId(candidate) == automationId)
            {
                return candidate;
            }
        }

        return null;
    }
}
