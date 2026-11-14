using Forge.App.Services;
using Xunit;

// The root namespace on purpose: declaring Forge.App.Tests.Services here would shadow Forge.App.Services for the
// tests that qualify FileIndex as Services.FileIndex (FileIndexTests).
namespace Forge.App.Tests;

/// <summary>
/// Pins <see cref="TextTail.Keep"/> (wave W8): the tail of one long text - the last characters first, then the last
/// lines of that - with a trailing newline never counted as a line of its own. The resource inspector uses it for the
/// streamed output of a running command; the CI fix chain and the checks panel use the same function, so "the last 200
/// lines" means one thing everywhere.
/// </summary>
public sealed class TextTailTests
{
    [Fact]
    public void The_last_lines_are_kept()
    {
        Assert.Equal("c\nd", TextTail.Keep("a\nb\nc\nd", maxLines: 2, maxChars: 1000));
    }

    [Fact]
    public void A_trailing_newline_does_not_count_as_a_line()
    {
        // Three lines plus the trailing newline: the last two lines are kept, and the newline stays with them. Without the
        // rule the trailing newline would consume one of the two lines and the answer would be "c\n".
        Assert.Equal("b\nc\n", TextTail.Keep("a\nb\nc\n", maxLines: 2, maxChars: 1000));
    }

    [Fact]
    public void The_last_characters_are_cut_first()
    {
        // The character cut slices the first line, which is expected: the point is the end of the log.
        Assert.Equal("ne2", TextTail.Keep("line1\nline2", maxLines: 10, maxChars: 3));

        // And it runs before the line cut: the character cut removes the newline after "aa", so three lines of room can
        // only find one newline in what is left, and the whole remaining text comes back.
        Assert.Equal("b\ncc", TextTail.Keep("aa\nbb\ncc", maxLines: 3, maxChars: 4));
    }

    [Fact]
    public void A_text_that_fits_is_returned_as_the_same_instance()
    {
        var text = "a\nb";
        Assert.Same(text, TextTail.Keep(text, maxLines: 5, maxChars: 1000));

        var empty = string.Empty;
        Assert.Same(empty, TextTail.Keep(empty, maxLines: 1, maxChars: 1));
    }

    [Fact]
    public void A_limit_below_one_and_a_null_text_are_rejected()
    {
        Assert.Throws<ArgumentNullException>("text", () => TextTail.Keep(null!, maxLines: 1, maxChars: 1));
        Assert.Throws<ArgumentOutOfRangeException>("maxLines", () => TextTail.Keep("x", maxLines: 0, maxChars: 1));
        Assert.Throws<ArgumentOutOfRangeException>("maxChars", () => TextTail.Keep("x", maxLines: 1, maxChars: 0));
    }
}