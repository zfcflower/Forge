using Avalonia.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// Syntax colours for a fenced code block in the chat: the same TextMate registry, grammars and themes the editor and the diff
/// view use (<see cref="DiffSyntaxColorizer.SharedContext"/>), so a block reads like the file it quotes. A block is tokenized
/// once per text version, line by line with the grammar's state carried over, and is capped so a huge paste stays plain.
/// UI thread only: the shared grammar caches are not synchronized.
/// </summary>
internal static class CodeHighlighter
{
    /// <summary>Longer blocks stay uncoloured: highlighting is a courtesy, never a stall.</summary>
    internal const int MaxChars = 60_000;

    internal const int MaxLines = 1_500;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ".cs", ["c#"] = ".cs", ["cs"] = ".cs", ["fsharp"] = ".fs", ["js"] = ".js", ["javascript"] = ".js", ["jsx"] = ".jsx",
        ["ts"] = ".ts", ["typescript"] = ".ts", ["tsx"] = ".tsx", ["py"] = ".py", ["python"] = ".py", ["rb"] = ".rb", ["ruby"] = ".rb",
        ["rs"] = ".rs", ["rust"] = ".rs", ["golang"] = ".go", ["sh"] = ".sh", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh",
        ["ps1"] = ".ps1", ["powershell"] = ".ps1", ["pwsh"] = ".ps1", ["yml"] = ".yml", ["markdown"] = ".md", ["c++"] = ".cpp",
        ["objc"] = ".m", ["kt"] = ".kt", ["kotlin"] = ".kt", ["jsonc"] = ".json", ["json5"] = ".json", ["axaml"] = ".xml", ["xaml"] = ".xml",
        ["csproj"] = ".xml", ["svg"] = ".xml", ["html"] = ".html", ["dockerfile"] = ".dockerfile", ["tex"] = ".tex", ["latex"] = ".tex",
        ["diff"] = ".diff", ["patch"] = ".diff", ["text"] = "", ["txt"] = "", ["plaintext"] = "",
    };

    /// <summary>The coloured spans of each line of <paramref name="code"/>, or null when the language is unknown or the block too big.</summary>
    public static DiffSyntaxColorizer.Token[][]? Tokenize(string code, string info, bool dark)
    {
        var language = FirstWord(info);
        if (language.Length == 0 || code.Length == 0 || code.Length > MaxChars
            || DiffSyntaxColorizer.SharedContext(dark ? ThemeName.DarkPlus : ThemeName.LightPlus) is not { } context)
        {
            return null;
        }

        var scope = ScopeOf(context.Options, language);
        if (scope is null || context.GrammarFor(scope) is not { } grammar)
        {
            return null;
        }

        var lines = code.Split('\n');
        if (lines.Length > MaxLines)
        {
            return null;
        }

        var result = new DiffSyntaxColorizer.Token[lines.Length][];
        IStateStack? state = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var (tokens, next) = context.Tokenize(grammar, lines[index], state);
            result[index] = tokens;
            state = next;
        }

        return result;
    }

    private static string? ScopeOf(RegistryOptions options, string language)
    {
        var extension = Extensions.TryGetValue(language, out var mapped) ? mapped : "." + language;
        if (extension.Length == 0)
        {
            return null;
        }

        return options.GetScopeByExtension(extension) ?? options.GetScopeByLanguageId(language.ToLowerInvariant());
    }

    internal static string FirstWord(string info)
    {
        var span = info.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t', '{');
        return (end < 0 ? span : span[..end]).ToString();
    }
}
