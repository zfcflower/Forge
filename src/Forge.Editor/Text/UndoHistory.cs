namespace Forge.Editor.Text;

internal sealed record UndoEntry(TextSnapshot Before, TextSnapshot After, TextChange Forward, TextChange Inverse, string? GroupName);

/// <summary>Bounded undo/redo stacks. Every entry is a single replacement, so replaying one is a constant number of rope joins.</summary>
internal sealed class UndoHistory
{
    private readonly int _maxEntries;
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];

    public UndoHistory(int maxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxEntries = maxEntries;
    }

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? PeekUndoGroupName => _undo.Count > 0 ? _undo[^1].GroupName : null;

    public string? PeekRedoGroupName => _redo.Count > 0 ? _redo[^1].GroupName : null;

    public void Push(UndoEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > _maxEntries)
        {
            _undo.RemoveRange(0, _undo.Count - _maxEntries);
        }

        _redo.Clear();
    }

    public UndoEntry? TakeUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        return entry;
    }

    public UndoEntry? TakeRedo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        return entry;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
