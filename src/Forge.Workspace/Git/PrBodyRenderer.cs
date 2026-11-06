using System.Globalization;
using System.Text;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Pure functions over the pull request body. <see cref="Render"/> turns the session's plan summary plus its steps into the
/// body text; <see cref="TryGetSection"/> and <see cref="ReplaceSection"/> read and rewrite one <c>## Heading</c>
/// section, which is how the Testing gate looks at a body and how <c>pr_edit</c> (and the screenshot step) change one part
/// without touching the rest. Level-2 headings only match at column 0 and never inside a fenced code block; a section
/// ends at the next level-1 or level-2 heading, so <c>###</c> sub-headings stay inside it.
/// </summary>
public static class PrBodyRenderer
{
    /// <summary>The generated section that lists the steps.</summary>
    public const string PlanHeading = "Plan";

    private const int ShortShaLength = 7;

    /// <summary>
    /// The plan summary text (Summary / Changes / Testing, as the agent wrote it) followed by a generated
    /// <c>## Plan</c> section: one numbered entry per step in plan order with the title, the 7-character commit sha when the
    /// step was committed, its summary and its verification. A <c>## Plan</c> the summary text already contains is replaced
    /// by the generated one. Without steps there is no Plan section; without a summary and without steps the result is
    /// empty. Output uses <c>\n</c> line endings.
    /// </summary>
    public static string Render(PlanSummary? summary, IReadOnlyList<TodoItem> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var body = (summary?.Text ?? string.Empty).ReplaceLineEndings("\n").Trim();
        var plan = RenderPlan(steps);
        if (plan.Length == 0)
        {
            return body.Length == 0 ? string.Empty : body + "\n";
        }

        return body.Length == 0
            ? "## " + PlanHeading + "\n\n" + plan + "\n"
            : ReplaceSection(body, PlanHeading, plan);
    }

    /// <summary>
    /// The content of the <c>## <paramref name="heading"/></c> section (heading compared case-insensitively; a leading
    /// <c>##</c> in the argument is ignored), without its heading line and without surrounding blank lines. Null when the
    /// body has no such section; an empty string when the section exists but has no content.
    /// </summary>
    public static string? TryGetSection(string? body, string heading)
    {
        if (string.IsNullOrEmpty(body) || !TryFind(body, NormalizeHeading(heading), out var section))
        {
            return null;
        }

        return body[section.ContentStart..section.End].TrimStart('\r', '\n').TrimEnd();
    }

    /// <summary>
    /// Replaces the content of the <c>## <paramref name="heading"/></c> section, keeping the heading line, every other
    /// section and the body's line-ending style; appends the section at the end when the body has none. Replacing a section
    /// with the content it already has returns the body unchanged.
    /// </summary>
    public static string ReplaceSection(string? body, string heading, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = NormalizeHeading(heading);
        if (name.Length == 0)
        {
            throw new ArgumentException("A section heading is required.", nameof(heading));
        }

        body ??= string.Empty;
        var eol = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = content.ReplaceLineEndings(eol).TrimStart('\r', '\n').TrimEnd();

        if (TryFind(body, name, out var section))
        {
            if (string.Equals(body[section.ContentStart..section.End].TrimStart('\r', '\n').TrimEnd(), text, StringComparison.Ordinal))
            {
                return body;
            }

            var before = body[..section.ContentStart];
            if (before.Length > 0 && before[^1] != '\n')
            {
                before += eol;
            }

            var after = body[section.End..];
            var result = new StringBuilder(body.Length + text.Length + 16);
            result.Append(before);
            if (text.Length > 0)
            {
                result.Append(eol).Append(text).Append(eol);
            }

            if (after.Length > 0)
            {
                result.Append(eol).Append(after);
            }

            return result.ToString();
        }

        var appended = new StringBuilder(body.Length + text.Length + 32);
        var head = body.TrimEnd();
        if (head.Length > 0)
        {
            appended.Append(head).Append(eol).Append(eol);
        }

        appended.Append("## ").Append(name).Append(eol);
        if (text.Length > 0)
        {
            appended.Append(eol).Append(text).Append(eol);
        }

        return appended.ToString();
    }

    private static string RenderPlan(IReadOnlyList<TodoItem> steps)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        var ordered = new List<TodoItem>(steps);
        ordered.Sort(static (left, right) => left.Order.CompareTo(right.Order));

        var plan = new StringBuilder(256);
        for (var i = 0; i < ordered.Count; i++)
        {
            var step = ordered[i];
            if (i > 0)
            {
                plan.Append('\n');
            }

            plan.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". **").Append(OneLine(step.Title)).Append("**");
            if (!string.IsNullOrWhiteSpace(step.CommitId))
            {
                var sha = step.CommitId.Trim();
                plan.Append(" (`").Append(sha.Length <= ShortShaLength ? sha : sha[..ShortShaLength]).Append("`)");
            }

            if (step.Status != TodoStatus.Done)
            {
                plan.Append(step.Status == TodoStatus.InProgress ? " _(in progress)_" : " _(pending)_");
            }

            plan.Append('\n');
            AppendBullet(plan, null, step.Summary);
            AppendBullet(plan, "Verification: ", step.Verification);
        }

        return plan.ToString().TrimEnd('\n');
    }

    private static void AppendBullet(StringBuilder plan, string? label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lines = text.ReplaceLineEndings("\n").Trim().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (i == 0)
            {
                plan.Append("   - ").Append(label).Append(line).Append('\n');
            }
            else
            {
                plan.Append(line.Length == 0 ? string.Empty : "     " + line).Append('\n');
            }
        }
    }

    private static string OneLine(string text) =>
        text.ReplaceLineEndings(" ").Trim();

    private static string NormalizeHeading(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return string.Empty;
        }

        return StripClosingHashes(heading.Trim().TrimStart('#').Trim());
    }

    private static string StripClosingHashes(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#')
        {
            end--;
        }

        // A closing sequence only counts when it is separated from the text (or is all there is).
        return end == text.Length || end == 0 || char.IsWhiteSpace(text[end - 1])
            ? text[..end].TrimEnd()
            : text;
    }

    /// <summary>Locates the section; the heading text must already be normalised.</summary>
    private static bool TryFind(string body, string wanted, out SectionSpan section)
    {
        section = default;
        if (wanted.Length == 0)
        {
            return false;
        }

        var position = 0;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var headingStart = -1;
        var contentStart = -1;

        while (position < body.Length)
        {
            var newline = body.IndexOf('\n', position);
            var lineEnd = newline < 0 ? body.Length : newline;
            var next = newline < 0 ? body.Length : newline + 1;
            var line = body.AsSpan(position, lineEnd - position);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (fenceLength > 0)
            {
                if (ClosesFence(line, fenceCharacter, fenceLength))
                {
                    fenceLength = 0;
                }
            }
            else if (OpensFence(line, out fenceCharacter, out fenceLength))
            {
                // Everything up to the closing fence is code, whatever it looks like.
            }
            else if (TryParseHeading(line, out var level, out var text) && level <= 2)
            {
                if (headingStart >= 0)
                {
                    section = new SectionSpan(headingStart, contentStart, position);
                    return true;
                }

                if (level == 2 && string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    headingStart = position;
                    contentStart = next;
                }
            }

            position = next;
        }

        if (headingStart < 0)
        {
            return false;
        }

        section = new SectionSpan(headingStart, contentStart, body.Length);
        return true;
    }

    private static bool TryParseHeading(ReadOnlySpan<char> line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6)
        {
            return false;
        }

        if (level < line.Length && line[level] is not (' ' or '\t'))
        {
            return false;
        }

        text = StripClosingHashes(line[level..].ToString().Trim());
        return true;
    }

    private static bool OpensFence(ReadOnlySpan<char> line, out char character, out int length)
    {
        character = '\0';
        length = 0;
        var indent = 0;
        while (indent < line.Length && indent < 3 && line[indent] == ' ')
        {
            indent++;
        }

        if (indent >= line.Length || line[indent] is not ('`' or '~'))
        {
            return false;
        }

        var marker = line[indent];
        var run = 0;
        while (indent + run < line.Length && line[indent + run] == marker)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        character = marker;
        length = run;
        return true;
    }

    private static bool ClosesFence(ReadOnlySpan<char> line, char character, int length)
    {
        var indent = 0;
        while (indent < line.Length && indent < 3 && line[indent] == ' ')
        {
            indent++;
        }

        var run = 0;
        while (indent + run < line.Length && line[indent + run] == character)
        {
            run++;
        }

        return run >= length && line[(indent + run)..].Trim().IsEmpty;
    }

    private readonly record struct SectionSpan(int HeadingStart, int ContentStart, int End);
}
