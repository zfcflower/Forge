namespace Forge.Editor.Text;

/// <summary>
/// One replacement of <c>[Offset, Offset + Length)</c> with <see cref="Text"/>. Offsets always refer to the document
/// state the change is applied to, which for a batch is the state before the batch.
/// </summary>
public readonly record struct TextChange
{
    public TextChange(int offset, int length, string? text = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Offset = offset;
        Length = length;
        Text = text ?? string.Empty;
    }

    public int Offset { get; }

    public int Length { get; }

    public string Text { get; }

    public int End => Offset + Length;

    public int NewEnd => Offset + Text.Length;

    public int Delta => Text.Length - Length;

    public bool IsInsert => Length == 0 && Text.Length > 0;

    public bool IsDelete => Length > 0 && Text.Length == 0;

    public static TextChange Insert(int offset, string text) => new(offset, 0, text);

    public static TextChange Delete(int offset, int length) => new(offset, length);

    public static TextChange Replace(int offset, int length, string text) => new(offset, length, text);

    public override string ToString() => $"{Offset}..{End} -> {Text.Length} chars";
}
