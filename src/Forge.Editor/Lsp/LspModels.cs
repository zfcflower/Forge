using System.Buffers;
using System.Collections.Concurrent;
using Forge.Editor.Text;

namespace Forge.Editor.Lsp;

public readonly record struct LspPosition(int Line, int Character)
{
    public override string ToString() => $"{Line}:{Character}";
}

public readonly record struct LspRange(LspPosition Start, LspPosition End)
{
    public override string ToString() => $"{Start}-{End}";
}

public readonly record struct LspLocation(string Uri, LspRange Range);

public static class LspUri
{
    public static string FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
    }

    public static string? ToPath(string? uri) =>
        uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    public static bool SameFile(string uri, string path) =>
        string.Equals(ToPath(uri), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
}

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record LspDiagnosticRelatedInformation(LspLocation Location, string Message);

public sealed record LspDiagnostic(
    LspRange Range,
    DiagnosticSeverity Severity,
    string? Code,
    string? Source,
    string Message,
    IReadOnlyList<LspDiagnosticRelatedInformation>? RelatedInformation = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>Maps the range to document offsets in the given snapshot.</summary>
    public (int Start, int End) ToOffsets(TextSnapshot snapshot, PositionEncoding encoding) =>
        (snapshot.OffsetOfCharacter(Range.Start.Line, Range.Start.Character, encoding),
         snapshot.OffsetOfCharacter(Range.End.Line, Range.End.Character, encoding));
}

/// <summary>Immutable diagnostics of one document at one publish sequence.</summary>
public sealed record DiagnosticSnapshot(string Uri, int? Version, IReadOnlyList<LspDiagnostic> Diagnostics, long Sequence)
{
    public int ErrorCount => Count(DiagnosticSeverity.Error);

    public int WarningCount => Count(DiagnosticSeverity.Warning);

    private int Count(DiagnosticSeverity severity)
    {
        var count = 0;
        foreach (var diagnostic in Diagnostics)
        {
            if (diagnostic.Severity == severity)
            {
                count++;
            }
        }

        return count;
    }
}

public sealed class DiagnosticsChangedEventArgs(DiagnosticSnapshot snapshot) : EventArgs
{
    public DiagnosticSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
/// Last diagnostics per document, published as immutable snapshots so the UI can read without locking the server.
/// </summary>
public sealed class DiagnosticsStore
{
    private readonly ConcurrentDictionary<string, DiagnosticSnapshot> _byUri = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    public event EventHandler<DiagnosticsChangedEventArgs>? Changed;

    public DiagnosticSnapshot? Get(string uri) => _byUri.TryGetValue(uri, out var snapshot) ? snapshot : null;

    public DiagnosticSnapshot? GetForPath(string path) => string.IsNullOrWhiteSpace(path) ? null : Get(LspUri.FromPath(path));

    public IReadOnlyList<DiagnosticSnapshot> Snapshot() => [.. _byUri.Values];

    public int DocumentCount => _byUri.Count;

    public void Update(string uri, int? version, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        var snapshot = new DiagnosticSnapshot(uri, version, diagnostics, Interlocked.Increment(ref _sequence));
        _byUri[uri] = snapshot;
        Changed?.Invoke(this, new DiagnosticsChangedEventArgs(snapshot));
    }

    public void Remove(string uri)
    {
        if (_byUri.TryRemove(uri, out _))
        {
            Changed?.Invoke(this, new DiagnosticsChangedEventArgs(new DiagnosticSnapshot(uri, null, [], Interlocked.Increment(ref _sequence))));
        }
    }

    public void Clear()
    {
        foreach (var uri in _byUri.Keys)
        {
            Remove(uri);
        }
    }
}

public enum SymbolKind
{
    Unknown = 0,
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

public sealed record DocumentSymbol(
    string Name,
    string? Detail,
    SymbolKind Kind,
    LspRange Range,
    LspRange SelectionRange,
    IReadOnlyList<DocumentSymbol> Children)
{
    /// <summary>Depth-first walk over this symbol and its children.</summary>
    public IEnumerable<DocumentSymbol> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var nested in child.Flatten())
            {
                yield return nested;
            }
        }
    }
}

public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

public sealed record LspTextEdit(LspRange Range, string NewText);

/// <summary>The wire <c>insertTextFormat</c>. The client advertises no snippet support, so a server should send plain text.</summary>
public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

public sealed record CompletionItem(
    string Label,
    CompletionItemKind Kind,
    string? Detail,
    string? Documentation,
    string? InsertText,
    LspTextEdit? TextEdit,
    string? SortText,
    InsertTextFormat InsertTextFormat = InsertTextFormat.PlainText,
    IReadOnlyList<LspTextEdit>? AdditionalTextEdits = null,
    string? FilterText = null);

/// <param name="MalformedItems">Items the server sent with an invalid label, kind, format or edit; they are excluded and must be reported by the caller.</param>
public sealed record CompletionResult(IReadOnlyList<CompletionItem> Items, bool IsIncomplete, int MalformedItems = 0);

public sealed record HoverInfo(string Markup, LspRange? Range);

/// <summary>
/// One <c>textDocument/diagnostic</c> answer: either the document's diagnostics or "unchanged since the result id the
/// client sent".
/// </summary>
public sealed record PulledDiagnostics(bool Unchanged, IReadOnlyList<LspDiagnostic> Items, int Malformed = 0)
{
    public static PulledDiagnostics None { get; } = new(false, [], 0);
}

public readonly record struct SemanticToken(int Line, int StartChar, int Length, int Type, int ModifierBits = 0, string TypeName = "")
{
    public int EndChar => StartChar + Length;

    /// <summary>Alias of <see cref="ModifierBits"/>, named after the wire field.</summary>
    public int Modifiers => ModifierBits;
}

public sealed record SemanticTokensLegend(IReadOnlyList<string> TokenTypes, IReadOnlyList<string> TokenModifiers)
{
    public static SemanticTokensLegend Empty { get; } = new([], []);

    public string TypeName(int type) => type >= 0 && type < TokenTypes.Count ? TokenTypes[type] : "unknown";

    public IReadOnlyList<string> ModifierNames(int bits)
    {
        if (bits == 0 || TokenModifiers.Count == 0)
        {
            return [];
        }

        var names = new List<string>(2);
        for (var i = 0; i < TokenModifiers.Count && i < 32; i++)
        {
            if ((bits & (1 << i)) != 0)
            {
                names.Add(TokenModifiers[i]);
            }
        }

        return names;
    }
}

public sealed record SemanticTokensResult(string? ResultId, IReadOnlyList<SemanticToken> Tokens, SemanticTokensLegend Legend)
{
    public static SemanticTokensResult Empty(SemanticTokensLegend? legend = null) => new(null, [], legend ?? SemanticTokensLegend.Empty);

    public IReadOnlyList<string> ModifierNames(SemanticToken token) => Legend.ModifierNames(token.ModifierBits);
}
