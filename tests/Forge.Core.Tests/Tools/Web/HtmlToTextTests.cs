using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.Core.Tests.Tools.Web;

public sealed class HtmlToTextTests
{
    private static string Convert(string html, string? baseUrl = "https://example.com/dir/page", string? expectTitle = null)
    {
        var text = HtmlToText.Convert(html, baseUrl is null ? null : new Uri(baseUrl), out var title);
        if (expectTitle is not null)
        {
            Assert.Equal(expectTitle, title);
        }

        return text;
    }

    [Fact]
    public void Scripts_styles_nav_footer_svg_are_dropped()
    {
        var text = Convert("<html><head><style>p{color:red}</style><script>var a='<b>x</b>';</script></head><body><nav>MENU</nav><p>Hello</p><svg><text>ICON</text></svg><noscript>NS</noscript><footer>FOOT</footer></body></html>");
        Assert.Equal("Hello", text);
    }

    [Fact]
    public void Nested_skip_tags_are_dropped_as_a_whole()
    {
        Assert.Equal("after", Convert("<nav>a<nav>b</nav>c</nav>after"));
    }

    [Fact]
    public void Title_is_reported_and_not_in_body()
    {
        var text = Convert("<title> My  Page </title><p>Body</p>", expectTitle: "My Page");
        Assert.Equal("Body", text);
    }

    [Fact]
    public void Entities_are_decoded()
    {
        Assert.Equal("a & b < c \"d\" © A €", Convert("<p>a &amp; b &lt; c &quot;d&quot; &copy; &#65; &#x20AC;</p>"));
    }

    [Fact]
    public void Unknown_entity_is_kept()
    {
        Assert.Equal("&bogus; x", Convert("<p>&bogus; x</p>"));
    }

    [Fact]
    public void Headings_and_paragraphs_are_separated_by_blank_lines()
    {
        Assert.Equal("# Title\n\n## Sub\n\ntext one\n\ntext two", Convert("<h1>Title</h1><h2>Sub</h2><p>text   one</p><p>text\n two</p>"));
    }

    [Fact]
    public void Lists_use_dashes_and_nest_with_indent()
    {
        var text = Convert("<ul><li>one</li><li>two<ul><li>two-a</li></ul></li><li>three</li></ul>");
        Assert.Equal("- one\n- two\n  - two-a\n- three", text);
    }

    [Fact]
    public void Links_are_absolutized()
    {
        var text = Convert("<p>see <a href='/abs'>root</a>, <a href=\"rel?x=1&amp;y=2\">rel</a> and <a href='https://o.org/'>other</a></p>");
        Assert.Equal("see [root](https://example.com/abs), [rel](https://example.com/dir/rel?x=1&y=2) and [other](https://o.org/)", text);
    }

    [Fact]
    public void Non_http_and_fragment_links_keep_only_the_text()
    {
        Assert.Equal("a b c", Convert("<a href='javascript:x()'>a</a> <a href='#top'>b</a> <a>c</a>"));
    }

    [Fact]
    public void Pre_becomes_a_fenced_block_and_keeps_whitespace()
    {
        var text = Convert("<p>before</p><pre><code>int x = 1;\n  if (a &lt; b) {}</code></pre><p>after</p>");
        Assert.Equal("before\n\n```\nint x = 1;\n  if (a < b) {}\n```\n\nafter", text);
    }

    [Fact]
    public void Inline_code_uses_backticks()
    {
        Assert.Equal("use `foo()` now", Convert("<p>use <code>foo()</code> now</p>"));
    }

    [Theory]
    [InlineData("<p>unclosed <b>bold <a href='/x'>link")]
    [InlineData("<div><p>a</div></p></span></ul></li>")]
    [InlineData("<a href=\"unterminated")]
    [InlineData("<!-- never closed <p>x")]
    [InlineData("a < b > c <3 <<>>")]
    [InlineData("<script>never closed")]
    [InlineData("<")]
    [InlineData("")]
    [InlineData("&#99999999999; &#x; &;")]
    public void Malformed_html_does_not_throw(string html)
    {
        Assert.NotNull(Convert(html));
    }

    [Fact]
    public void Less_than_in_text_survives()
    {
        Assert.Equal("a < b", Convert("<p>a < b</p>"));
    }

    [Fact]
    public void Comments_are_dropped()
    {
        Assert.Equal("ab", Convert("a<!-- hidden <p>x</p> -->b"));
    }

    [Fact]
    public void Without_base_uri_relative_links_become_plain_text()
    {
        Assert.Equal("x", Convert("<a href='/rel'>x</a>", baseUrl: null));
    }

    [Fact]
    public void Br_breaks_the_line()
    {
        Assert.Equal("a\nb", Convert("a<br>b"));
    }
}
