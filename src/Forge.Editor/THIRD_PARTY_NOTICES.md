# Third-party notices

`Forge.Editor` contains source adapted from the projects below and embeds eighteen TextMate grammars taken from
them. Each section lists what was taken, where it lives, and the license and copyright line the upstream project
requires.

Release artifacts must ship this file: every license below requires its notice and permission text to travel with the
binaries, so the published output of `Forge.Editor` (the app package, the installer payload and any NuGet or tool
package) carries `THIRD_PARTY_NOTICES.md` next to the assembly that contains the adapted code and the embedded
grammars.

## Tint — adapted code

- Project: Tint, TextMate syntax colorization for .NET, <https://github.com/sebastienros/tint>
- License: MIT, Copyright (c) 2026 Sébastien Ros (license text below)
- Taken from: `main` at commit `df7ba6f0cad11bd96d41cc24f393896923ef5733` (2026-11-02, "GregsEx 0.5.7 (#23)").
  Verified against that commit: the ported files share Tint's code apart from comments, names and the changes listed
  below (in Tint's paths, `Internal/Rules/Rule.cs`, `StateStack.cs` and `Internal/Regex/PatternRewriter.cs` share 201 of
  204, 92 of 95 and 96 of 110 non-comment lines), and the embedded grammar files are byte-identical to Tint's
  `grammars/` directory at that commit.
- What was adapted, under `src/Forge.Editor/TextMate/`:
  - `Loading/` (`DocumentTree`, `GrammarLoader`, `GrammarLoader.Read`, `LoadedGrammar`, `DocumentFormat`): reading
    `.tmLanguage.json` and plist grammars.
  - `Grammars/` (`GrammarInfo`, `IGrammarSource`, `IncludeReference`, `RawGrammar`, `RawRule`, `RegexSource`,
    `StandardTokenType`): the grammar model.
  - `Internal/Rules/` (`Rule`, `CompiledGrammar`, `BasicScopeAttributesProvider`), `Internal/Selectors/ScopeSelector.cs`,
    `Internal/AttributedScopeStack.cs`, `Internal/StateStack.cs`, `Internal/Tokenizer.cs`, `Internal/Token.cs`,
    `Internal/EncodedTokenAttributes.cs`, `Internal/FontStyle.cs`, `Internal/BoundedCache.cs`, `Internal/Jit.cs`,
    `Internal/GrammarStore.cs`, `GrammarRegistry.cs`, `TokenizerOptions.cs`: rule compilation, scope selectors, scope
    stacks, encoded token attributes and the line tokenizer, which is itself a port of vscode-textmate.
  - `Internal/Regexes/PatternRewriter.cs`, `Internal/Regexes/CharBuffer.cs`, `Internal/Regexes/CaptureSpan.cs`: the
    textual rewrites and capture shape of Tint's regex layer. `Internal/Regexes/RuleScanner.cs` keeps the
    multi-pattern search semantics of Tint's scanner (earliest match wins, the first pattern on a tie, left-to-right
    searches) but its caching, anchor-variant and capture handling were rewritten for this module.
- Changes made to the adapted code: namespaces and type visibility; Tint's theme, rendering, highlighter and source
  generator layers were removed (categories and colours are resolved by `Forge.Editor.Highlighting` instead); the regex
  layer now translates Oniguruma patterns to `System.Text.RegularExpressions` instead of calling GregsEx, so anchor
  variants, scanner caching, invalid-pattern handling and pattern promotion were rewritten; a tokenizer error list
  replaced the exception thrown when an invalid pattern is reached; capture retokenization anchors `\G` at the region
  start.
- Added by this module, not present upstream: include-cycle detection and depth caps for user-supplied grammars, a
  diagnostics sink (`GrammarRegistry.Diagnostics`) for the problems they work around, an end-of-line flag and the name
  scopes in the state equality that incremental highlighting relies on, and captures collected from a second search
  of the winning pattern that starts where the winning search started.

Tint is itself a port of vscode-textmate, and its pattern rewrites follow vscode-textmate
(<https://github.com/microsoft/vscode-textmate>, MIT, Copyright (c) Microsoft Corporation); the parts of this module
that mirror vscode-textmate are covered by the Tint license above and by that MIT license.

## Embedded TextMate grammars

The eighteen grammars in `src/Forge.Editor/TextMate/Grammars/Embedded/` are the files Tint ships under its `grammars/`
directory, which are the grammar files bundled by the Visual Studio Code extensions at VS Code commit
`3ac0f0563e382d8e3480c787d04a2d05afca6746`. Tint's `THIRD-PARTY-NOTICES.txt` and each VS Code extension's
`cgmanifest.json` record, per grammar, the upstream repository, commit and license that the file is distributed under;
the table below is that record. The embedded grammar text itself is unmodified.

The grammars are not all Microsoft MIT: the HTML, JavaScript and Markdown grammars come from TextMate bundles under
the TextMate Bundle License, the YAML grammars from RedCMD's YAML-Syntax-Highlighter, the Python grammar from
MagicPython, the Go grammar from go-syntax, the Rust grammar from rust-syntax, the shell grammar from
better-shell-syntax, the C# grammar from dotnet/csharp-tmLanguage and the XML grammar from atom/language-xml.

| # | Scope | Embedded source file | Upstream repository | Commit | Version | License | Copyright line |
|---|-------|----------------------|---------------------|--------|---------|---------|----------------|
| 1 | `source.cs` | `CSharpGrammarSource.cs` | <https://github.com/dotnet/csharp-tmLanguage> | `c2719309e91d2e26be21fce0ecaaca86c7b48ce4` | 0.1.0 | MIT | `Copyright (c) 2016 .NET Foundation` |
| 2 | `source.json` | `JsonGrammarSource.cs` | <https://github.com/microsoft/vscode-JSON.tmLanguage> | `9bd83f1c252b375e957203f21793316203f61f70` | 0.0.0 | MIT | `Copyright (c) 2015 simonzack, zertosh, benvie` |
| 3 | `source.json.comments` | `JsoncGrammarSource.cs` | <https://github.com/microsoft/vscode-JSON.tmLanguage> | `9bd83f1c252b375e957203f21793316203f61f70` | 0.0.0 | MIT | `Copyright (c) 2015 simonzack, zertosh, benvie` |
| 4 | `text.xml` | `XmlGrammarSource.cs` | <https://github.com/atom/language-xml> | `7bc75dfe779ad5b35d9bf4013d9181864358cb49` | 0.35.2 | MIT | `Copyright (c) 2014 GitHub Inc.` |
| 5 | `text.html.basic` | `HtmlGrammarSource.cs` | <https://github.com/textmate/html.tmbundle> | `0c3d5ee54de3a993f747f54186b73a4d2d3c44a2` | 0.0.0 | TextMate Bundle License | `Copyright (c) textmate-html.tmbundle project authors` |
| 6 | `text.html.derivative` | `HtmlDerivativeGrammarSource.cs` | <https://github.com/textmate/html.tmbundle> | `0c3d5ee54de3a993f747f54186b73a4d2d3c44a2` | 0.0.0 | TextMate Bundle License | `Copyright (c) textmate-html.tmbundle project authors` |
| 7 | `source.css` | `CssGrammarSource.cs` | <https://github.com/microsoft/vscode-css> | `de9e6beee756760f31b15efbd782735fc25de3db` | 0.0.0 | MIT, derived from textmate/css.tmbundle | `Copyright (c) Microsoft Corporation.` |
| 8 | `text.html.markdown` | `MarkdownGrammarSource.cs` | <https://github.com/microsoft/vscode-markdown-tm-grammar> | `0812fc4b190efc17bfed0d5b4ff918eff8e4e377` | 1.0.0 | MIT | `Copyright (c) Microsoft 2018` |
| 9 | `source.yaml` | `YamlGrammarSource.cs` | <https://github.com/RedCMD/YAML-Syntax-Highlighter> | `53d38bbc66b704803de54ffce5b251bf97211c60` | 1.3.2 | MIT | `Copyright 2024 RedCMD` |
| 10 | `source.yaml.1.2` | `Yaml12GrammarSource.cs` | <https://github.com/RedCMD/YAML-Syntax-Highlighter> | `53d38bbc66b704803de54ffce5b251bf97211c60` | 1.3.2 | MIT | `Copyright 2024 RedCMD` |
| 11 | `source.yaml.embedded` | `YamlEmbeddedGrammarSource.cs` | <https://github.com/RedCMD/YAML-Syntax-Highlighter> | `53d38bbc66b704803de54ffce5b251bf97211c60` | 1.3.2 | MIT | `Copyright 2024 RedCMD` |
| 12 | `source.js` | `JavaScriptGrammarSource.cs` | <https://github.com/microsoft/TypeScript-TmLanguage> | `3133e3d914db9a2bb8812119f9273727a305f16b` | 0.0.1 | MIT | `Copyright (c) Microsoft Corporation` / `All rights reserved.` |
| 13 | `source.js.regexp` | `JavaScriptRegexpGrammarSource.cs` | <https://github.com/textmate/javascript.tmbundle> | `fccf0af0c95430a42e1bf98f0c7a4723a53283e7` | 0.0.0 | TextMate Bundle License | `Copyright (c) textmate-javascript.tmbundle project authors` |
| 14 | `source.ts` | `TypeScriptGrammarSource.cs` | <https://github.com/microsoft/TypeScript-TmLanguage> | `48f608692aa6d6ad7bd65b478187906c798234a8` | 1.0.0 | MIT | `Copyright (c) Microsoft Corporation` / `All rights reserved.` |
| 15 | `source.python` | `PythonGrammarSource.cs` | <https://github.com/MagicStack/MagicPython> | `7d0f2b22a5ad8fccbd7341bc7b7a715169283044` | 1.1.1 | MIT | `Copyright (c) 2015-present MagicStack Inc.  http://magic.io` |
| 16 | `source.rust` | `RustGrammarSource.cs` | <https://github.com/dustypomerleau/rust-syntax> | `ca34cf382a7b250144f2ccb99ff9af96912c79f2` | 0.6.1 | MIT | `Copyright (c) 2020 Dustin Pomerleau` |
| 17 | `source.go` | `GoGrammarSource.cs` | <https://github.com/worlpaker/go-syntax> | `c74e22eb9ef32958e3edd130ea750ce78d8b8241` | 0.8.6 | MIT | `Copyright (c) 2023 Furkan Ozalp` |
| 18 | `source.shell` | `ShellGrammarSource.cs` | <https://github.com/jeff-hykin/better-shell-syntax> | `35020b0bd79a90d3b262b4c13a8bb0b33adc1f45` | 1.8.7 | MIT | `Copyright (c) 2019 Jeff Hykin` |

Notes on the table:

- Row 7: the CSS grammar carries two licenses, as its `LICENSE` file does: the MIT text of `microsoft/vscode-css` and,
  below it, the permission notice of `textmate/css.tmbundle` (`README.mdown`) that it was derived from. Both are
  reproduced under "License texts" below.
- Row 8: the Markdown grammar's directory in VS Code also registers `textmate/markdown.tmbundle` @
  `11cf764606cb2cde54badb5d0e5a0758a8871c4b` under the TextMate Bundle License (`Copyright (c) markdown.tmbundle
  authors`), because the grammar is derived from that bundle; that bundle's permission notice is reproduced below.
- Row 12: the JavaScript grammar's directory in VS Code registers both `microsoft/TypeScript-TmLanguage` @
  `3133e3d9…` (MIT) and `textmate/javascript.tmbundle` @ `fccf0af0…` (TextMate Bundle License); the grammar text is
  derived from `TypeScriptReact.tmLanguage` per VS Code's `cgmanifest.json`. Both notices are reproduced below.
- Row 13: the embedded file is byte-identical to `Syntaxes/Regular Expressions (JavaScript).tmLanguage` in
  `textmate/javascript.tmbundle` at the commit listed, which is the attribution VS Code's `extensions/javascript`
  `cgmanifest.json` records for that directory. Tint's own manifest carries no per-file entry for it; the bundle
  attribution above is the verified one.
- Not embedded, and therefore not listed: the VS Code `json` extension directory also registers
  `jeff-hykin/better-snippet-syntax` @ `2b1bb124cb2b9c75c3c80eae1b8f3a043841d654` for `snippets.tmLanguage.json`, the
  YAML extension ships the YAML 1.0, 1.1 and 1.3 variants, and `JSONL.tmLanguage.json` exists as well. This module
  embeds only the eighteen files above.
- Unverified: the content of the embedded files was checked byte for byte against Tint's `grammars/` copy, not against
  each upstream repository at the commit in the table. VS Code ships its own variant of some grammars and the files at
  the upstream commits differ from it in content, so the provenance claimed here is the one the upstream projects'
  `cgmanifest.json` files record for the files VS Code bundles, which is the record their licenses attach to. Where a
  grammar's upstream file at the listed commit is known to differ (YAML: the upstream `syntaxes/yaml.tmLanguage.json`
  is not the file VS Code bundles), no claim of textual identity with the upstream repository is made.

## License texts

### MIT License

Used by: Tint (section above), `dotnet/csharp-tmLanguage`, `microsoft/vscode-JSON.tmLanguage`,
`atom/language-xml`, `microsoft/vscode-css` (first part of its license), `microsoft/vscode-markdown-tm-grammar`,
`microsoft/TypeScript-TmLanguage`, `MagicStack/MagicPython`, `dustypomerleau/rust-syntax`, `worlpaker/go-syntax`,
`jeff-hykin/better-shell-syntax` and `RedCMD/YAML-Syntax-Highlighter`. The copyright lines each project requires are
the ones in the table above (and `Copyright (c) 2026 Sébastien Ros` for Tint); the permission text is:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

`microsoft/TypeScript-TmLanguage` writes its `LICENSE.txt` as `Copyright (c) Microsoft Corporation` / `All rights
reserved.` / `MIT License` followed by the same permission text.

### RedCMD/YAML-Syntax-Highlighter

Reproduced exactly as the project's `LICENSE` (and the `licenseDetail` of VS Code's YAML extension `cgmanifest.json`)
has it, including its typographic quotes:

```
MIT License

Copyright 2024 RedCMD

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### TextMate Bundle License

Used by `textmate/html.tmbundle` (rows 5 and 6), `textmate/javascript.tmbundle` (the JavaScript directory of row 12 and
row 13) and `textmate/markdown.tmbundle` (row 8, which the Markdown grammar is derived from), as their repositories and
VS Code's `licenseDetail` records have it:

```
Copyright (c) textmate-html.tmbundle project authors
Copyright (c) textmate-javascript.tmbundle project authors
Copyright (c) markdown.tmbundle authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

### microsoft/vscode-css

Reproduced as the project's `LICENSE` has it, both parts:

```
MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

--------------------------------------------------------------------

This package was derived from a TextMate bundle located at
https://github.com/textmate/css.tmbundle and distributed under the following
license, located in `README.mdown`:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.
```

### Tint

```
MIT License

Copyright (c) 2026 Sébastien Ros

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Oniguruma semantics

The pattern translation in `Internal/Regexes/OnigurumaTranslator.cs` and `Internal/Regexes/OnigurumaNormalizer.cs` is
written for this module against the Oniguruma syntax that TextMate grammars use
(<https://github.com/kkos/oniguruma>, BSD-2-Clause). No Oniguruma source was copied, and neither Oniguruma itself nor
GregsEx (the Oniguruma-compatible engine Tint calls) is linked, shipped or redistributed with `Forge.Editor`: grammar
patterns are translated to `System.Text.RegularExpressions` syntax at load time, and a construct the translation
cannot express makes that rule match nothing and is reported as a diagnostic instead.
