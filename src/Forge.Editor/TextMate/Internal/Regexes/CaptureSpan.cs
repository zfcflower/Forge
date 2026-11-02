// Governed by THIRD_PARTY_NOTICES.md: the TextMate regex pipeline is adapted from Tint (MIT, Sebastien Ros).

using System.Text.RegularExpressions;

namespace Forge.Editor.TextMate.Internal.Regexes;

// A capture of a match as vscode-textmate sees it: start and end offsets in the line. Groups that did not participate
// are (0, 0), as Onigwrap reports them; vscode-textmate treats them like any empty capture.
internal readonly struct CaptureSpan
{
    public CaptureSpan(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public int Length => End - Start;
}

// One group of a match over the searched line, offset by where the search started.
internal readonly struct OnigCapture
{
    public OnigCapture(int index, int length, bool participated)
    {
        Index = index;
        Length = length;
        Participated = participated;
    }

    public int Index { get; }

    public int Length { get; }

    public bool Participated { get; }

    public int End => Index + Length;
}
