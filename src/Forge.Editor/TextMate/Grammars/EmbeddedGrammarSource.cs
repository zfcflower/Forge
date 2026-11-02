// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.TextMate.Grammars.Embedded;
using Forge.Editor.TextMate.Loading;

namespace Forge.Editor.TextMate.Grammars;

// A bundled grammar: registered without parsing, read the first time a grammar that needs it is compiled.
internal sealed class EmbeddedGrammarSource : IGrammarSource
{
    private readonly Lock _lock = new();
    private readonly EmbeddedGrammar _embedded;
    private readonly GrammarInfo _info;
    private RawGrammar? _grammar;

    public EmbeddedGrammarSource(EmbeddedGrammar embedded)
    {
        _embedded = embedded;
        _info = new GrammarInfo
        {
            LanguageId = embedded.LanguageId,
            FileExtensions = embedded.FileExtensions,
        };
    }

    public string ScopeName => _embedded.ScopeName;

    public GrammarInfo Info => _info;

    public RawGrammar GetGrammar()
    {
        var grammar = Volatile.Read(ref _grammar);
        if (grammar is not null)
        {
            return grammar;
        }

        lock (_lock)
        {
            _grammar ??= GrammarLoader.LoadText(_embedded.Content, DocumentFormat.Auto, _info).GetGrammar();
            return _grammar;
        }
    }
}
