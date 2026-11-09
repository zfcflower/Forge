using System.Text;
using AvaloniaEdit.Document;

namespace Forge.App.Changes;

/// <summary>Copy text of a diff selection: the code lines only, never the placeholder and spacer lines.</summary>
internal static class DiffSelectionText
{
    /// <summary>
    /// The text of document offsets <paramref name="start"/>..<paramref name="end"/>, line by line, leaving out the lines that
    /// hold no code (file headers, bars, folds, embeds, spacers).
    /// </summary>
    public static string Extract(TextDocument document, DiffRowMap map, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        if (end <= start)
        {
            return string.Empty;
        }

        start = Math.Clamp(start, 0, document.TextLength);
        end = Math.Clamp(end, 0, document.TextLength);
        var builder = new StringBuilder();
        var first = document.GetLineByOffset(start);
        var last = document.GetLineByOffset(end);
        var lastNumber = end == last.Offset && last.LineNumber > first.LineNumber ? last.LineNumber - 1 : last.LineNumber;
        for (var number = first.LineNumber; number <= lastNumber; number++)
        {
            if (!map.Contains(number) || !map.ForLine(number).IsCode)
            {
                continue;
            }

            var line = document.GetLineByNumber(number);
            var from = Math.Max(start, line.Offset);
            var to = Math.Min(end, line.EndOffset);
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            if (to > from)
            {
                builder.Append(document.GetText(from, to - from));
            }
        }

        return builder.ToString();
    }
}
