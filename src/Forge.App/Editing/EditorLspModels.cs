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

/// <summary>One other file's edits as they came off the wire; they are mapped against that file's text on the thread pool.</summary>
internal sealed record EditorLspFileEdit(string Path, IReadOnlyList<LspTextEdit> Edits);

/// <summary>What applying a workspace edit did; <paramref name="Error"/> means nothing was applied at all.</summary>
internal sealed record EditorLspWorkspaceEditResult(int Files, int Edits, int OtherFiles, int Dropped, string? Error = null)
{
    public static EditorLspWorkspaceEditResult Failed(string error) => new(0, 0, 0, 0, error);
}

/// <summary>Whether a rename may start at the caret, and the name the input box is seeded with (null = the word at the caret).</summary>
internal sealed record EditorLspPrepareRename(bool Allowed, string? Placeholder);

/// <summary>One inlay hint mapped to a document offset: the label drawn after that offset, on its line.</summary>
internal sealed record EditorInlayHint(int Line, int Offset, string Label, InlayHintKind? Kind, bool PaddingLeft, bool PaddingRight);

/// <summary>
/// Inlay hints bucketed per document line: the renderer reads the array of the line it is about to draw and never scans the
/// document, so the per-frame cost is one array lookup. <see cref="Version"/> is the document version the offsets were mapped
/// against, the same contract the semantic index has.
/// </summary>
internal sealed class InlayHintLineIndex
{
    private static readonly EditorInlayHint[] NoHints = [];
    private readonly EditorInlayHint[][] lines;

    private InlayHintLineIndex(EditorInlayHint[][] lines, int version, int hintCount, int dropped)
    {
        this.lines = lines;
        Version = version;
        HintCount = hintCount;
        Dropped = dropped;
    }

    /// <summary>An index that paints nothing; its version never matches a document.</summary>
    public static InlayHintLineIndex Empty { get; } = new([], -1, 0, 0);

    /// <summary>The document version the offsets were mapped against.</summary>
    public int Version { get; }

    public int HintCount { get; }

    /// <summary>Hints this index refused: an empty label (nothing to draw) or a second hint at an offset already taken.</summary>
    public int Dropped { get; }

    public bool IsEmpty => HintCount == 0;

    /// <summary>The hints of one 0-based line, sorted by offset; empty for a line outside the document.</summary>
    public ReadOnlySpan<EditorInlayHint> GetLine(int line) => (uint)line < (uint)lines.Length ? lines[line] : NoHints;

    /// <summary>
    /// Buckets <paramref name="hints"/> by line. Two hints at the same offset cannot both be drawn (a zero-length visual
    /// element is built once per offset), so the second is dropped and counted.
    /// </summary>
    public static InlayHintLineIndex Build(IReadOnlyList<EditorInlayHint> hints, int lineCount, int version)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (lineCount <= 0 || hints.Count == 0)
        {
            return new InlayHintLineIndex([], version, 0, hints.Count);
        }

        var buckets = new Dictionary<int, List<EditorInlayHint>>(Math.Min(hints.Count, 64));
        var dropped = 0;
        var count = 0;
        foreach (var hint in hints)
        {
            if (hint.Line < 0 || hint.Line >= lineCount || hint.Label.Length == 0)
            {
                dropped++;
                continue;
            }

            if (!buckets.TryGetValue(hint.Line, out var bucket))
            {
                bucket = new List<EditorInlayHint>(2);
                buckets[hint.Line] = bucket;
            }
            else if (bucket[^1].Offset == hint.Offset)
            {
                dropped++;
                continue;
            }

            bucket.Add(hint);
            count++;
        }

        if (count == 0)
        {
            return new InlayHintLineIndex([], version, 0, dropped);
        }

        var lines = new EditorInlayHint[lineCount][];
        foreach (var (line, bucket) in buckets)
        {
            bucket.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            lines[line] = [.. bucket];
        }

        return new InlayHintLineIndex(lines, version, count, dropped);
    }
}

internal sealed class EditorLspInlayHintsEventArgs(InlayHintLineIndex index) : EventArgs
{
    public InlayHintLineIndex Index { get; } = index;
}
