namespace Forge.App.Terminal.Vt;

/// <summary>
/// Writes ASCII protocol bytes (answers, key and mouse sequences) into a caller-provided stack buffer: no allocation,
/// no culture-sensitive formatting, no <see cref="string"/> in the path.
/// </summary>
internal ref struct AsciiWriter
{
    private const string HexDigits = "0123456789abcdef";

    private readonly Span<byte> buffer;
    private int length;

    public AsciiWriter(Span<byte> buffer)
    {
        this.buffer = buffer;
        length = 0;
    }

    public readonly ReadOnlySpan<byte> Written => buffer[..length];

    public void Add(byte value)
    {
        buffer[length++] = value;
    }

    public void Add(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(buffer[length..]);
        length += bytes.Length;
    }

    /// <summary>Appends a non-negative decimal number.</summary>
    public void AddNumber(int value)
    {
        if (value < 0)
        {
            value = 0;
        }

        Span<byte> digits = stackalloc byte[10];
        int count = 0;
        do
        {
            digits[count++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        while (value > 0);

        for (int i = count - 1; i >= 0; i--)
        {
            buffer[length++] = digits[i];
        }
    }

    /// <summary>Appends one colour component as the four hex digits xterm uses in <c>rgb:rrrr/gggg/bbbb</c>.</summary>
    public void AddHex16(byte value)
    {
        int wide = value * 0x101;
        buffer[length++] = (byte)HexDigits[(wide >> 12) & 0xF];
        buffer[length++] = (byte)HexDigits[(wide >> 8) & 0xF];
        buffer[length++] = (byte)HexDigits[(wide >> 4) & 0xF];
        buffer[length++] = (byte)HexDigits[wide & 0xF];
    }
}
