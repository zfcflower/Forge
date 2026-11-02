using System.Text;

namespace Forge.CodeMode;

internal static class TextBounds
{
    public static string Truncate(string text, int maxChars, string what)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return maxChars <= 0 ? string.Empty : text;
        }

        return new StringBuilder(maxChars + 96)
            .Append(text, 0, maxChars)
            .Append("\n[... ")
            .Append(what)
            .Append(" truncated: kept ")
            .Append(maxChars)
            .Append(" of ")
            .Append(text.Length)
            .Append(" chars]")
            .ToString();
    }
}
