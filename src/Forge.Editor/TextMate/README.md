# TextMate syntax highlighting

`Forge.Editor.TextMate` loads TextMate grammars and tokenizes lines; `Forge.Editor.Highlighting` turns those tokens into
per-line spans with interned scope stacks and token categories, and re-tokenizes incrementally.

## Using it

```csharp
var registry = new GrammarRegistry();          // the bundled grammars, parsed on first use
registry.AddUserGrammarDirectories(workspace); // ~/.forge/grammars, then <workspace>/.forge/grammars

var tokenizer = new Tokenizer(registry);       // one per document: it owns scanners and scope interning
var highlighter = new IncrementalHighlighter(tokenizer, snapshotAdapter, "csharp");
var result = highlighter.Current;
var tokens = result.RenderLine(line);          // category per token, ready to render

// After an edit, the source already holds the new text.
var updated = highlighter.Update(LineChange.Replace(line));
```

`IHighlightSource` is the only thing the module needs from a document: a line count and line text. A text snapshot
implements it directly.

Threading: a `Tokenizer` is not thread-safe and must stay on one document; `GrammarRegistry` may be shared by every
document and every thread. Results are immutable, so a UI thread may render one while the next is built.

## Adding grammars

A grammar is any TextMate grammar file: `.tmLanguage.json` (JSON with comments and trailing commas) or `.tmLanguage` /
`.plist` (XML property list). Register it by path, stream or text:

```csharp
registry.AddGrammarFile("/path/to/rust.tmLanguage.json", languageId: "rust");
registry.AddGrammarDirectory("~/.vscode/extensions/.../syntaxes", recursive: true);
registry.AddUserGrammarDirectories(workspaceRoot);
```

A file registered later replaces one with the same `scopeName`. VS Code ships its grammars inside extensions under
`syntaxes/`; a grammar that includes another (`source.css` in HTML, `source.cs` in Markdown fences) needs that one
registered too — `GetMissingGrammars(scope)` lists what is missing and those rules are skipped.

Language ids resolve to scopes through `TryGetScopeName`, and file extensions through `GetScopeNameForFile`. The bundled
grammars cover C#, JSON, JSON with comments, XML, HTML, CSS, Markdown, YAML, JavaScript, TypeScript, Python, Rust, Go and
shell script, with the languages and extensions in `Grammars/Embedded/EmbeddedGrammars.cs`.

## Regex fidelity

TextMate grammars are written for Oniguruma; .NET has no Oniguruma engine and adding one as a native dependency is out of
scope, so `Internal/Regexes/OnigurumaTranslator.cs` translates each pattern to `System.Text.RegularExpressions` and
records the patterns it cannot express. All bundled grammars translate completely; these constructs are approximated:

- `\g<name>` subexpression calls are inlined up to three levels; past that depth a call matches text without brackets, so
  deeply nested generics and tuples may stop matching.
- `(?~expr)` (the absent operator) becomes a tempered scan, which stops before the first `expr` instead of consuming to
  the end of the subject.
- `[A[^B]]` (a class unioned with a negated class) becomes `[^B]`, and `[A&&[^B]]` becomes `[A-[B]]`; other nested classes
  and intersections are reported and skipped.
- An astral code point inside a character class becomes the whole surrogate range, and `\X` matches a base character plus
  its combining marks.
- A back-reference to a group the pattern does not have is treated as never matching, as Oniguruma rejects it.
- Patterns are matched with a 500 ms per-search timeout; a timeout counts as no match so a pathological pattern cannot
  freeze the editor.

Patterns are interpreted first and compiled once a tokenizer has searched them 128 times: `RegexOptions.Compiled` costs
about 2 ms per pattern, which is worth paying only for the patterns a document actually keeps reaching.

Scope-to-category mapping lives in `Forge.Editor.Highlighting.ThemeMapper` (an ordered table of TextMate scope selectors
with foreground colour ids, no UI types), and `SemanticTokenMapper` maps LSP semantic token types onto the same
`TokenCategory` values so `HighlightMerger.Overlay` can lay server tokens over the lexical ones.
