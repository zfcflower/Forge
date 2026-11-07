using AvaloniaEdit.Document;

namespace Forge.App.Editing;

/// <summary>Maps request offsets through edits made while a completion popup is open.</summary>
internal sealed class EditorLspChangeLog : IDisposable
{
    private readonly TextDocument document;
    private readonly List<(int Offset, int Removed, int Inserted)> changes = [];

    public EditorLspChangeLog(TextDocument document)
    {
        this.document = document;
        document.Changed += OnChanged;
    }

    public void Dispose() => document.Changed -= OnChanged;

    public bool TryMap(int offset, bool after, out int mapped)
    {
        mapped = offset;
        foreach (var (start, removed, inserted) in changes)
        {
            if (mapped < start || (mapped == start && removed > 0))
            {
                continue;
            }

            if (mapped == start)
            {
                if (after)
                {
                    mapped += inserted;
                }

                continue;
            }

            if (mapped >= start + removed)
            {
                mapped += inserted - removed;
                continue;
            }

            return false;
        }

        return true;
    }

    public bool TryMapUntouched(int start, int end, out int mappedStart, out int mappedEnd)
    {
        mappedStart = start;
        mappedEnd = end;
        foreach (var (offset, removed, inserted) in changes)
        {
            if (offset < mappedEnd && (offset + removed > mappedStart || (removed == 0 && offset >= mappedStart)))
            {
                return false;
            }

            if (offset + removed <= mappedStart)
            {
                var delta = inserted - removed;
                mappedStart += delta;
                mappedEnd += delta;
            }
        }

        return true;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e) =>
        changes.Add((e.Offset, e.RemovalLength, e.InsertionLength));
}

internal static class EditorLspEditApplier
{
    public static bool TryApply(TextDocument document, int caret, EditorLspCompletionItem item, EditorLspChangeLog log, out int newCaret)
    {
        newCaret = caret;
        if (!TryMap(item.Edit, isMain: true, log, document.TextLength, out var main) || main.Start > caret || caret > main.End)
        {
            return false;
        }

        var edits = new List<EditorLspEdit>(1 + item.AdditionalEdits.Length) { main };
        if (item.Edit.End > item.Edit.Start && main.End <= main.Start)
        {
            return false;
        }

        foreach (var additional in item.AdditionalEdits)
        {
            if (!TryMap(additional, isMain: false, log, document.TextLength, out var mapped))
            {
                return false;
            }

            edits.Add(mapped);
        }

        edits.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        for (var i = 1; i < edits.Count; i++)
        {
            if (edits[i].Start == edits[i - 1].Start || edits[i].Start < edits[i - 1].End)
            {
                return false;
            }
        }

        var shift = 0;
        foreach (var edit in edits)
        {
            if (edit != main && edit.End <= main.Start)
            {
                shift += edit.NewText.Length - (edit.End - edit.Start);
            }
        }

        using (document.RunUpdate())
        {
            for (var i = edits.Count - 1; i >= 0; i--)
            {
                document.Replace(edits[i].Start, edits[i].End - edits[i].Start, edits[i].NewText);
            }
        }

        newCaret = main.Start + shift + main.NewText.Length;
        return true;
    }

    private static bool TryMap(EditorLspEdit edit, bool isMain, EditorLspChangeLog log, int length, out EditorLspEdit mapped)
    {
        mapped = default;
        int start;
        int end;
        var ok = isMain
            ? log.TryMap(edit.Start, false, out start) & log.TryMap(edit.End, true, out end)
            : log.TryMapUntouched(edit.Start, edit.End, out start, out end);
        if (!ok || start > end || end > length)
        {
            return false;
        }

        mapped = edit with { Start = start, End = end };
        return true;
    }
}
