using System.Text;

namespace Forge.Core.Prompts.Templates;

/// <summary>
/// Turns <c>/name arguments</c> into the text that is sent, the way PI and Claude Code command files work:
/// <list type="bullet">
/// <item><c>$ARGUMENTS</c> and <c>$@</c> become the whole argument string (trimmed);</item>
/// <item><c>$1</c>..<c>$9</c> become the n-th argument (whitespace-separated, <c>"double"</c> or <c>'single'</c> quotes group), empty when absent;</item>
/// <item>when the body uses none of them and arguments were given, they are appended after a blank line so nothing typed is lost.</item>
/// </list>
/// Substitution is a single left-to-right pass: an argument that itself contains <c>$1</c> is not expanded again.
/// </summary>
public static class CommandExpander
{
    private const string ArgumentsToken = "$ARGUMENTS";

    /// <summary>
    /// Splits a message that starts with <c>/</c> into a lower-case command name and the rest. False when the message
    /// does not start with <c>/</c> or the name is not a valid template id (so <c>/usr/bin</c> is just text).
    /// </summary>
    public static bool TryParse(string? message, out string name, out string arguments)
    {
        name = string.Empty;
        arguments = string.Empty;
        if (message is null)
        {
            return false;
        }

        var text = message.TrimStart();
        if (text.Length < 2 || text[0] != '/')
        {
            return false;
        }

        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var candidate = text[1..end].ToLowerInvariant();
        if (!PromptTemplateIds.IsValid(candidate))
        {
            return false;
        }

        name = candidate;
        arguments = text[end..].Trim();
        return true;
    }

    public static string Expand(string body, string? arguments)
    {
        ArgumentNullException.ThrowIfNull(body);
        var args = arguments?.Trim() ?? string.Empty;
        string[]? positional = null;
        var used = false;
        var builder = new StringBuilder(body.Length + args.Length);
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c != '$' || i + 1 >= body.Length)
            {
                builder.Append(c);
                i++;
                continue;
            }

            var next = body[i + 1];
            if (string.CompareOrdinal(body, i, ArgumentsToken, 0, ArgumentsToken.Length) == 0)
            {
                builder.Append(args);
                i += ArgumentsToken.Length;
                used = true;
            }
            else if (next == '@')
            {
                builder.Append(args);
                i += 2;
                used = true;
            }
            else if (next is >= '1' and <= '9' && (i + 2 >= body.Length || !char.IsAsciiDigit(body[i + 2])))
            {
                positional ??= Split(args);
                var index = next - '1';
                if (index < positional.Length)
                {
                    builder.Append(positional[index]);
                }

                i += 2;
                used = true;
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        if (!used && args.Length > 0)
        {
            var trimmed = builder.ToString().TrimEnd();
            return trimmed.Length == 0 ? args : trimmed + "\n\n" + args;
        }

        return builder.ToString();
    }

    /// <summary>Whitespace-separated arguments; a quoted run is one argument without its quotes.</summary>
    internal static string[] Split(string arguments)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var inToken = false;
        foreach (var c in arguments)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }
}
