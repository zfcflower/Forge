using System.Globalization;

namespace Forge.Core.Agents;

/// <summary>
/// Receipts a context reduction puts in place of an old tool result: what ran, on what target, how much text went away,
/// and the sequence number <c>read_history</c> brings the original back with. The fixed parts — brackets, the
/// <c>read_history</c> reference, the separators, the char count — are never cut; only the path/command segment shrinks
/// (its middle for a path, its end for a command). A rendered receipt always stays under
/// <see cref="MinCroppableChars"/>, so the second reduction pass leaves it alone and the projection stays a fixed point.
/// </summary>
public static class ContextReceipt
{
    /// <summary>
    /// A result this short is never cropped. Every rendered receipt must fit inside it — otherwise the second pass would
    /// crop the receipt itself and the projection would stop being a fixed point.
    /// </summary>
    public const int MinCroppableChars = 512;

    // The three limits below must keep line + head + tail + markers under MinCroppableChars; changing any of them reruns
    // ContextReceiptTests.
    /// <summary>Longest the path/range or command segment of a receipt line may be; the fixed parts around it are never cut.</summary>
    public const int LineChars = 96;

    /// <summary>Characters kept of a cropped result's head — an error's first line usually lives here.</summary>
    public const int HeadChars = 150;

    /// <summary>Characters kept of a cropped result's tail — the result line, the exit code.</summary>
    public const int TailChars = 150;

    /// <summary>
    /// The receipt line for a cropped <c>read</c> result: the find-back reference first (it is never cut), then the path
    /// with its line range, then the size of what went away.
    /// </summary>
    public static string Read(string path, int start, int end, long chars, long seq) =>
        string.Concat(
            "[omitted: read_history ", Invariant(seq), " · ",
            MiddleCut(string.Concat(path, ":", Invariant(start), end > 0 ? "-" + Invariant(end) : "-"), LineChars),
            " · ", Invariant(chars), " chars]");

    /// <summary>The receipt line for a cropped command result; the tool name keeps its budget and the command loses its end.</summary>
    public static string Command(string tool, string command, long chars, long seq)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Concat(
                "[omitted: read_history ", Invariant(seq), " · ", MiddleCut(tool, LineChars),
                " · ", Invariant(chars), " chars]");
        }

        var budget = Math.Max(1, LineChars - tool.Length - 1);
        return string.Concat(
            "[omitted: read_history ", Invariant(seq), " · ",
            tool, " ", TailCut(command, budget),
            " · ", Invariant(chars), " chars]");
    }

    /// <summary>The receipt line in place of one cropped image: the history reference alone is enough to bring it back.</summary>
    public static string Image() => "[omitted: image]";

    /// <summary>The replacement for a cropped result's text: its head and tail kept, the middle replaced by one receipt line.</summary>
    public static string Cropped(string text, string receipt)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(receipt);
        // A caller-side guard, not a product path: the rung only gets here past MinCroppableChars, which already exceeds
        // head plus tail; a shorter text that arrives anyway gets the receipt alone rather than a half-cut frame.
        if (text.Length <= HeadChars + TailChars)
        {
            return receipt;
        }

        var dropped = text.Length - HeadChars - TailChars;
        return string.Concat(
            text.Substring(0, CutAtCharBoundary(text, HeadChars)),
            "\n[… ", Invariant(dropped), " chars omitted …]\n",
            text.Substring(CutAtCharBoundary(text, text.Length - TailChars)),
            "\n\n",
            receipt);
    }

    /// <summary>Moves an index back one char when it would split a surrogate pair; never leaves the string's bounds.</summary>
    private static int CutAtCharBoundary(string text, int index)
    {
        if (index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]))
        {
            return index - 1;
        }

        return index;
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Cuts the middle: the head keeps the root of a path, the tail keeps the file name.</summary>
    private static string MiddleCut(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var head = Math.Max(1, limit / 3);
        var tail = Math.Max(1, limit - head - 1);
        return string.Concat(text.Substring(0, head), "…", text.Substring(text.Length - tail));
    }

    /// <summary>Cuts the end: a command's verb and subcommand stay, its arguments go.</summary>
    private static string TailCut(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        return string.Concat(text.Substring(0, Math.Max(1, limit - 1)), "…");
    }
}
