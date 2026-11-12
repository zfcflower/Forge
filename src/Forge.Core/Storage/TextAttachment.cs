using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// The inline shape of an attached text or source file: a <see cref="TextBlock"/> of its own whose first line is
/// <c>File: &lt;name&gt;</c>, followed by the content in a backtick fence one longer than the longest backtick run inside it,
/// so the content can never close the fence early. Every model reads it as plain text (no capability gate); the transcript
/// recognises it with <see cref="TryParse"/> to show a chip instead of the raw content, and the agent loop keeps it apart
/// from the typed text when interjections are merged.
/// </summary>
public static class TextAttachment
{
    private const string Header = "File: ";
    private const int MinFence = 3;

    /// <summary>Builds the block for a file called <paramref name="name"/> holding <paramref name="content"/>.</summary>
    public static TextBlock Create(string name, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        name = CleanName(name);
        var fence = new string('`', Math.Max(MinFence, LongestBacktickRun(content) + 1));
        var builder = new StringBuilder(Header.Length + name.Length + content.Length + fence.Length * 2 + 4);
        builder.Append(Header).Append(name).Append('\n').Append(fence).Append('\n').Append(content);
        if (content.Length > 0 && content[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append(fence);
        return new TextBlock(builder.ToString());
    }

    /// <summary>Whether <paramref name="block"/> is an attached file rather than typed text.</summary>
    public static bool IsAttachment(ContentBlock block) => block is TextBlock text && TryParse(text.Text, out _, out _);

    /// <summary>
    /// Recognises the shape <see cref="Create"/> writes and returns the file name and the content's UTF-8 size; false for
    /// any other text (typed text never matches by accident unless it reproduces the header, fence and closing fence).
    /// </summary>
    public static bool TryParse(string text, out string name, out long contentBytes)
    {
        name = string.Empty;
        contentBytes = 0;
        if (text is null || !text.StartsWith(Header, StringComparison.Ordinal))
        {
            return false;
        }

        var span = text.AsSpan();
        var nameEnd = span.IndexOf('\n');
        if (nameEnd <= Header.Length)
        {
            return false;
        }

        var rest = span[(nameEnd + 1)..];
        var fenceEnd = rest.IndexOf('\n');
        if (fenceEnd < MinFence)
        {
            return false;
        }

        var fence = rest[..fenceEnd];
        if (fence.ContainsAnyExcept('`'))
        {
            return false;
        }

        var body = rest[(fenceEnd + 1)..];
        if (!body.EndsWith(fence))
        {
            return false;
        }

        var content = body[..^fence.Length];
        if (content.Length > 0 && content[^1] != '\n')
        {
            return false;
        }

        name = span[Header.Length..nameEnd].ToString();
        contentBytes = Encoding.UTF8.GetByteCount(content.Length > 0 ? content[..^1] : content);
        return true;
    }

    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "file";
        }

        var trimmed = name.Trim();
        return trimmed.AsSpan().IndexOfAny('\r', '\n') < 0
            ? trimmed
            : trimmed.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static int LongestBacktickRun(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            if (c == '`')
            {
                run++;
                if (run > longest)
                {
                    longest = run;
                }
            }
            else
            {
                run = 0;
            }
        }

        return longest;
    }
}
