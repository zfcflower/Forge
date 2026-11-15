using System.Text.Json;

namespace Forge.Editor.Lsp;

/// <summary>
/// The text edits a server wants in one file, normalised out of a workspace edit's <c>changes</c> map and its
/// <c>documentChanges</c> array. <paramref name="Uri"/> is the file URI the edits belong to; ranges are in the session's
/// negotiated position encoding.
/// </summary>
public sealed record LspFileEdit(string Uri, IReadOnlyList<LspTextEdit> Edits);

/// <summary>
/// A server edit across one or more files as this client understands it: text edits only.
/// <para>File resource operations (<c>create</c>/<c>rename</c>/<c>delete</c>) are not applied — advertising
/// <c>workspaceEdit.documentChanges</c> would promise them — so they are counted in
/// <paramref name="UnsupportedOperations"/> and the caller reports the edit as partial instead of applying half of a rename
/// silently.</para>
/// </summary>
public sealed record WorkspaceEdit(IReadOnlyList<LspFileEdit> Files, int UnsupportedOperations = 0)
{
    public static WorkspaceEdit Empty { get; } = new([], 0);

    public int FileCount => Files.Count;

    public int EditCount
    {
        get
        {
            var count = 0;
            foreach (var file in Files)
            {
                count += file.Edits.Count;
            }

            return count;
        }
    }
}

/// <summary>
/// One <c>textDocument/codeAction</c> answer entry. <paramref name="Raw"/> is the entry exactly as received, because
/// <c>codeAction/resolve</c> takes the whole action back; <paramref name="HasCommand"/> marks an action that carries only a
/// <c>Command</c>, which this client cannot execute (<c>workspace/executeCommand</c> is not advertised) and therefore does not
/// offer.
/// </summary>
public sealed record LspCodeAction(
    string Title,
    string? Kind,
    bool IsPreferred,
    bool Disabled,
    string? DisabledReason,
    WorkspaceEdit? Edit,
    bool HasCommand,
    JsonElement Raw)
{
    /// <summary>True when the action still needs <c>codeAction/resolve</c> before it has an edit to apply.</summary>
    public bool NeedsResolve => Edit is null && !HasCommand;
}

/// <summary>One code action answer: the actions the client can offer and the entries it had to drop, by reason.</summary>
public sealed record CodeActionResult(IReadOnlyList<LspCodeAction> Items, int Malformed = 0, int Unsupported = 0)
{
    public static CodeActionResult Empty { get; } = new([], 0, 0);
}

/// <summary>The wire kinds that change how a hint is drawn; the label token either a type or a parameter name.</summary>
public enum InlayHintKind
{
    Type = 1,
    Parameter = 2,
}

/// <summary>
/// One <c>textDocument/inlayHint</c> entry. <paramref name="Raw"/> is the entry exactly as received, because
/// <c>inlayHint/resolve</c> takes the whole hint back; a hint whose label only arrives after a resolve is kept with an empty
/// label (<see cref="NeedsResolve"/>) rather than dropped.
/// </summary>
public sealed record InlayHint(
    LspPosition Position,
    string Label,
    InlayHintKind? Kind,
    bool PaddingLeft,
    bool PaddingRight,
    JsonElement Raw)
{
    /// <summary>True when the label is still missing and the server declared <c>resolveProvider</c>.</summary>
    public bool NeedsResolve => Label.Length == 0;
}

/// <summary>
/// The answer to <c>textDocument/prepareRename</c>: the range a rename would replace and the placeholder the server wants the
/// input box seeded with. <paramref name="DefaultBehavior"/> means the server accepts a rename anywhere and has no range of
/// its own (LSP 3.16 <c>{ defaultBehavior: true }</c>).
/// </summary>
public sealed record RenamePreparation(LspRange Range, string? Placeholder = null, bool DefaultBehavior = false)
{
    /// <summary>The 3.16 "any position is fine" answer.</summary>
    public static RenamePreparation Anywhere { get; } = new(default, null, DefaultBehavior: true);
}
