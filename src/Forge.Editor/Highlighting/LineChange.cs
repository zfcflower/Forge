// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// What changed in the source since the last highlight: the first line that differs, how many lines the old text had
// there and how many the new text has. Replacing one line is (line, 1, 1); typing a new line is (line, 0, 1).
public readonly record struct LineChange(int StartLine, int RemovedLineCount, int InsertedLineCount)
{
    public static LineChange Replace(int line) => new(line, 1, 1);

    public static LineChange Insert(int line, int count) => new(line, 0, count);

    public static LineChange Remove(int line, int count) => new(line, count, 0);
}
