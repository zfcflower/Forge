namespace Forge.App.Terminal.Vt;

/// <summary>
/// The primary screen's scrollback: a ring of line objects, oldest first. Pushing while full hands the evicted line
/// object back to the caller, which recycles it for the row scrolled in at the bottom, so steady-state scrolling
/// allocates nothing. Changing the limit re-indexes the ring once (a settings change, not a scrolling path).
/// </summary>
internal sealed class ScrollbackBuffer
{
    private TerminalLine[] lines = [];
    private int start;
    private int count;

    /// <summary>Maximum number of lines kept; the ring is allocated to exactly this size.</summary>
    public int Capacity => lines.Length;

    public int Count => count;

    /// <summary>0 is the oldest line.</summary>
    public TerminalLine Get(int index) => lines[(start + index) % lines.Length];

    /// <summary>
    /// Appends a line. When the ring is full the oldest line object is returned so the caller can reuse it; when the
    /// limit is 0 the line itself is returned (the caller recycles it immediately).
    /// </summary>
    public TerminalLine? Push(TerminalLine line)
    {
        if (lines.Length == 0)
        {
            return line;
        }

        if (count == lines.Length)
        {
            TerminalLine victim = lines[start];
            lines[start] = line;
            start = start + 1 == lines.Length ? 0 : start + 1;
            return victim;
        }

        int slot = start + count;
        if (slot >= lines.Length)
        {
            slot -= lines.Length;
        }

        lines[slot] = line;
        count++;
        return null;
    }

    public void SetLimit(int limit)
    {
        limit = Math.Max(limit, 0);
        if (limit == lines.Length)
        {
            return;
        }

        TerminalLine[] replacement = limit == 0 ? [] : new TerminalLine[limit];
        int keep = Math.Min(count, limit);
        int offset = count - keep;
        for (int i = 0; i < keep; i++)
        {
            replacement[i] = Get(offset + i);
        }

        lines = replacement;
        start = 0;
        count = keep;
    }

    public void Clear()
    {
        Array.Clear(lines);
        start = 0;
        count = 0;
    }
}
