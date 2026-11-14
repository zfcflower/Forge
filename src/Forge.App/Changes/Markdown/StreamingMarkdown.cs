using System.Text;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// The Markdown of one streaming reply as it grows: a committed block prefix, published once and never re-parsed, plus the
/// live region after the last block boundary, re-parsed on every append.
/// <para>The block boundary is the last blank line outside a fenced code block. A block that can still absorb following lines
/// (<see cref="MdBlockKind.List"/>, indented code, an HTML block) is not committed at that boundary: the prefix then ends on
/// that block's first line and the next append re-parses it, so a loose list is never rendered as two lists while it streams.
/// </para>
/// <para><see cref="Complete"/> re-parses the whole reply once and replaces the incremental result when the two differ
/// (indented code across a blank line, an HTML comment across one, a fence this line-level scan judged differently, or an
/// over-long paragraph committed in fragments), so a completed document renders exactly like a single
/// <see cref="MarkdownBlockParser.Parse"/> of every line. That equality is the invariant the chat renderer relies on.</para>
/// </summary>
internal sealed class StreamingMarkdown
{
    /// <summary>
    /// A live region that is a single paragraph longer than this is committed in line-aligned fragments. Without it every
    /// frame would re-parse and re-shape the whole paragraph, which is quadratic in the length of the reply; the fragments are
    /// ordinary paragraph lines, and <see cref="Complete"/> merges them back into one paragraph if the reply ends there.
    /// </summary>
    private const int ParagraphFragmentLimit = 4096;

    private readonly StringBuilder source = new();
    private readonly List<string> lines = [];
    private readonly StringBuilder line = new();
    private readonly List<MdBlock> stable = [];
    private readonly List<MdBlock> live = [];
    private readonly List<string> slice = [];
    private char fenceChar;
    private int fenceLength;
    private int scanned;
    private int boundary;
    private int stableLine;
    private string? text;

    /// <summary>Every character appended so far, exactly as it arrived: what the copy button puts on the clipboard.</summary>
    public string Source => text ??= source.ToString();

    /// <summary>
    /// The parsed blocks of the lines before <see cref="StableLine"/>. Append-only: an index published here keeps its meaning
    /// and its rendered control for the rest of the reply.
    /// </summary>
    public IReadOnlyList<MdBlock> Stable => stable;

    /// <summary>The parsed blocks of everything after the last committed boundary, including the line still being written.</summary>
    public IReadOnlyList<MdBlock> Live => live;

    /// <summary>Nothing has been appended yet.</summary>
    public bool IsEmpty => source.Length == 0;

    /// <summary>
    /// How many lines have ended (with a newline, or by <see cref="Complete"/>). A line numbered at most this never changes
    /// again, so a renderer may keep what it built from such lines - a table row, or a list item another item follows.
    /// </summary>
    public int CompleteLines => lines.Count;

    /// <summary>
    /// Bumped whenever <see cref="Stable"/> stops being an extension of what it was - the one time that happens is a
    /// <see cref="Complete"/> that replaced the incremental parse - so a renderer compares one number instead of every block.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>Whether two blocks from this source are the same block, so a renderer may keep the control it already built.</summary>
    /// <remarks>
    /// The content of a block is a pure function of its kind and its source lines, so equal ranges and kind mean equal content.
    /// </remarks>
    internal static bool SameBlock(MdBlock left, MdBlock right) =>
        left.Kind == right.Kind && left.StartLine == right.StartLine && left.EndLine == right.EndLine;

    /// <summary>Appends one streamed chunk and re-parses only what is not committed yet.</summary>
    public void Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0)
        {
            return;
        }

        text = null;
        source.Append(chunk);
        var rest = chunk.AsSpan();
        while (true)
        {
            var end = rest.IndexOf('\n');
            AppendLineText(end < 0 ? rest : rest[..end]);
            if (end < 0)
            {
                break;
            }

            lines.Add(line.ToString());
            line.Clear();
            rest = rest[(end + 1)..];
        }

        Scan();
        Commit();
        ParseLive();
    }

    /// <summary>
    /// Closes the reply: the unfinished line becomes a line, and the whole text is parsed once to correct anything the
    /// incremental split got wrong. Idempotent.
    /// </summary>
    public void Complete()
    {
        if (line.Length > 0)
        {
            lines.Add(line.ToString());
            line.Clear();
            Scan();
        }

        var whole = MarkdownBlockParser.Parse(lines);
        ParseLive();
        if (SameSequence(whole))
        {
            return;
        }

        stable.Clear();
        stable.AddRange(whole);
        live.Clear();
        stableLine = lines.Count;
        Revision++;
    }

    /// <summary>Adds one stretch of a line (no newline in it) to the line being written, dropping carriage returns.</summary>
    private void AppendLineText(ReadOnlySpan<char> part)
    {
        while (true)
        {
            var cr = part.IndexOf('\r');
            if (cr < 0)
            {
                line.Append(part);
                return;
            }

            line.Append(part[..cr]);
            part = part[(cr + 1)..];
        }
    }

    /// <summary>Scans the lines that ended since the last call for fences and for the latest block boundary.</summary>
    private void Scan()
    {
        for (; scanned < lines.Count; scanned++)
        {
            var current = lines[scanned];
            if (fenceLength > 0)
            {
                if (ClosesFence(current))
                {
                    fenceLength = 0;
                }

                continue;
            }

            if (TryOpenFence(current, out var ch, out var length))
            {
                fenceChar = ch;
                fenceLength = length;
                continue;
            }

            if (IsBlank(current))
            {
                boundary = scanned + 1;
            }
        }
    }

    /// <summary>Moves the block boundary into the committed prefix, keeping a block that can still grow in the live region.</summary>
    private void Commit()
    {
        if (boundary <= stableLine)
        {
            return;
        }

        var parsed = ParseRange(stableLine, boundary);
        var count = parsed.Count;
        if (count > 0 && Extends(parsed[^1], boundary))
        {
            // The block can still absorb what follows, so it stays live until one line decides it.
            count--;
        }

        for (var index = 0; index < count; index++)
        {
            stable.Add(parsed[index]);
        }

        if (count == parsed.Count)
        {
            // Everything up to the boundary is committed; whatever is left of it was blank.
            stableLine = boundary;
        }
        else if (count > 0)
        {
            stableLine = parsed[count - 1].EndLine;
        }
    }

    /// <summary>Re-parses the live region, committing it in fragments while it is one over-long paragraph.</summary>
    private void ParseLive()
    {
        while (true)
        {
            ParseLiveOnce();
            if (!CommitFragment())
            {
                return;
            }
        }
    }

    private void ParseLiveOnce()
    {
        live.Clear();
        slice.Clear();
        for (var index = stableLine; index < lines.Count; index++)
        {
            slice.Add(lines[index]);
        }

        // A high surrogate at the very end is half a character whose other half is still in the next chunk: leave it out until
        // it is whole, so the live tail never renders a broken glyph.
        var pending = line.Length > 0 && char.IsHighSurrogate(line[^1]) ? line.Length - 1 : line.Length;
        if (pending > 0)
        {
            slice.Add(line.ToString(0, pending));
        }

        if (slice.Count > 0)
        {
            live.AddRange(MarkdownBlockParser.Parse(slice, stableLine + 1, top: stableLine == 0));
        }
    }

    /// <summary>
    /// Commits the complete lines of an over-long single-paragraph live region as paragraph blocks, leaving only the line still
    /// being written live. Returns whether it committed anything, in which case the live region must be parsed again.
    /// </summary>
    private bool CommitFragment()
    {
        if (live.Count != 1 || live[0].Kind != MdBlockKind.Paragraph || live[0].PlainText.Length <= ParagraphFragmentLimit ||
            lines.Count <= stableLine)
        {
            return false;
        }

        var parsed = ParseRange(stableLine, lines.Count);
        foreach (var block in parsed)
        {
            if (block.Kind != MdBlockKind.Paragraph)
            {
                return false;
            }
        }

        stable.AddRange(parsed);
        stableLine = lines.Count;
        return true;
    }

    /// <summary>Parses <paramref name="from" /> up to but excluding <paramref name="to" />, numbered like the whole document.</summary>
    private List<MdBlock> ParseRange(int from, int to)
    {
        slice.Clear();
        for (var index = from; index < to; index++)
        {
            slice.Add(lines[index]);
        }

        return slice.Count == 0 ? [] : MarkdownBlockParser.Parse(slice, from + 1, top: from == 0);
    }

    private bool SameSequence(List<MdBlock> whole)
    {
        if (stable.Count + live.Count != whole.Count)
        {
            return false;
        }

        for (var index = 0; index < stable.Count; index++)
        {
            if (!SameBlock(stable[index], whole[index]))
            {
                return false;
            }
        }

        for (var index = 0; index < live.Count; index++)
        {
            if (!SameBlock(live[index], whole[stable.Count + index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the block that ends at the boundary can still absorb the lines after it: a list continues while the next content
    /// line is a list item, indented code while it is indented, and an HTML comment until it is closed. A line that is still
    /// being written only counts when it could still become a continuation, so a half-written marker does not close a list early.
    /// </summary>
    private bool Extends(MdBlock block, int from)
    {
        if (block.Kind == MdBlockKind.Html)
        {
            // Same test as the parser's: an indented comment is a comment too, and only an unterminated one runs past a blank line.
            return block.Text.AsSpan().TrimStart().StartsWith("<!--", StringComparison.Ordinal) &&
                !block.Text.Contains("-->", StringComparison.Ordinal);
        }

        // A fenced code block (and YAML front matter) is closed as soon as it parsed; only indented code can absorb more lines.
        if (block.Kind is not (MdBlockKind.List or MdBlockKind.Code) ||
            (block.Kind == MdBlockKind.Code && Indent(lines[block.StartLine - 1]) < 4))
        {
            return false;
        }

        for (var index = from; index < lines.Count; index++)
        {
            if (!IsBlank(lines[index]))
            {
                return Extends(block, lines[index], growing: false);
            }
        }

        // Nothing follows it yet, so it may still continue: keep it live until one line decides that.
        if (line.Length == 0)
        {
            return true;
        }

        var pending = line.ToString();
        return IsBlank(pending) || Extends(block, pending, growing: true);
    }

    private static bool Extends(MdBlock block, ReadOnlySpan<char> next, bool growing) => block.Kind switch
    {
        MdBlockKind.Code => Indent(next) >= 4 || (growing && Indent(next) >= next.Length),
        MdBlockKind.List => ContinuesList(block, next, growing),
        _ => false,
    };

    private static bool ContinuesList(MdBlock block, ReadOnlySpan<char> next, bool growing)
    {
        var indent = Indent(next);
        if (indent > 3)
        {
            return false;
        }

        if (indent >= next.Length)
        {
            return growing;
        }

        var ch = next[indent];
        if (ch is not ('-' or '+' or '*' or >= '0' and <= '9'))
        {
            return false;
        }

        return growing || MarkdownBlockParser.StartsListItem(next.ToString(), block.Ordered);
    }

    private bool ClosesFence(string current)
    {
        if (Indent(current) > 3)
        {
            return false;
        }

        var span = current.AsSpan().Trim();
        if (span.Length < fenceLength)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c != fenceChar)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryOpenFence(string current, out char ch, out int length)
    {
        ch = default;
        length = 0;
        var indent = Indent(current);
        if (indent > 3)
        {
            return false;
        }

        var rest = current.AsSpan(indent);
        if (rest.Length < 3 || (rest[0] != '`' && rest[0] != '~'))
        {
            return false;
        }

        ch = rest[0];
        while (length < rest.Length && rest[length] == ch)
        {
            length++;
        }

        if (length < 3)
        {
            return false;
        }

        var tail = rest[length..];
        return ch != '`' || tail.IndexOf('`') < 0;
    }

    private static int Indent(ReadOnlySpan<char> current)
    {
        var indent = 0;
        while (indent < current.Length && current[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static bool IsBlank(string current)
    {
        foreach (var c in current)
        {
            if (c != ' ' && c != '\t' && c != '\r')
            {
                return false;
            }
        }

        return true;
    }
}
