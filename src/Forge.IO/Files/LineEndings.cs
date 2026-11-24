using System.Buffers;

namespace Forge.IO;

/// <summary>
/// The one line ending the file tools speak: LF.
/// </summary>
internal static class LineEndings
{
    /// <summary>
    /// Rewrites <c>\r\n</c> and a lone <c>\r</c> to <c>\n</c>. Returns the same instance when there is no <c>\r</c>, which
    /// is the common case and costs one vectorized scan.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOf('\r') < 0)
        {
            return text;
        }

        var buffer = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var length = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character != '\r')
                {
                    buffer[length++] = character;
                    continue;
                }

                buffer[length++] = '\n';
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }

            return new string(buffer, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
