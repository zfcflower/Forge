using System.Text;

namespace Forge.IO.Tools;

/// <summary>One simple command found in a command line: its normalized program name, the words after it, and whether its input is a pipe.</summary>
internal readonly record struct ShellCommand(string Name, IReadOnlyList<string> Arguments, bool FromPipe = false);

/// <summary>
/// Finds the simple commands a <c>bash</c> command line would run, so <see cref="BashPolicy"/> can judge each one. It is a
/// guard rail for steering the model, not a shell parser and not a sandbox: it splits on the separators every supported
/// dialect shares (<c>; | &amp;&amp; || ( ) { }</c>, line breaks, backticks), reads quotes, peels the wrappers that only
/// prefix another command (<c>env</c>, <c>sudo -u x</c>, <c>timeout 5</c>, <c>xargs</c>, …), looks into command
/// substitutions (<c>$(…)</c> and backticks, also inside double quotes) and one level into <c>sh -c</c> /
/// <c>pwsh -Command</c> / <c>cmd /c</c> payloads. What a script file or a build tool runs internally is not inspected.
/// </summary>
internal static class BashCommandAnalyzer
{
    private const int MaxNesting = 2;

    /// <summary>Programs that only prefix another command, with the options of each that take the next word as a value.</summary>
    private static readonly Dictionary<string, string[]> Wrappers = new(StringComparer.Ordinal)
    {
        ["env"] = ["-u", "-C", "-S"],
        ["sudo"] = ["-u", "-g", "-C", "-D", "-h", "-p", "-r", "-t", "-T", "-U"],
        ["doas"] = ["-u", "-C"],
        ["timeout"] = ["-s", "-k"],
        ["nice"] = ["-n"],
        ["xargs"] = ["-n", "-I", "-L", "-P", "-d", "-E", "-s", "-a"],
        ["stdbuf"] = ["-i", "-o", "-e"],
        ["command"] = [],
        ["exec"] = [],
        ["nohup"] = [],
        ["time"] = [],
        ["call"] = [],
        ["builtin"] = [],
        ["setsid"] = [],
    };

    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "pwsh", "powershell", "cmd",
    };

    private static readonly string[] ProgramExtensions = [".exe", ".cmd", ".bat", ".com", ".ps1"];

    /// <summary>Every simple command in <paramref name="command"/>, in order; empty when nothing runnable is found.</summary>
    public static IReadOnlyList<ShellCommand> Analyze(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var found = new List<ShellCommand>(2);
        Collect(command, found, 0);
        return found;
    }

    /// <summary>The name a program is known by: last path segment, no script/executable extension, lower case.</summary>
    internal static string NormalizeName(string word)
    {
        var start = Math.Max(word.LastIndexOf('/'), word.LastIndexOf('\\')) + 1;
        var name = word.AsSpan(start);
        foreach (var extension in ProgramExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.ToString().ToLowerInvariant();
    }

    private readonly record struct Segment(List<string> Words, bool Piped);

    private static void Collect(string text, List<ShellCommand> into, int nesting)
    {
        if (CommandLine.TryParseSimple(text, out var argv))
        {
            Add(argv, false, into, nesting);
            return;
        }

        foreach (var segment in Split(text))
        {
            Add(segment.Words, segment.Piped, into, nesting);
        }

        CollectSubstitutions(text, into, nesting);
    }

    /// <summary>The commands inside <c>$(…)</c> and backticks that are not single-quoted; double quotes do not hide them.</summary>
    private static void CollectSubstitutions(string text, List<ShellCommand> into, int nesting)
    {
        if (nesting >= MaxNesting || (!text.Contains("$(", StringComparison.Ordinal) && !text.Contains('`')))
        {
            return;
        }

        var single = false;
        var doubled = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\' && !single)
            {
                index++;
            }
            else if (character == '\'' && !doubled)
            {
                single = !single;
            }
            else if (character == '"' && !single)
            {
                doubled = !doubled;
            }
            else if (!single && character == '$' && index + 1 < text.Length && text[index + 1] == '(')
            {
                var depth = 1;
                var end = index + 2;
                while (end < text.Length && depth > 0)
                {
                    depth += text[end] == '(' ? 1 : text[end] == ')' ? -1 : 0;
                    end++;
                }

                var inner = text.Substring(index + 2, Math.Max(0, end - index - 3));
                Collect(inner, into, nesting + 1);
                index = end - 1;
            }
            else if (!single && character == '`')
            {
                var end = text.IndexOf('`', index + 1);
                end = end < 0 ? text.Length : end;
                Collect(text.Substring(index + 1, end - index - 1), into, nesting + 1);
                index = end;
            }
        }
    }

    private static void Add(IReadOnlyList<string> words, bool piped, List<ShellCommand> into, int nesting)
    {
        var index = 0;
        while (index < words.Count)
        {
            if (IsAssignment(words[index]))
            {
                index++;
                continue;
            }

            var wrapper = NormalizeName(words[index]);
            if (!Wrappers.TryGetValue(wrapper, out var valueOptions))
            {
                break;
            }

            // `command -v git` looks a program up; it does not run it.
            if (wrapper is "command" or "builtin" && index + 1 < words.Count && words[index + 1] is "-v" or "-V")
            {
                break;
            }

            index++;
            while (index < words.Count && words[index].Length > 1 && words[index][0] == '-')
            {
                var option = words[index];
                index++;
                if (index < words.Count && Array.IndexOf(valueOptions, option) >= 0)
                {
                    index++;
                }
            }

            if (wrapper == "timeout" && index < words.Count && words[index].Length > 0 && char.IsAsciiDigit(words[index][0]))
            {
                index++;
            }
        }

        if (index >= words.Count)
        {
            return;
        }

        var name = NormalizeName(words[index]);
        var arguments = new string[words.Count - index - 1];
        for (var copy = 0; copy < arguments.Length; copy++)
        {
            arguments[copy] = words[index + 1 + copy];
        }

        if (nesting < MaxNesting && Interpreters.Contains(name) && PayloadOf(name, arguments) is { } payload)
        {
            Collect(payload, into, nesting + 1);
            return;
        }

        into.Add(new ShellCommand(name, arguments, piped));
    }

    /// <summary>The script text handed to an interpreter with <c>-c</c> / <c>-Command</c> / <c>/c</c>, or null when it runs a file or reads stdin.</summary>
    private static string? PayloadOf(string interpreter, string[] arguments)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            var flag = arguments[index];
            var matches = interpreter switch
            {
                "cmd" => flag.Equals("/c", StringComparison.OrdinalIgnoreCase) || flag.Equals("/k", StringComparison.OrdinalIgnoreCase),
                "pwsh" or "powershell" => flag.Equals("-Command", StringComparison.OrdinalIgnoreCase)
                    || flag.Equals("-c", StringComparison.OrdinalIgnoreCase),
                // sh -c, bash -lc, sh -ec: a short-option cluster that contains c.
                _ => flag.Length > 1 && flag[0] == '-' && flag[1] != '-' && flag.Contains('c'),
            };
            if (matches)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static bool IsAssignment(string word)
    {
        var separator = word.IndexOf('=');
        if (separator <= 0 || !(char.IsAsciiLetter(word[0]) || word[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < separator; index++)
        {
            if (!char.IsAsciiLetterOrDigit(word[index]) && word[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static List<Segment> Split(string text)
    {
        var segments = new List<Segment>();
        var words = new List<string>();
        var word = new StringBuilder();
        var started = false;
        var piped = false;
        var quote = '\0';

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else if (quote == '"' && character == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
                {
                    word.Append(text[++index]);
                }
                else
                {
                    word.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '\'' or '"':
                    quote = character;
                    started = true;
                    break;
                case '\\' when index + 1 < text.Length && text[index + 1] is ' ' or '\t' or '"' or '\'':
                    word.Append(text[++index]);
                    started = true;
                    break;
                case ' ' or '\t':
                    EndWord();
                    break;
                case '|':
                    if (index + 1 < text.Length && text[index + 1] == '|')
                    {
                        index++;
                        EndSegment(false);
                    }
                    else
                    {
                        if (index + 1 < text.Length && text[index + 1] == '&')
                        {
                            index++;
                        }

                        EndSegment(true);
                    }

                    break;
                case ';' or '\n' or '\r' or '(' or ')' or '{' or '}' or '`':
                    EndSegment(false);
                    break;
                case '&':
                    // `2>&1` and `&>` are redirections, not command separators.
                    if ((index > 0 && text[index - 1] == '>') || (index + 1 < text.Length && text[index + 1] == '>'))
                    {
                        word.Append(character);
                        started = true;
                    }
                    else
                    {
                        EndSegment(false);
                    }

                    break;
                default:
                    word.Append(character);
                    started = true;
                    break;
            }
        }

        EndSegment(false);
        return segments;

        void EndWord()
        {
            if (!started)
            {
                return;
            }

            words.Add(word.ToString());
            word.Clear();
            started = false;
        }

        void EndSegment(bool nextPiped)
        {
            EndWord();
            if (words.Count > 0)
            {
                segments.Add(new Segment(words, piped));
                words = [];
            }

            piped = nextPiped;
        }
    }
}
