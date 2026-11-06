using System.Text;

namespace Forge.App.Terminal;

/// <summary>
/// Splits the <c>TerminalArguments</c> preference into the host's argument list: spaces separate arguments and a double
/// pair of quotes groups one that contains spaces (so <c>-l -i --title "my shell"</c> becomes four arguments). There is
/// no shell expansion here: the host starts the program with a fixed argument vector, so quoting is only about grouping.
/// </summary>
internal static class TerminalCommandLine
{
    /// <summary>The arguments of <paramref name="text"/>; empty text yields no arguments.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var pending = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '"')
            {
                quoted = !quoted;
                pending = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (pending)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    pending = false;
                }

                continue;
            }

            current.Append(character);
            pending = true;
        }

        if (pending)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
