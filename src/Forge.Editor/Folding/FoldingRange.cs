namespace Forge.Editor.Folding;

/// <summary>What produced a foldable range; the editor draws regions slightly differently from plain blocks.</summary>
public enum FoldingKind
{
    Block,
    Region,
}

/// <summary>
/// One foldable range in 0-based lines. <see cref="EndLine"/> is the last line the range hides, so a range is only
/// usable when it spans at least two lines (<see cref="IsValid"/>). A server, a structural scan and an editor strategy
/// all produce this shape, so the view never needs to know where a range came from.
/// </summary>
public readonly record struct FoldingRange(int StartLine, int EndLine, FoldingKind Kind = FoldingKind.Block, string? Name = null)
{
    public bool IsValid => EndLine > StartLine;

    public override string ToString() => $"{Kind} {StartLine}-{EndLine}" + (Name is null ? string.Empty : " " + Name);
}
