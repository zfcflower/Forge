using System.Buffers;
using System.Text;

namespace Forge.IO;

/// <summary>
/// The rolling tail of a child's output behind <see cref="ProcessRequest.OnOutput"/>. Both pumps append decoded text under one
/// lock; the newest <c>capacity</c> characters are kept and handed to the sink as a snapshot at most once per interval. A
/// chunk that arrives inside the interval arms a one-shot timer, so the last words before a silence are still reported.
/// </summary>
internal sealed class ProcessOutputTail : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<string> sink;
    private readonly int capacity;
    private readonly long intervalTicks;
    private readonly TimeProvider time;
    private readonly ITimer timer;
    private readonly StringBuilder text = new();
    private long lastEmit;
    private bool pending;
    private bool timerArmed;
    private bool disposed;

    public ProcessOutputTail(Action<string> sink, int capacity, TimeSpan interval, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        this.sink = sink;
        this.capacity = Math.Max(1, capacity);
        this.time = time ?? TimeProvider.System;
        intervalTicks = (long)((interval < TimeSpan.Zero ? 0 : interval.TotalSeconds) * this.time.TimestampFrequency);
        lastEmit = this.time.GetTimestamp() - intervalTicks;
        timer = this.time.CreateTimer(static state => ((ProcessOutputTail)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Decodes <paramref name="bytes"/> with the stream's own <paramref name="decoder"/> (it carries a split multi-byte character over) and appends it.</summary>
    public void Append(Decoder decoder, ReadOnlySpan<byte> bytes)
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        var rented = ArrayPool<char>.Shared.Rent(Math.Max(1, decoder.GetCharCount(bytes, flush: false)));
        try
        {
            var count = decoder.GetChars(bytes, rented, flush: false);
            if (count == 0)
            {
                return;
            }

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                AppendLocked(rented.AsSpan(0, count));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Appends under the lock and reports now when the interval has passed, else arms the trailing timer.</summary>
    private void AppendLocked(ReadOnlySpan<char> chunk)
    {
        text.Append(chunk);
        if (text.Length > capacity)
        {
            var drop = text.Length - capacity;
            // Never leave a lone low surrogate at the front.
            if (char.IsLowSurrogate(text[drop]))
            {
                drop++;
            }

            text.Remove(0, drop);
        }

        pending = true;
        var now = time.GetTimestamp();
        if (now - lastEmit >= intervalTicks)
        {
            Report(Take(now));
            return;
        }

        if (!timerArmed)
        {
            timerArmed = true;
            timer.Change(time.GetElapsedTime(now, lastEmit + intervalTicks), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the timer and drops anything not yet reported: the finished result carries the full output. A report already in flight may still complete.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending = false;
        }

        timer.Dispose();
    }

    private void OnTimer()
    {
        lock (gate)
        {
            timerArmed = false;
            if (disposed || !pending)
            {
                return;
            }

            Report(Take(time.GetTimestamp()));
        }
    }

    /// <summary>
    /// Called under the lock, so two pumps and the timer can never deliver snapshots out of order: the sink sees text only
    /// ever getting newer. That is why <see cref="ProcessRequest.OnOutput"/> must not block.
    /// A listener that throws must not fail the child's pump or crash the timer thread: progress is best effort.
    /// </summary>
    private void Report(string snapshot)
    {
        try
        {
            sink(snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private string Take(long now)
    {
        lastEmit = now;
        pending = false;
        return text.ToString();
    }
}
