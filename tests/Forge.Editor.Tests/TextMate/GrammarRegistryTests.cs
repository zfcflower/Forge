using System.Diagnostics;
using System.Text;
using Forge.Editor.Highlighting;
using Forge.Editor.Tests.Support;
using Forge.Editor.TextMate;

namespace Forge.Editor.Tests.TextMate;

public class GrammarRegistryTests
{
    private const string MyLangGrammar = """
        {"scopeName":"source.mylang","name":"MyLang","fileTypes":[".my"],
         "patterns":[{"match":"hello","name":"greeting.mine"}]}
        """;

    private const string PlistGrammar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>scopeName</key><string>source.plisty</string>
          <key>fileTypes</key><array><string>plisty</string></array>
          <key>patterns</key><array><dict>
            <key>match</key><string>plisty</string>
            <key>name</key><string>word.plisty</string>
          </dict></array>
        </dict></plist>
        """;

    [Fact]
    public void Bundled_grammars_are_registered_by_scope_language_and_extension()
    {
        var registry = new GrammarRegistry();

        Assert.True(registry.IsRegistered("source.cs"));
        Assert.Equal("source.cs", registry.Languages["csharp"]);
        Assert.Equal("source.json", registry.Languages["json"]);
        Assert.Equal("text.html.markdown", registry.Languages["markdown"]);
        Assert.True(registry.TryGetScopeName("csharp", out var scope));
        Assert.Equal("source.cs", scope);
        Assert.True(registry.TryGetScopeName("source.cs", out var byScope));
        Assert.Equal("source.cs", byScope);
        Assert.False(registry.TryGetScopeName("cobol", out _));
        Assert.Equal("source.cs", registry.GetScopeNameForFile("Program.CS"));
        Assert.Equal("source.cs", registry.GetScopeNameForFile("/a/b/c.csx"));
        Assert.Null(registry.GetScopeNameForFile("Makefile"));
        Assert.Null(registry.GetScopeNameForFile("archive.unknownext"));
        Assert.Empty(registry.Diagnostics);
        Assert.Empty(registry.GetMissingGrammars("source.cs"));
    }

    [Fact]
    public void A_registry_without_embedded_grammars_knows_nothing_until_told()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);

        Assert.Empty(registry.ScopeNames);
        Assert.Empty(registry.Languages);
        Assert.False(registry.TryGetScopeName("csharp", out _));
        Assert.Throws<KeyNotFoundException>(() => new Tokenizer(registry).HighlightText("int a;", "csharp"));
    }

    [Fact]
    public void Grammar_text_registers_its_language_and_file_types()
    {
        var registry = new GrammarRegistry();

        Assert.True(registry.AddGrammarText(MyLangGrammar, languageId: "mylang"));

        Assert.True(registry.IsRegistered("source.mylang"));
        Assert.True(registry.TryGetScopeName("mylang", out var scope));
        Assert.Equal("source.mylang", scope);
        Assert.Equal("source.mylang", registry.GetScopeNameForFile("notes.my"));
        var result = new Tokenizer(registry).HighlightText("say hello", "mylang");
        Assert.Contains("greeting.mine", string.Join(' ', result.Scopes.GetScopes(result.GetTokens(0).Span[^1].ScopeId).ToArray()));
    }

    [Fact]
    public void Plist_grammar_text_is_read_in_plist_format()
    {
        var registry = new GrammarRegistry();

        Assert.True(registry.AddGrammarText(PlistGrammar, format: DocumentFormat.Auto));

        Assert.True(registry.IsRegistered("source.plisty"));
        Assert.Equal("source.plisty", registry.GetScopeNameForFile("x.plisty"));
    }

    [Fact]
    public void Invalid_grammar_text_is_reported_as_a_diagnostic_and_not_registered()
    {
        var registry = new GrammarRegistry();

        Assert.False(registry.AddGrammarText("{\"scopeName\": "));
        Assert.False(registry.AddGrammarText("[1,2,3]"));
        Assert.False(registry.AddGrammarText("""{"patterns":[]}"""));

        var diagnostics = registry.Diagnostics;
        Assert.Equal(3, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Source == "<text>" && d.Message.Contains("scopeName", StringComparison.Ordinal));
        Assert.DoesNotContain("source.mylang", registry.ScopeNames);
    }

    [Fact]
    public void Missing_grammar_file_is_a_diagnostic_and_not_an_exception()
    {
        using var dir = new TempDirectory();
        var registry = new GrammarRegistry();
        var missing = dir.PathOf("gone.tmLanguage.json");

        Assert.False(registry.AddGrammarFile(missing));

        var diagnostic = Assert.Single(registry.Diagnostics);
        Assert.Equal(missing, diagnostic.Source);
    }

    [Fact]
    public void Directory_load_registers_valid_grammars_recursively_and_reports_broken_ones()
    {
        using var dir = new TempDirectory();
        dir.WriteText("mine.tmLanguage.json", """{"scopeName":"source.dirlang","patterns":[{"match":"dir","name":"dir.word"}]}""");
        dir.WriteText("broken.tmLanguage.json", "{ nope");
        Directory.CreateDirectory(Path.Combine(dir.Root, "nested"));
        File.WriteAllText(Path.Combine(dir.Root, "nested", "deep.tmLanguage.json"), """{"scopeName":"source.deeplang","patterns":[]}""");
        File.WriteAllText(Path.Combine(dir.Root, "style.plist"), PlistGrammar);
        File.WriteAllText(Path.Combine(dir.Root, "ignored.txt"), "not a grammar");

        var registry = new GrammarRegistry();

        Assert.Equal(3, registry.AddGrammarDirectory(dir.Root));
        Assert.True(registry.IsRegistered("source.dirlang"));
        Assert.True(registry.IsRegistered("source.deeplang"));
        Assert.True(registry.IsRegistered("source.plisty"));
        Assert.Single(registry.Diagnostics);
        Assert.Equal(3, new GrammarRegistry(includeEmbeddedGrammars: false).AddGrammarDirectory(dir.Root));
    }

    [Fact]
    public void Non_recursive_directory_load_skips_subfolders_and_missing_directories_count_zero()
    {
        using var dir = new TempDirectory();
        dir.WriteText("top.tmLanguage.json", """{"scopeName":"source.toplang","patterns":[]}""");
        Directory.CreateDirectory(Path.Combine(dir.Root, "sub"));
        File.WriteAllText(Path.Combine(dir.Root, "sub", "inner.tmLanguage.json"), """{"scopeName":"source.innerlang","patterns":[]}""");

        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);

        Assert.Equal(1, registry.AddGrammarDirectory(dir.Root, recursive: false));
        Assert.False(registry.IsRegistered("source.innerlang"));
        Assert.Equal(0, registry.AddGrammarDirectory(dir.PathOf("absent")));
    }

    [Fact]
    public void Registering_the_same_scope_again_replaces_the_grammar_in_place()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        registry.AddGrammarText(MyLangGrammar, languageId: "mylang");
        registry.AddGrammarText("""{"scopeName":"source.mylang","patterns":[{"match":"hello","name":"second.mine"}]}""", languageId: "mylang");

        Assert.Single(registry.ScopeNames, "source.mylang");
        var result = new Tokenizer(registry).HighlightText("hello", "mylang");
        Assert.Contains("second.mine", string.Join(' ', result.Scopes.GetScopes(result.GetTokens(0).Span[0].ScopeId).ToArray()));
    }

    [Fact]
    public void Includes_of_unregistered_grammars_are_reported_as_missing()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        registry.AddGrammarText("""{"scopeName":"source.incl","patterns":[{"include":"source.nothere"}]}""", languageId: "incl");

        Assert.Contains("source.nothere", registry.GetMissingGrammars("source.incl"));
        Assert.Contains("source.nothere", new Tokenizer(registry).MissingGrammars("incl"));
    }

    [Fact]
    public void Invalid_regular_expressions_match_nothing_and_are_reported_by_the_tokenizer()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        registry.AddGrammarText("""{"scopeName":"source.badrx","patterns":[{"match":"(","name":"bad.rx"},{"match":"ok","name":"good.rx"}]}""", languageId: "badrx");
        var tokenizer = new Tokenizer(registry);

        var result = tokenizer.HighlightText("ok (", "badrx");

        Assert.Single(result.GetTokens(0).Span.ToArray(), t => result.Scopes.GetScopes(t.ScopeId).ToArray().Contains("good.rx"));
        Assert.NotEmpty(tokenizer.Diagnostics);
        Assert.Contains(tokenizer.Diagnostics, message => message.Contains("Invalid regular expression", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(200)]
    public void Pathological_user_regex_is_bounded_by_the_match_timeout(int runLength)
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        registry.AddGrammarText("""{"scopeName":"source.patho","patterns":[{"match":"^(a+)+$","name":"bad.x"}]}""", languageId: "patho");
        var line = new string('a', runLength) + "!";
        var tokenizer = new Tokenizer(registry);
        var clock = Stopwatch.StartNew();

        var result = tokenizer.HighlightText(line, "patho");

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(1, result.LineCount);
        Assert.Equal(line.Length, result.GetTokens(0).Span.ToArray().Sum(t => t.Length));
    }

    [Fact]
    public void Line_time_budget_stops_a_long_line_early_but_keeps_the_tokens_found_so_far()
    {
        var registry = new GrammarRegistry();
        var line = string.Concat(Enumerable.Repeat("int a = 1; ", 3000));

        var full = new Tokenizer(registry).HighlightText(line, "csharp").GetTokens(0).Length;
        var budgeted = new Tokenizer(registry, options: new TokenizerOptions { LineTimeBudget = TimeSpan.FromTicks(1) })
            .HighlightText(line, "csharp")
            .GetTokens(0).Length;

        Assert.True(budgeted < full, $"budgeted {budgeted} vs full {full}");
        Assert.True(budgeted > 0);
    }

    [Fact]
    public void Grammar_stream_and_file_loading_match_the_text_form()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("stream.tmLanguage.json", MyLangGrammar);
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);

        using (var stream = File.OpenRead(path))
        {
            Assert.True(registry.AddGrammar(stream, languageId: "streamed"));
        }

        Assert.True(registry.AddGrammarFile(path, languageId: "fromfile"));
        Assert.True(registry.TryGetScopeName("streamed", out _));
        Assert.True(registry.TryGetScopeName("fromfile", out _));
        Assert.Throws<ArgumentNullException>(() => registry.AddGrammar(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddGrammarText(null!));
        Assert.Throws<ArgumentException>(() => registry.AddGrammarFile(" "));
    }

    [Fact]
    public void Grammar_bytes_with_a_utf8_bom_still_load()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(MyLangGrammar)).ToArray();

        Assert.True(registry.AddGrammar(new MemoryStream(bytes), languageId: "bom"));
        Assert.True(registry.IsRegistered("source.mylang"));
    }
}
