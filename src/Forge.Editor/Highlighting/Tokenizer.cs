// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.Text;
using Forge.Editor.TextMate;
using Forge.Editor.TextMate.Internal;

namespace Forge.Editor.Highlighting;

// Turns lines into tokens with one grammar: the entry point for a full pass and the engine an incremental highlighter
// drives line by line. Not thread-safe: keep one per open document and reuse it, since it owns the grammar's scanners and
// the interning of the document's scope stacks.
public sealed class Tokenizer
{
    private readonly Lock _lock = new();
    private readonly GrammarRegistry _registry;
    private readonly Dictionary<string, LineTokenizer> _tokenizers = new(StringComparer.Ordinal);
    private readonly TokenizerOptions _options;

    private ThemeMapper _theme;
    private TokenCategory[] _categories = [];
    private int[] _foregrounds = [];
    private int _styled;
    private long _version;

    public Tokenizer(GrammarRegistry registry, ThemeMapper? theme = null, TokenizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _theme = theme ?? ThemeMapper.CreateDefault();
        _options = options ?? TokenizerOptions.Default;
    }

    public GrammarRegistry Registry => _registry;

    public ScopeTable Scopes { get; } = new();

    // The rules that map scope stacks to categories and colours. Setting it re-resolves the current result without
    // tokenizing again.
    public ThemeMapper Theme
    {
        get
        {
            lock (_lock)
            {
                return _theme;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
            {
                _theme = value;
                _styled = 0;
            }
        }
    }

    // Grammar patterns that could not be translated; the rules that use them match nothing.
    public IReadOnlyCollection<string> Diagnostics
    {
        get
        {
            lock (_lock)
            {
                var errors = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tokenizer in _tokenizers.Values)
                {
                    errors.UnionWith(tokenizer.Errors);
                }

                return errors;
            }
        }
    }

    public IReadOnlyList<string> MissingGrammars(string language)
    {
        lock (_lock)
        {
            return GetTokenizer(language).MissingGrammars;
        }
    }

    // Highlights every line of a source.
    public HighlightResult Highlight(IHighlightSource source, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var tokenizer = GetTokenizer(language);
            var lines = new HighlightLineData[source.LineCount];
            var state = tokenizer.Initial;
            for (var i = 0; i < lines.Length; i++)
            {
                if ((i & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var data = tokenizer.TokenizeLine(source.GetLine(i), state);
                lines[i] = data;
                state = data.EndState;
            }

            return Publish(tokenizer.ScopeName, lines);
        }
    }

    public HighlightResult HighlightText(string text, string language, CancellationToken cancellationToken = default)
        => Highlight(new StringHighlightSource(text), language, cancellationToken);

    // Highlights a document snapshot through its span-based line source instead of materialising every line as a string.
    public HighlightResult Highlight(TextSnapshot snapshot, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Highlight(new TextSnapshotHighlightSource(snapshot), language, cancellationToken);
    }

    internal HighlightResult Update(IHighlightSource source, HighlightResult previous, LineChange change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var tokenizer = GetTokenizer(previous.ScopeName);
            var oldLines = previous.Lines;
            var oldCount = previous.LineCount;
            var newCount = source.LineCount;
            var start = Math.Clamp(change.StartLine, 0, oldCount);
            var removed = Math.Clamp(change.RemovedLineCount, 0, oldCount - start);
            var inserted = Math.Max(0, newCount - (oldCount - removed));

            var lines = new HighlightLineData[newCount];
            Array.Copy(oldLines, lines, start);

            var delta = inserted - removed;
            var state = start == 0 ? tokenizer.Initial : oldLines[start - 1].EndState;
            var index = start;
            for (; index < newCount; index++)
            {
                if ((index & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var data = tokenizer.TokenizeLine(source.GetLine(index), state);
                lines[index] = data;

                var oldIndex = index - delta;
                if (index >= start + inserted && oldIndex >= start + removed && oldIndex < oldCount && data.EndState.Equals(oldLines[oldIndex].EndState))
                {
                    // The state converged: every later line keeps the tokens it had.
                    Array.Copy(oldLines, oldIndex + 1, lines, index + 1, oldCount - oldIndex - 1);
                    index = newCount;
                    break;
                }

                state = data.EndState;
            }

            return Publish(tokenizer.ScopeName, lines);
        }
    }

    internal HighlightResult Retheme(HighlightResult previous)
    {
        lock (_lock)
        {
            ResolveStyles();
            return previous.WithTheme([.. _categories], [.. _foregrounds]);
        }
    }

    private HighlightResult Publish(string scopeName, HighlightLineData[] lines)
    {
        ResolveStyles();
        return new HighlightResult(Scopes, lines, lines.Length, [.. _categories], [.. _foregrounds], ++_version, scopeName);
    }

    // Fills the per-scope-id category and colour tables for the scopes interned so far.
    private void ResolveStyles()
    {
        var count = Scopes.Count;
        if (_categories.Length < count)
        {
            Array.Resize(ref _categories, count);
            Array.Resize(ref _foregrounds, count);
        }

        var foregrounds = _theme.Foregrounds;
        var render = _theme;
        for (; _styled < count; _styled++)
        {
            var category = render.Map(Scopes.GetScopes(_styled));
            _categories[_styled] = category;
            _foregrounds[_styled] = foregrounds[(int)category];
        }
    }

    private LineTokenizer GetTokenizer(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!_registry.TryGetScopeName(language, out var scopeName))
        {
            throw new KeyNotFoundException($"No grammar is registered for '{language}'.");
        }

        if (_tokenizers.TryGetValue(scopeName, out var tokenizer))
        {
            return tokenizer;
        }

        tokenizer = new LineTokenizer(_registry.Store.CreateTokenizer(scopeName, _options), Scopes);
        _tokenizers[scopeName] = tokenizer;
        return tokenizer;
    }
}
