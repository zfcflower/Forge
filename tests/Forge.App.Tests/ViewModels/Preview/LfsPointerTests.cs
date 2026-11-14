using System.Text;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels.Preview;

public sealed class LfsPointerTests
{
    [Fact]
    public void A_valid_pointer_parses_oid_size_and_version()
    {
        const string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nsize 1048576\n";

        var parsed = LfsPointerParser.Parse(Encoding.UTF8.GetBytes(pointer));

        Assert.Equal("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", parsed.Oid);
        Assert.Equal(1048576, parsed.Size);
        Assert.Equal("https://git-lfs.github.com/spec/v1", parsed.Version);
    }

    [Fact]
    public void A_pointer_without_a_size_line_still_previews_with_what_it_has()
    {
        const string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:abcd\n";

        var parsed = LfsPointerParser.Parse(Encoding.UTF8.GetBytes(pointer));

        Assert.Null(parsed.Size);
        Assert.Equal("sha256:abcd", parsed.Oid);
        Assert.Equal("https://git-lfs.github.com/spec/v1", parsed.Version);
    }

    [Fact]
    public void A_pointer_whose_size_is_not_a_number_previews_without_a_size()
    {
        const string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:abcd\nsize lots\n";

        var parsed = LfsPointerParser.Parse(Encoding.UTF8.GetBytes(pointer));

        Assert.Null(parsed.Size);
        Assert.Equal("sha256:abcd", parsed.Oid);
    }

    [Fact]
    public void Crlf_line_endings_parse_like_lf()
    {
        const string pointer = "version https://git-lfs.github.com/spec/v1\r\noid sha256:abcd\r\nsize 7\r\n";

        var parsed = LfsPointerParser.Parse(Encoding.UTF8.GetBytes(pointer));

        Assert.Equal(7, parsed.Size);
        Assert.Equal("sha256:abcd", parsed.Oid);
    }
}
