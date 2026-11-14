using Forge.Editor.Highlighting;
using Forge.Editor.Lsp;
using Forge.Editor.Text;

namespace Forge.App.Editing;

internal readonly record struct EditorLspEdit(int Start, int End, string NewText);

internal sealed record EditorLspCompletionItem(
    string Label,
    string FilterText,
    CompletionItemKind Kind,
    string? Detail,
    string? Documentation,
    string SortKey,
    EditorLspEdit Edit,
    EditorLspEdit[] AdditionalEdits);

internal sealed record EditorLspCompletion(
    int Version,
    int RequestOffset,
    int PrefixStart,
    IReadOnlyList<EditorLspCompletionItem> Items,
    bool IsIncomplete);

internal sealed record EditorLspCompletionMap(EditorLspCompletion Completion, int ConvertedSnippets, int InvalidItems);

internal sealed record EditorLspHover(int Version, int Start, int End, string Text);

internal enum EditorLspDefinitionKind
{
    SameFile,
    OtherFile,
}

internal sealed record EditorLspDefinitionTarget(string Path, LspPosition Position, PositionEncoding Encoding);

internal sealed record EditorLspDefinition(EditorLspDefinitionKind Kind, int Offset, EditorLspDefinitionTarget Target, int Count);

internal sealed record EditorLspDiagnostic(int Start, int End, DiagnosticSeverity Severity, string Message, string? Code, string? Source);

internal sealed record EditorLspDiagnosticSet(int Version, IReadOnlyList<EditorLspDiagnostic> Items, int Malformed = 0)
{
    public static EditorLspDiagnosticSet Empty { get; } = new(-1, []);
}

internal sealed class EditorLspDiagnosticsEventArgs(EditorLspDiagnosticSet set) : EventArgs
{
    public EditorLspDiagnosticSet Set { get; } = set;
}

/// <summary>
/// One place a symbol is referenced. <paramref name="Line"/> and <paramref name="Column"/> are 1-based, for display only;
/// navigation goes through <paramref name="Target"/>, which carries the session's position encoding.
/// </summary>
internal sealed record EditorLspReference(string Path, int Line, int Column, EditorLspDefinitionTarget Target);

/// <summary>The places a symbol is used, with the identifier the request was made for (empty when the caret was not on one).</summary>
internal sealed record EditorLspReferenceList(string Symbol, IReadOnlyList<EditorLspReference> Items, bool Truncated = false);

internal sealed class EditorLspSemanticTokensEventArgs(SemanticLineIndex index) : EventArgs
{
    public SemanticLineIndex Index { get; } = index;
}
