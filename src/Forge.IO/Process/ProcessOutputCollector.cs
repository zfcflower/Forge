using System.Buffers;
using System.Text;

namespace Forge.IO;

/// <summary>What one stream produced: the decoded text, every byte the child wrote, and whether the cap dropped the rest.</summary>
internal readonly record struct CapturedStream(string Text, long TotalBytes, bool Truncated);

/// <summary>
/// The bounded store behind one captured stream. It keeps the first <c>capacity</c> bytes in a single pooled array and
/// counts every byte read, including the ones it drops: the pump has to keep reading after the cap, or a child that writes
/// more than the cap blocks forever on a full pipe. Nothing here grows with the output: the array is rented once at the
/// cap and decoding happens once, at the end.
/// <para>The pump and the caller can be alive at the same time only when a descendant keeps the pipe open past the wait
/// budget. <see cref="Abandon"/> closes that window: after it, the pump's appends become no-ops, so <see cref="Release"/>
/// can put the pooled array back safely even though the pump task has not finished.</para>
/// </summary>
internal sealed class ProcessOutputCollector
{
    private readonly Lock gate = new();
    private readonly byte[] buffer;
    private readonly int capacity;
    private int written;
    private long total;
    private bool truncated;
    private bool abandoned;
    private bool released;

    public ProcessOutputCollector(int capacity)
    {
        this.capacity = Math.Max(0, capacity);
        buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, this.capacity));
    }

    /// <summary>Stores what fits and counts the rest. Only the pump calls this, so every counter has one writer.</summary>
    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        lock (gate)
        {
            if (abandoned)
            {
                return;
            }

            var count = written;
            var room = capacity - count;
            if (room > 0)
            {
                var take = Math.Min(room, chunk.Length);
                chunk[..take].CopyTo(buffer.AsSpan(count, take));
                written = count + take;
                if (take < chunk.Length)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }

            total += chunk.Length;
        }
    }

    /// <summary>The text and byte count as they stand now. A still-running pump may append more after this call, which the
    /// caller only does when it is about to abandon that pump.</summary>
    public CapturedStream Capture()
    {
        lock (gate)
        {
            return new CapturedStream(Decode(buffer.AsSpan(0, written)), total, truncated);
        }
    }

    /// <summary>
    /// Stops accepting output. Called when the pump outlived its wait budget: the process tree is gone, but a descendant may
    /// still hold the pipe, and this is what makes it safe to hand the array back to the pool before that pump finishes.
    /// </summary>
    public void Abandon()
    {
        lock (gate)
        {
            abandoned = true;
        }
    }

    /// <summary>Returns the pooled array, once. Safe after <see cref="Abandon"/>; otherwise only once no pump can append.</summary>
    public void Release()
    {
        lock (gate)
        {
            if (released)
            {
                return;
            }

            released = true;
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> preamble = [0xEF, 0xBB, 0xBF];
        if (bytes.StartsWith(preamble))
        {
            bytes = bytes[3..];
        }

        // Invalid sequences decode to U+FFFD rather than throwing: half a multi-byte character at the truncation point is
        // expected output, not a failure of the run.
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }
}
