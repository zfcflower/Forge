using System.Collections.Concurrent;
using Avalonia.Threading;

namespace Forge.App.ViewModels;

/// <summary>
/// Coalesces work items produced on any thread into at most one UI-thread flush per dispatcher pass. Producers enqueue
/// into a lock-free queue; only the first item schedules a dispatcher callback, which then drains everything accumulated
/// so far in FIFO order. A bounded per-flush item count keeps one burst from occupying the UI thread, and disposal drops
/// whatever has not been processed yet.
/// </summary>
internal sealed class FrameCoalescer<T>
{
    /// <summary>Upper bound of items a single flush handles; the remainder waits for the next dispatcher pass.</summary>
    public const int MaxItemsPerFlush = 2048;

    private readonly ConcurrentQueue<T> queue = new();
    private readonly Action<T> handler;
    private readonly Action? flushOnce;
    private readonly Action flush;
    private int flushScheduled;
    private int disposed;

    public FrameCoalescer(Action<T> handler, Action? flushOnce = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.handler = handler;
        this.flushOnce = flushOnce;
        flush = Flush;
    }

    /// <summary>Enqueues one item; it is handled on the UI thread in the next coalesced flush, in enqueue order.</summary>
    public void Enqueue(T item)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        queue.Enqueue(item);
        Schedule();
    }

    /// <summary>
    /// Schedules a flush without enqueuing an item, for producers whose payload lives outside the queue (the pending text
    /// buffer in this module). The flush still runs the per-flush hook, so that payload is drained.
    /// </summary>
    public void RequestFlush()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Schedule();
    }

    /// <summary>Stops scheduling and drops every queued item; a flush already in flight becomes a no-op.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        while (queue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref flushScheduled, 0);
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref flushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(flush, DispatcherPriority.Background);
        }
    }

    private void Flush()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref flushScheduled, 0);
        flushOnce?.Invoke();

        var processed = 0;
        while (processed < MaxItemsPerFlush && queue.TryDequeue(out var item))
        {
            handler(item);
            processed++;
        }

        // Items that arrived while this pass ran, or the remainder above the cap, wait for the next pass.
        if (Volatile.Read(ref disposed) == 0 && !queue.IsEmpty)
        {
            Schedule();
        }
    }
}
