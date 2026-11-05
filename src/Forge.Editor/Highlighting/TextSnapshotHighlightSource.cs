// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.Text;

namespace Forge.Editor.Highlighting;

// An IHighlightSource over an immutable text snapshot. The whole text is materialised once so each line is returned as
// a span into that string: reading a line allocates nothing, however many rope leaves it spans.
public sealed class TextSnapshotHighlightSource : IHighlightSource
{
    private readonly string _text;

    public TextSnapshotHighlightSource(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        _text = snapshot.GetText();
    }

    public TextSnapshot Snapshot { get; }

    public int LineCount => Snapshot.LineCount;

    public ReadOnlySpan<char> GetLine(int index)
    {
        var start = Snapshot.GetLineStart(index);
        var end = Snapshot.GetLineEnd(index);
        return _text.AsSpan(start, end - start);
    }
}
