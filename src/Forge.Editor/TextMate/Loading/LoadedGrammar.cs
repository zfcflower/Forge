// Adapted from Tint (https://github.com/sebastienros/tint, MIT License, Copyright (c) 2026 Sebastien Ros):
// see THIRD_PARTY_NOTICES.md for the license text and the sources it was itself derived from.

using Forge.Editor.TextMate.Grammars;

namespace Forge.Editor.TextMate.Loading;

/// <summary>A grammar read from a file by <see cref="GrammarLoader"/>. Its regexes are parsed on first use.</summary>
internal sealed class LoadedGrammar : IGrammarSource
{
    private readonly RawGrammar _grammar;

    internal LoadedGrammar(RawGrammar grammar, GrammarInfo info)
    {
        _grammar = grammar;
        Info = info;
    }

    /// <inheritdoc/>
    public string ScopeName => _grammar.ScopeName;

    /// <inheritdoc/>
    public GrammarInfo Info { get; }

    /// <inheritdoc/>
    public RawGrammar GetGrammar() => _grammar;
}
