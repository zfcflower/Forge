// Governed by THIRD_PARTY_NOTICES.md.

using System.Diagnostics.CodeAnalysis;
using Forge.Editor.TextMate.Grammars;
using Forge.Editor.TextMate.Grammars.Embedded;
using Forge.Editor.TextMate.Internal.Rules;
using Forge.Editor.TextMate.Loading;

namespace Forge.Editor.TextMate;

// Something the registry could not use: an unreadable grammar file, a grammar without a scope name, or a pattern the
// tokenizer had to skip. Highlighting continues with the rules that do work.
public readonly record struct GrammarDiagnostic(string Source, string Message);

// The grammars documents can be highlighted with: the bundled ones, grammar files from the user's directories, and
// grammars a host registers itself. A grammar is parsed and compiled the first time a document needs it, so a registry
// holding every bundled grammar costs nothing until one is used.
public sealed class GrammarRegistry
{
    private const string UserGrammarDirectoryName = ".forge/grammars";

    private static readonly string[] s_grammarFilePatterns = ["*.tmLanguage.json", "*.tmLanguage", "*.plist"];

    private readonly Lock _lock = new();
    private readonly GrammarStore _store = new();
    private readonly Dictionary<string, string> _languageScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extensionScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GrammarDiagnostic> _diagnostics = [];

    public GrammarRegistry(bool includeEmbeddedGrammars = true)
    {
        _store.Diagnostics = new DiagnosticSink(this);
        if (includeEmbeddedGrammars)
        {
            AddEmbeddedGrammars();
        }
    }

    public IReadOnlyList<string> ScopeNames => _store.ScopeNames;

    // The language ids a document can ask for, mapped to the scope of the grammar that highlights them.
    public IReadOnlyDictionary<string, string> Languages
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_languageScopes, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyList<GrammarDiagnostic> Diagnostics
    {
        get
        {
            lock (_lock)
            {
                return [.. _diagnostics];
            }
        }
    }

    // Registers a grammar file: .json for a JSON grammar, anything else read as an XML property list.
    public bool AddGrammarFile(string path, string? languageId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? DocumentFormat.Json : DocumentFormat.Auto;
        try
        {
            return Register(GrammarLoader.Load(path, CreateInfo(languageId)), path);
        }
        catch (Exception exception) when (exception is IOException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            AddDiagnostic(path, exception.Message);
            return false;
        }
    }

    public bool AddGrammar(Stream stream, string? languageId = null, DocumentFormat format = DocumentFormat.Auto)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return Register(GrammarLoader.Load(stream, format, CreateInfo(languageId)), "<stream>");
        }
        catch (Exception exception) when (exception is IOException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            AddDiagnostic("<stream>", exception.Message);
            return false;
        }
    }

    public bool AddGrammarText(string text, string? languageId = null, DocumentFormat format = DocumentFormat.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return Register(GrammarLoader.LoadText(text, format, CreateInfo(languageId)), "<text>");
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            AddDiagnostic("<text>", exception.Message);
            return false;
        }
    }

    // Registers every grammar file of a directory; returns how many were registered.
    public int AddGrammarDirectory(string directory, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var added = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var files = new List<string>();
        foreach (var pattern in s_grammarFilePatterns)
        {
            files.AddRange(Directory.EnumerateFiles(directory, pattern, options));
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (AddGrammarFile(file))
            {
                added++;
            }
        }

        return added;
    }

    // Registers the user's grammar directories: ~/.forge/grammars, then <workspace>/.forge/grammars, so a workspace can
    // override a grammar installed for every project.
    public int AddUserGrammarDirectories(string? workspaceDirectory = null)
    {
        var added = 0;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            added += AddGrammarDirectory(Path.Combine(profile, UserGrammarDirectoryName));
        }

        if (!string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            added += AddGrammarDirectory(Path.Combine(workspaceDirectory, UserGrammarDirectoryName));
        }

        return added;
    }

    public bool IsRegistered(string scopeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        return _store.Contains(scopeName);
    }

    // Resolves a language id or a scope name to the scope of the registered grammar.
    public bool TryGetScopeName(string languageIdOrScopeName, [NotNullWhen(true)] out string? scopeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageIdOrScopeName);
        if (_store.Contains(languageIdOrScopeName))
        {
            scopeName = languageIdOrScopeName;
            return true;
        }

        lock (_lock)
        {
            return _languageScopes.TryGetValue(languageIdOrScopeName, out scopeName);
        }
    }

    // The scope of the grammar that handles a file, by extension.
    public string? GetScopeNameForFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            return _extensionScopes.GetValueOrDefault(extension);
        }
    }

    public IReadOnlyList<string> GetMissingGrammars(string scopeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        return _store.GetMissingGrammars(scopeName);
    }

    internal GrammarStore Store => _store;

    private void AddEmbeddedGrammars()
    {
        foreach (var embedded in EmbeddedGrammar.All)
        {
            var source = new EmbeddedGrammarSource(embedded);
            _store.Add(source);
            RegisterNames(source.ScopeName, embedded.LanguageId, embedded.FileExtensions);
        }
    }

    private bool Register(LoadedGrammar grammar, string origin)
    {
        if (string.IsNullOrEmpty(grammar.ScopeName))
        {
            AddDiagnostic(origin, "A grammar must declare a scopeName.");
            return false;
        }

        _store.Add(grammar);
        RegisterNames(grammar.ScopeName, grammar.Info.LanguageId, grammar.Info.FileExtensions);
        return true;
    }

    private void RegisterNames(string scopeName, string? languageId, IReadOnlyList<string> fileExtensions)
    {
        lock (_lock)
        {
            _languageScopes[scopeName] = scopeName;
            if (!string.IsNullOrEmpty(languageId))
            {
                _languageScopes[languageId] = scopeName;
            }

            foreach (var extension in fileExtensions)
            {
                _extensionScopes[extension] = scopeName;
            }
        }
    }

    private void AddDiagnostic(string source, string message)
    {
        lock (_lock)
        {
            // A grammar compiles again when another grammar is registered; the same problem is reported only once.
            var diagnostic = new GrammarDiagnostic(source, message);
            if (!_diagnostics.Contains(diagnostic))
            {
                _diagnostics.Add(diagnostic);
            }
        }
    }

    // The rules and tokenizers of a compiled grammar report through this sink, under the grammar's scope name.
    private sealed class DiagnosticSink : IGrammarDiagnostics
    {
        private readonly GrammarRegistry _registry;

        public DiagnosticSink(GrammarRegistry registry)
        {
            _registry = registry;
        }

        public void Report(string scopeName, string message) => _registry.AddDiagnostic(scopeName, message);
    }

    private static GrammarInfo CreateInfo(string? languageId)
        => languageId is null ? GrammarInfo.Empty : new GrammarInfo { LanguageId = languageId };
}
