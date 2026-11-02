// Governed by THIRD_PARTY_NOTICES.md: the bundled grammar files come from the Visual Studio Code extensions, each
// under the license of the project it was converted from; see THIRD_PARTY_NOTICES.md for the per-grammar sources,
// commits, licenses and copyright lines.

namespace Forge.Editor.TextMate.Grammars.Embedded;

// One bundled grammar: what it covers and its document text, parsed the first time a document needs it.
internal readonly record struct EmbeddedGrammar(string ScopeName, string? LanguageId, string[] FileExtensions, string Content)
{
    public static IReadOnlyList<EmbeddedGrammar> All { get; } =
    [
        new("source.cs", "csharp", [".cs", ".csx"], CSharpGrammarSource.Content),
        new("source.json", "json", [".json"], JsonGrammarSource.Content),
        new("source.json.comments", "jsonc", [".jsonc"], JsoncGrammarSource.Content),
        new("text.xml", "xml", [".xml", ".xsd", ".xsl", ".xslt", ".svg", ".csproj", ".props", ".targets"], XmlGrammarSource.Content),
        new("text.html.basic", "html", [".html", ".htm", ".xhtml"], HtmlGrammarSource.Content),
        new("text.html.derivative", null, [], HtmlDerivativeGrammarSource.Content),
        new("source.css", "css", [".css"], CssGrammarSource.Content),
        new("text.html.markdown", "markdown", [".md", ".markdown", ".mdx"], MarkdownGrammarSource.Content),
        new("source.yaml", "yaml", [".yaml", ".yml"], YamlGrammarSource.Content),
        new("source.yaml.1.2", null, [], Yaml12GrammarSource.Content),
        new("source.yaml.embedded", null, [], YamlEmbeddedGrammarSource.Content),
        new("source.js", "javascript", [".js", ".mjs", ".cjs", ".jsx"], JavaScriptGrammarSource.Content),
        new("source.js.regexp", null, [], JavaScriptRegexpGrammarSource.Content),
        new("source.ts", "typescript", [".ts", ".mts", ".cts", ".tsx"], TypeScriptGrammarSource.Content),
        new("source.python", "python", [".py", ".pyi", ".pyw"], PythonGrammarSource.Content),
        new("source.rust", "rust", [".rs"], RustGrammarSource.Content),
        new("source.go", "go", [".go"], GoGrammarSource.Content),
        new("source.shell", "shellscript", [".sh", ".bash", ".zsh", ".ksh"], ShellGrammarSource.Content),
    ];
}
