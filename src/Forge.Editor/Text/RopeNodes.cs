namespace Forge.Editor.Text;

internal sealed class LeafRope : Rope
{
    public LeafRope(ReadOnlyMemory<char> text)
    {
        Text = text;
        Length = text.Length;
        LineBreakCount = text.Span.Count('\n');
    }

    public ReadOnlyMemory<char> Text { get; }

    public override int Length { get; }

    public override int LineBreakCount { get; }

    internal override int Height => 1;
}

internal sealed class BranchRope : Rope
{
    public BranchRope(Rope left, Rope right)
    {
        Left = left;
        Right = right;
        Length = left.Length + right.Length;
        LineBreakCount = left.LineBreakCount + right.LineBreakCount;
        Height = Math.Max(left.Height, right.Height) + 1;
    }

    public Rope Left { get; }

    public Rope Right { get; }

    public override int Length { get; }

    public override int LineBreakCount { get; }

    internal override int Height { get; }
}
