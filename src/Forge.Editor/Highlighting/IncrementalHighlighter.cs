// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// Keeps the tokens of a document current as it changes. An update re-tokenizes from the first changed line and stops as
// soon as a line ends in the state the line at that position ended in before: every later line keeps its tokens. The
// source must already hold the new text.
public sealed class IncrementalHighlighter
{
    private readonly Tokenizer _tokenizer;

    public IncrementalHighlighter(Tokenizer tokenizer, IHighlightSource source, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(source);
        _tokenizer = tokenizer;
        Source = source;
        Language = language;
        Current = tokenizer.Highlight(source, language, cancellationToken);
    }

    public IHighlightSource Source { get; }

    public string Language { get; }

    // The tokens of the current text. Published results never change, so a UI thread can hold one while the next is built.
    public HighlightResult Current { get; private set; }

    public HighlightResult HighlightAll(CancellationToken cancellationToken = default)
    {
        Current = _tokenizer.Highlight(Source, Language, cancellationToken);
        return Current;
    }

    // Brings the tokens up to date after the source changed.
    public HighlightResult Update(LineChange change, CancellationToken cancellationToken = default)
    {
        Current = _tokenizer.Update(Source, Current, change, cancellationToken);
        return Current;
    }

    // Re-resolves categories and colours for the current tokens without tokenizing again.
    public HighlightResult Retheme(ThemeMapper theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _tokenizer.Theme = theme;
        Current = _tokenizer.Retheme(Current);
        return Current;
    }
}
