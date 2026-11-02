namespace Forge.Mcp;

/// <summary>Bounded last-N text captured from a server's stderr, exposed for status reporting.</summary>
internal sealed class BoundedLog(int capacityChars)
{
    private readonly Queue<string> lines = new();
    private readonly Lock gate = new();
    private int chars;

    public int Capacity => Math.Max(0, capacityChars);

    public int Length
    {
        get
        {
            lock (gate)
            {
                return chars;
            }
        }
    }

    public void Add(string? line)
    {
        if (capacityChars <= 0 || string.IsNullOrEmpty(line))
        {
            return;
        }

        var entry = line.Length <= capacityChars ? line : line[^capacityChars..];
        lock (gate)
        {
            chars += entry.Length + (lines.Count > 0 ? 1 : 0);
            lines.Enqueue(entry);
            while (chars > capacityChars && lines.Count > 1)
            {
                chars -= lines.Dequeue().Length + 1;
            }
        }
    }

    public string Tail
    {
        get
        {
            lock (gate)
            {
                return lines.Count == 0 ? string.Empty : string.Join('\n', lines);
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (gate)
        {
            return lines.Count == 0 ? [] : [.. lines];
        }
    }
}

/// <summary>Fixed-capacity byte buffer that keeps the most recent bytes of a line that has no newline yet.</summary>
internal sealed class TailLineBuffer
{
    private readonly int capacity;
    private byte[] buffer;
    private int length;

    public TailLineBuffer(int capacity)
    {
        this.capacity = Math.Clamp(capacity, 16, 256 * 1024 * 1024);
        buffer = new byte[Math.Min(this.capacity, 4096)];
    }

    public int Length => length;

    public ReadOnlyMemory<byte> Memory => buffer.AsMemory(0, length);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (data.Length >= capacity)
        {
            EnsureCapacity(capacity);
            data[^capacity..].CopyTo(buffer);
            length = capacity;
            return;
        }

        if (length + data.Length > capacity)
        {
            var keep = capacity - data.Length;
            buffer.AsSpan(length - keep, keep).CopyTo(buffer);
            length = keep;
        }

        EnsureCapacity(length + data.Length);
        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    public void Clear() => length = 0;

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
        {
            return;
        }

        var size = buffer.Length;
        while (size < required)
        {
            size = size <= capacity / 2 ? size * 2 : capacity;
        }

        Array.Resize(ref buffer, size);
    }
}
