// Adapted from Tint (https://github.com/sebastienros/tint, MIT License, Copyright (c) 2026 Sebastien Ros):
// see THIRD_PARTY_NOTICES.md for the license text and the sources it was itself derived from.

using System.Diagnostics.CodeAnalysis;
using Forge.Editor.TextMate.Grammars;
using Forge.Editor.TextMate.Internal.Rules;

namespace Forge.Editor.TextMate;

/// <summary>
/// The grammars a document can use: sources by scope name, injections, embedded language ids, and the compiled
/// grammars shared by the tokenizers it creates. Registration is not thread-safe; creating tokenizers is.
/// </summary>
internal sealed class GrammarStore
{
    private readonly Dictionary<string, IGrammarSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _injections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _languageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledGrammar> _compiled = new(StringComparer.Ordinal);
    private const int MaxLanguageId = 255;

    private readonly object _lock = new();
    private TokenizerOptions _tokenizerOptions = TokenizerOptions.Default;

    /// <summary>
    /// Registers a grammar, its language and the grammars it injects into. A grammar with the same scope name is
    /// replaced, along with the injections it declared; tokenizers created before keep the grammar they compiled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The grammar's language would need an id above 255.</exception>
    public void Add(IGrammarSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_lock)
        {
            // A replaced grammar no longer injects where it used to.
            if (_sources.TryGetValue(source.ScopeName, out var previous))
            {
                foreach (var target in previous.Info.InjectTo)
                {
                    if (_injections.TryGetValue(target, out var list))
                    {
                        list.Remove(previous.ScopeName);
                    }
                }
            }

            _sources[source.ScopeName] = source;
            if (source.Info.LanguageId is { } languageId)
            {
                GetLanguageIdCore(languageId);
            }

            foreach (var target in source.Info.InjectTo)
            {
                AddInjectionCore(target, source.ScopeName);
            }

            _compiled.Clear();
        }
    }

    /// <summary>Registers grammars in order.</summary>
    public void Add(params IGrammarSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            Add(source);
        }
    }

    /// <summary>Finds a registered grammar.</summary>
    public bool TryGet(string scopeName, [NotNullWhen(true)] out IGrammarSource? source)
    {
        ArgumentNullException.ThrowIfNull(scopeName);
        lock (_lock)
        {
            return _sources.TryGetValue(scopeName, out source);
        }
    }

    /// <summary>
    /// Makes the grammar <paramref name="injectionScopeName"/> inject into <paramref name="targetScopeName"/>, as
    /// <see cref="GrammarInfo.InjectTo"/> does.
    /// </summary>
    public void AddInjection(string targetScopeName, string injectionScopeName)
    {
        ArgumentNullException.ThrowIfNull(targetScopeName);
        ArgumentNullException.ThrowIfNull(injectionScopeName);
        lock (_lock)
        {
            AddInjectionCore(targetScopeName, injectionScopeName);
            _compiled.Clear();
        }
    }

    /// <summary>The injection grammars registered for a scope, in registration order.</summary>
    public IReadOnlyList<string> GetInjections(string targetScopeName)
    {
        ArgumentNullException.ThrowIfNull(targetScopeName);
        lock (_lock)
        {
            return _injections.TryGetValue(targetScopeName, out var list) ? [.. list] : [];
        }
    }

    /// <summary>
    /// The numeric id of a language, as stored in <see cref="EncodedTokenAttributes"/>: 1-based, assigned in the order
    /// languages are first seen (registered grammars' languages, then calls to this method, then embedded languages).
    /// </summary>
    /// <exception cref="InvalidOperationException">The language would need an id above 255, the largest the 8-bit field holds.</exception>
    public int GetLanguageId(string languageId)
    {
        ArgumentNullException.ThrowIfNull(languageId);
        lock (_lock)
        {
            return GetLanguageIdCore(languageId);
        }
    }

    /// <summary>Creates a tokenizer for a registered grammar; the compiled grammar is shared by the registry.</summary>
    public Tokenizer CreateTokenizer(string scopeName, TokenizerOptions? options = null)
    {
        return new Tokenizer(GetCompiledGrammar(scopeName), options ?? TokenizerOptions);
    }

    /// <summary>The options of the tokenizers this store creates when none are given.</summary>
    public TokenizerOptions TokenizerOptions
    {
        get => _tokenizerOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _tokenizerOptions = value;
        }
    }

    /// <summary>Where the grammars this store compiles report the problems they work around.</summary>
    public IGrammarDiagnostics? Diagnostics { get; set; }

    public bool Contains(string scopeName)
    {
        lock (_lock)
        {
            return _sources.ContainsKey(scopeName);
        }
    }

    public IReadOnlyList<string> ScopeNames
    {
        get
        {
            lock (_lock)
            {
                return [.. _sources.Keys];
            }
        }
    }

    /// <summary>The scope names a grammar includes, directly or through the grammars it includes, that are not registered.</summary>
    public IReadOnlyList<string> GetMissingGrammars(string scopeName) => GetCompiledGrammar(scopeName).MissingGrammars;

    private CompiledGrammar GetCompiledGrammar(string scopeName)
    {
        ArgumentNullException.ThrowIfNull(scopeName);
        lock (_lock)
        {
            if (_compiled.TryGetValue(scopeName, out var compiled))
            {
                return compiled;
            }

            if (!_sources.TryGetValue(scopeName, out var source))
            {
                throw new KeyNotFoundException($"No grammar is registered for the scope '{scopeName}'.");
            }

            var info = source.Info;
            var initialLanguageId = info.LanguageId is { } languageId ? GetLanguageIdCore(languageId) : 0;
            var embeddedLanguages = new List<KeyValuePair<string, int>>();
            foreach (var embedded in info.EmbeddedLanguages)
            {
                embeddedLanguages.Add(new KeyValuePair<string, int>(embedded.Key, GetLanguageIdCore(embedded.Value)));
            }

            compiled = new CompiledGrammar(source, new Repository(this), initialLanguageId, embeddedLanguages, Diagnostics);
            _compiled[scopeName] = compiled;
            return compiled;
        }
    }

    private int GetLanguageIdCore(string languageId)
    {
        if (!_languageIds.TryGetValue(languageId, out var id))
        {
            id = _languageIds.Count + 1;
            if (id > MaxLanguageId)
            {
                throw new InvalidOperationException($"Cannot assign an id to the language '{languageId}': encoded token attributes hold at most {MaxLanguageId} languages.");
            }

            _languageIds[languageId] = id;
        }

        return id;
    }

    private void AddInjectionCore(string target, string injection)
    {
        if (!_injections.TryGetValue(target, out var list))
        {
            list = [];
            _injections[target] = list;
        }

        if (!list.Contains(injection))
        {
            list.Add(injection);
        }
    }

    // Called under the registry lock while a grammar compiles.
    private sealed class Repository : IGrammarRepository
    {
        private readonly GrammarStore _registry;

        public Repository(GrammarStore registry)
        {
            _registry = registry;
        }

        public IGrammarSource? Lookup(string scopeName) => _registry._sources.GetValueOrDefault(scopeName);

        public IReadOnlyList<string> GetInjections(string targetScopeName)
            => _registry._injections.TryGetValue(targetScopeName, out var list) ? list : [];
    }
}
