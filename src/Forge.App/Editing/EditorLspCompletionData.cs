using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Forge.App.Editing;

internal sealed class EditorLspCompletionData(
    EditorLspCompletionItem item,
    int rank,
    EditorLspChangeLog log,
    Action applyFailed) : ICompletionData
{
    private readonly string? description = BuildDescription(item);

    public IImage Image => null!;
    public string Text => item.FilterText;
    public object Content => item.Label;
    public object Description => description!;
    public double Priority => -rank;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (insertionRequestEventArgs is TextInputEventArgs)
        {
            return;
        }

        var document = textArea.Document;
        if (document is null || textArea.IsReadOnly)
        {
            return;
        }

        if (!EditorLspEditApplier.TryApply(document, textArea.Caret.Offset, item, log, out var caret))
        {
            applyFailed();
            return;
        }

        textArea.Caret.Offset = caret;
        textArea.ClearSelection();
    }

    private static string? BuildDescription(EditorLspCompletionItem item) =>
        (item.Detail, item.Documentation) switch
        {
            (null or "", null or "") => null,
            (null or "", var documentation) => documentation,
            (var detail, null or "") => detail,
            var (detail, documentation) => detail + "\n\n" + documentation,
        };
}
