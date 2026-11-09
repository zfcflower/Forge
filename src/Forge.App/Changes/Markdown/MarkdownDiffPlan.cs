using Forge.Workspace;

namespace Forge.App.Changes.Markdown;

internal enum MdItemKind { Bar, Deleted, Block, Gap }

/// <summary>One row of the preview: a change bar, a removed block, a (new-side) block, or a non-expandable gap between hunks.</summary>
internal sealed record MdItem(MdItemKind Kind, MdBlock? Block = null, bool Changed = false, IDiffBlockItem? Bar = null, int GapLines = 0);

/// <summary>Pure planning of the Markdown preview: parses the new side once and places change bars and removed blocks.</summary>
internal static class MarkdownDiffPlan
{
    public static List<MdItem> Build(IDiffFileItem file)
    {
        var diff = file.Diff;
        var changes = new List<(ChangeBlock Block, IDiffBlockItem? Item)>();
        if (file.Blocks.Count > 0)
        {
            foreach (var item in file.Blocks)
            {
                changes.Add((item.Block, item));
            }
        }
        else
        {
            foreach (var block in DiffBlocks.Split(diff))
            {
                changes.Add((block, null));
            }
        }

        var items = new List<MdItem>();
        var consumed = new bool[changes.Count];
        var previousEnd = 0;
        var firstSegment = true;
        foreach (var hunk in diff.Hunks)
        {
            var newLines = new List<string>(hunk.Lines.Count);
            var startNew = hunk.NewCount == 0 ? hunk.NewStart + 1 : hunk.NewStart;
            var seenFirst = false;
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Removed)
                {
                    continue;
                }

                if (!seenFirst)
                {
                    startNew = line.NewLineNumber;
                    seenFirst = true;
                }

                newLines.Add(line.Text);
            }

            if (!firstSegment && startNew - previousEnd - 1 > 0)
            {
                items.Add(new MdItem(MdItemKind.Gap, GapLines: startNew - previousEnd - 1));
            }

            firstSegment = false;
            previousEnd = startNew + newLines.Count - 1;

            var blocks = MarkdownBlockParser.Parse(newLines, startNew);
            var changed = new bool[blocks.Count];
            var anchors = new List<(ChangeBlock Block, IDiffBlockItem? Item)>?[blocks.Count + 1];
            for (var ci = 0; ci < changes.Count; ci++)
            {
                var (change, bar) = changes[ci];
                if (consumed[ci] || !Belongs(hunk, change))
                {
                    continue;
                }

                consumed[ci] = true;
                var anchor = -1;
                if (change.NewCount > 0)
                {
                    for (var k = 0; k < blocks.Count; k++)
                    {
                        if (blocks[k].Intersects(change.NewStart, change.NewEnd))
                        {
                            changed[k] = true;
                            if (anchor < 0)
                            {
                                anchor = k;
                            }
                        }
                    }
                }

                if (anchor < 0)
                {
                    // A pure deletion (or blank-only insertion) sits after line "a": before the first block that continues past it.
                    var a = change.NewCount > 0 ? change.NewStart - 1 : change.NewStart;
                    anchor = blocks.Count;
                    for (var k = 0; k < blocks.Count; k++)
                    {
                        if (blocks[k].EndLine > a)
                        {
                            anchor = k;
                            if (change.OldCount > 0 && a >= 1 && blocks[k].StartLine <= a)
                            {
                                changed[k] = true;
                            }

                            break;
                        }
                    }
                }

                (anchors[anchor] ??= []).Add((change, bar));
            }

            for (var k = 0; k <= blocks.Count; k++)
            {
                if (anchors[k] is { } list)
                {
                    foreach (var (change, bar) in list)
                    {
                        if (bar is not null)
                        {
                            items.Add(new MdItem(MdItemKind.Bar, Bar: bar));
                        }

                        if (change.OldCount > 0)
                        {
                            foreach (var removed in MarkdownBlockParser.Parse(change.OldLines, Math.Max(1, change.OldStart)))
                            {
                                items.Add(new MdItem(MdItemKind.Deleted, removed));
                            }
                        }
                    }
                }

                if (k < blocks.Count)
                {
                    items.Add(new MdItem(MdItemKind.Block, blocks[k], changed[k]));
                }
            }
        }

        return items;
    }

    private static bool Belongs(DiffHunk hunk, ChangeBlock change)
    {
        if (change.NewCount > 0)
        {
            return change.NewStart >= hunk.NewStart && change.NewStart <= Math.Max(hunk.NewEnd, hunk.NewStart);
        }

        if (change.OldCount > 0)
        {
            return change.OldStart >= hunk.OldStart && change.OldStart <= Math.Max(hunk.OldEnd, hunk.OldStart);
        }

        return false;
    }
}
