namespace Forge.Editor.Text;

public enum AnchorBias
{
    /// <summary>Stay before text inserted at the anchor position.</summary>
    Left,

    /// <summary>Stay after text inserted at the anchor position.</summary>
    Right,
}

/// <summary>Position that follows edits made through the owning <see cref="TextDocument"/>.</summary>
public sealed class Anchor : IDisposable
{
    private readonly TextDocument _document;

    internal Anchor(TextDocument document, int offset, AnchorBias bias)
    {
        _document = document;
        Offset = offset;
        Bias = bias;
    }

    public int Offset { get; internal set; }

    public AnchorBias Bias { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _document.RemoveAnchor(this);
    }
}

/// <summary>Range that follows edits; by default text inserted exactly at a boundary stays outside the range.</summary>
public sealed class RangeAnchor : IDisposable
{
    private readonly TextDocument _document;

    internal RangeAnchor(TextDocument document, int start, int end, AnchorBias startBias, AnchorBias endBias)
    {
        _document = document;
        Start = start;
        End = end;
        StartBias = startBias;
        EndBias = endBias;
    }

    public int Start { get; internal set; }

    public int End { get; internal set; }

    public int Length => End - Start;

    public AnchorBias StartBias { get; }

    public AnchorBias EndBias { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _document.RemoveAnchor(this);
    }
}
