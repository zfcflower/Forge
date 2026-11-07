using System.Text;

namespace Forge.IO;

/// <summary>
/// Splits a command line into an argument vector when — and only when — that vector means exactly what a shell would do
/// with the same text. Anything a shell would interpret itself (a pipe, a redirect, a glob, an expansion, a variable
/// assignment, a comment, a line break) makes <see cref="TryParseSimple"/> answer <c>false</c>, and the caller runs the
/// command through a shell instead. Direct mode is the fast path: no extra process, no quoting rules between the caller
/// and the program, and the arguments arrive exactly as written.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Parses <paramref name="command"/> into the arguments it names, or reports that it needs a shell. Quoting follows
    /// POSIX: a single-quoted run is literal, a double-quoted run is literal except for <c>\"</c> and <c>\\</c>, and a
    /// backslash outside quotes escapes only a character that would otherwise be read as a separator or a quote — so a
    /// Windows path such as <c>src\Forge.IO\a.csproj</c> keeps its separators instead of being mangled into one name.
    /// </summary>
    /// <param name="command">The command line as the model wrote it.</param>
    /// <param name="arguments">The program and its arguments, when the answer is <c>true</c>; otherwise empty.</param>
    /// <returns>
    /// <c>true</c> when the vector is safe to run without a shell. <c>false</c> for an empty or blank command, an
    /// unclosed quote, a dangling backslash, a leading <c>NAME=value</c> assignment, or any character a shell treats as
    /// syntax — including one inside quotes, where a shell would keep it literal but the check here stays conservative.
    /// </returns>
    public static bool TryParseSimple(string command, out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var argumentsFound = new List<string>(4);
        var token = new StringBuilder(command.Length < 64 ? command.Length : 64);
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var tokenStarted = false;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (IsShellSensitive(character))
            {
                return false;
            }

            if (inSingleQuotes)
            {
                if (character == '\'')
                {
                    inSingleQuotes = false;
                    continue;
                }

                token.Append(character);
                continue;
            }

            if (inDoubleQuotes)
            {
                if (character == '"')
                {
                    inDoubleQuotes = false;
                    continue;
                }

                if (character == '\\' && index + 1 < command.Length && command[index + 1] is '"' or '\\')
                {
                    token.Append(command[++index]);
                    continue;
                }

                token.Append(character);
                continue;
            }

            switch (character)
            {
                case ' ' or '\t':
                    Flush();
                    continue;
                case '\'':
                    inSingleQuotes = true;
                    tokenStarted = true;
                    continue;
                case '"':
                    inDoubleQuotes = true;
                    tokenStarted = true;
                    continue;
                case '\\':
                    if (index + 1 >= command.Length)
                    {
                        // A trailing backslash is a line continuation in a shell; there is nothing to continue here.
                        return false;
                    }

                    // A shell would consume this backslash and keep the next character literally. Doing the same to a path
                    // separator would silently rewrite `src\a.csproj` as `srca.csproj`, so only the characters that would
                    // otherwise split or quote a token are escaped here; any other backslash stays part of the argument.
                    switch (command[index + 1])
                    {
                        case ' ' or '\t' or '"' or '\'':
                            token.Append(command[++index]);
                            break;
                        case '\\':
                            token.Append('\\');
                            index++;
                            break;
                        default:
                            token.Append('\\');
                            break;
                    }

                    tokenStarted = true;
                    continue;
                default:
                    token.Append(character);
                    tokenStarted = true;
                    continue;
            }
        }

        if (inSingleQuotes || inDoubleQuotes)
        {
            return false;
        }

        Flush();
        if (argumentsFound.Count == 0 || IsEnvironmentAssignment(argumentsFound[0]))
        {
            return false;
        }

        arguments = argumentsFound;
        return true;

        void Flush()
        {
            if (!tokenStarted)
            {
                return;
            }

            argumentsFound.Add(token.ToString());
            token.Clear();
            tokenStarted = false;
        }
    }

    private static bool IsShellSensitive(char character) => character switch
    {
        '|' or '&' or ';' or '<' or '>' or '(' or ')' or '$' or '`' or '*' or '?' or '[' or ']' or '{' or '}'
            or '~' or '#' or '!' or '\n' or '\r' => true,
        _ => false,
    };

    /// <summary>True for the leading <c>NAME=value</c> form a shell reads as an assignment rather than as the program.</summary>
    private static bool IsEnvironmentAssignment(string token)
    {
        var separator = token.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var first = token[0];
        if (!char.IsAsciiLetter(first) && first != '_')
        {
            return false;
        }

        for (var index = 1; index < separator; index++)
        {
            var character = token[index];
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
