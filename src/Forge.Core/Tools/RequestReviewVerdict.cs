using System.Globalization;
using System.Text;

namespace Forge.Core.Tools;

/// <summary>What a review ended with. <see cref="Unstructured"/> is never an approval: the reviewer missed its verdict line.</summary>
public enum ReviewVerdict
{
    Unstructured,
    Approve,
    RequestChanges,
}

/// <summary>
/// Reads the verdict out of a reviewer's text and turns it into the head of the tool result. The verdict line is the only
/// contract the reviewer has to keep; everything else in its text is prose the caller reads, never parsed.
/// </summary>
public static class RequestReviewVerdict
{
    /// <summary>Cap on the reviewer's text in the tool result; past this it did not follow its format.</summary>
    public const int MaxReviewChars = 8_000;

    private const string VerdictPrefix = "verdict:";

    /// <summary>Reads the reviewer's verdict from the last verdict line of its text.</summary>
    public static ReviewVerdict Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ReviewVerdict.Unstructured;
        }

        var span = text.AsSpan();
        var position = span.Length;

        // Backwards from the end, over the whole text: the last verdict line is the one that ends the review, and a
        // reviewer that appends a findings summary after its verdict still said approve. Scanning everything is O(n) over
        // at most one model answer, so no window is worth the false negative a cut-off window produces.
        while (position > 0)
        {
            var start = position;
            while (start > 0 && span[start - 1] != '\n')
            {
                start--;
            }

            var line = span[start..position];
            position = start > 0 ? start - 1 : 0;
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var trimmed = TrimMarkup(line);
            if (trimmed.StartsWith(VerdictPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return FromValue(TrimValue(trimmed[VerdictPrefix.Length..]));
            }
        }

        return ReviewVerdict.Unstructured;
    }

    /// <summary>First line of the tool result: "verdict: approve" / "verdict: request changes" / "verdict: unstructured".</summary>
    public static string VerdictLine(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "verdict: approve",
        ReviewVerdict.RequestChanges => "verdict: request changes",
        _ => "verdict: unstructured",
    };

    /// <summary>What the caller must do next, per verdict (2 sentences max).</summary>
    public static string NextStep(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve =>
            "Nothing blocking was found. State in the step's verification (or in your reply) what was reviewed and that the review approved.",
        ReviewVerdict.RequestChanges =>
            "Fix the blocking and major findings, commit the fix, then call request_review again at the new HEAD. Do not report the step as done while a blocking finding stands.",
        _ =>
            "The reviewer did not end with a verdict line, so this is not an approval: read the findings, act on them, and ask for another review.",
    };

    /// <summary>The reviewer's text, capped at <see cref="MaxReviewChars"/> on a line boundary, with a note when it was cut.</summary>
    public static string Clamp(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= MaxReviewChars)
        {
            return text;
        }

        var cut = text.LastIndexOf('\n', MaxReviewChars - 1, MaxReviewChars);
        var shown = cut <= 0 ? MaxReviewChars : cut;
        if (shown > 0 && char.IsHighSurrogate(text[shown - 1]))
        {
            shown--;
        }

        return new StringBuilder(shown + 160)
            .Append(text, 0, shown)
            .Append("\n[the review was cut: it is ")
            .Append(text.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" characters long and only the first ")
            .Append(shown.ToString(CultureInfo.InvariantCulture))
            .Append(" fit in this result]")
            .ToString();
    }

    private static ReviewVerdict FromValue(ReadOnlySpan<char> value)
    {
        if (Matches(value, "approve") || Matches(value, "approved"))
        {
            return ReviewVerdict.Approve;
        }

        if (Matches(value, "request changes")
            || Matches(value, "requests changes")
            || Matches(value, "request change")
            || Matches(value, "changes requested")
            || Matches(value, "changes required"))
        {
            return ReviewVerdict.RequestChanges;
        }

        return ReviewVerdict.Unstructured;
    }

    /// <summary>Case-insensitive equality with <paramref name="expected"/>, where an underscore counts as a space.</summary>
    private static bool Matches(ReadOnlySpan<char> value, string expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var actual = value[index] == '_' ? ' ' : value[index];
            if (char.ToLowerInvariant(actual) != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Trims what a verdict line is often wrapped in: whitespace, Markdown emphasis and a list or heading marker. A reviewer
    /// that writes <c>- **verdict: approve**</c> still said approve.
    /// </summary>
    private static ReadOnlySpan<char> TrimMarkup(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsMarkup(value[start]))
        {
            start++;
        }

        while (end > start && IsMarkup(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>Trims a verdict value down to its words: markup, and the punctuation a sentence ends with.</summary>
    private static ReadOnlySpan<char> TrimValue(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsValuePadding(value[start]))
        {
            start++;
        }

        while (end > start && IsValuePadding(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsMarkup(char value) => value is '*' or '_' or '`' or '-' or '>' or '#' || char.IsWhiteSpace(value);

    private static bool IsValuePadding(char value) => value is '.' or '!' or ';' or ':' || IsMarkup(value);
}
