using System.Globalization;
using System.Text;

namespace Forge.IO.Tools;

/// <summary>
/// Collapses the noise of a captured stream: a run of <see cref="MinRun"/> or more consecutive lines whose text differs
/// only in digits becomes its first line plus a count. MSBuild's locked-file warning is exactly that shape — one line per
/// retry, ten retries per file, a hundred in all — and the model pays for every one of them.
/// <para>Nothing else is touched: lines that are not consecutive, runs shorter than five, and every non-numeric difference
/// stay verbatim. The result's byte count and truncation flag keep describing what the command actually wrote, so the
/// count of a folded stream is still the real one.</para>
/// </summary>
internal static class OutputFold
{
    /// <summary>The shortest run worth replacing with a count; below it the count costs more than the copies.</summary>
    internal const int MinRun = 5;

    /// <summary>
    /// The text with long digit-only runs of lines folded, and whether anything was folded. Returns the input instance
    /// when nothing was, which is every stream that is not a warning storm.
    /// </summary>
    public static (string Text, bool Folded) Squeeze(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.AsSpan().Count('\n') < MinRun - 1)
        {
            return (text, false);
        }

        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length);
        var folded = false;
        var first = true;
        var index = 0;
        while (index < lines.Length)
        {
            var run = 1;
            var shape = Shape(lines[index]);
            while (index + run < lines.Length && Shape(lines[index + run]) == shape)
            {
                run++;
            }

            if (!first)
            {
                builder.Append('\n');
            }

            first = false;
            builder.Append(lines[index]);
            if (run >= MinRun)
            {
                builder.Append("  [×").Append((run - 1).ToString(CultureInfo.InvariantCulture)).Append(" more lines, same message]");
                folded = true;
            }
            else
            {
                for (var extra = 1; extra < run; extra++)
                {
                    builder.Append('\n').Append(lines[index + extra]);
                }
            }

            index += run;
        }

        return folded ? (builder.ToString(), true) : (text, false);
    }

    /// <summary>The line with every run of digits replaced by one <c>#</c>: two retry lines share a shape.</summary>
    private static string Shape(string line)
    {
        var builder = new StringBuilder(line.Length);
        var inDigits = false;
        foreach (var character in line)
        {
            if (char.IsAsciiDigit(character))
            {
                if (!inDigits)
                {
                    builder.Append('#');
                    inDigits = true;
                }

                continue;
            }

            inDigits = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
}
