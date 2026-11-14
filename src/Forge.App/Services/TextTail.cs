namespace Forge.App.Services;

/// <summary>
/// Trims one long text to the part that matters at the end of it: the tail of a failed job log, where the failure is.
/// Used by the CI fix chain, by the checks panel (a row's log and the message handed to the agent) and by the resource
/// inspector's live output tail, so none of them disagree about what "the last 200 lines" means.
/// </summary>
internal static class TextTail
{
    /// <summary>
    /// Keeps the last <paramref name="maxChars"/> characters, then the last <paramref name="maxLines"/> lines of that. A
    /// trailing newline does not count as an extra line, so a log that ends with one keeps exactly
    /// <paramref name="maxLines"/> lines.
    /// </summary>
    public static string Keep(string text, int maxLines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var s = text.Length > maxChars ? text[^maxChars..] : text;
        var position = s.Length;
        if (position > 0 && s[position - 1] == '\n')
        {
            position--;
        }

        for (var seen = 0; seen < maxLines; seen++)
        {
            position = position > 0 ? s.LastIndexOf('\n', position - 1) : -1;
            if (position < 0)
            {
                return s;
            }
        }

        return s[(position + 1)..];
    }
}
