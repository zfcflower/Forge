using System.Text;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels.Preview;

public sealed class FilePreviewClassifierTests
{
    [Theory]
    [InlineData("photo.png", "Image")]
    [InlineData("photo.jpg", "Image")]
    [InlineData("photo.jpeg", "Image")]
    [InlineData("photo.gif", "Image")]
    [InlineData("photo.webp", "Image")]
    [InlineData("PHOTO.PNG", "Image")]
    [InlineData("data.csv", "Table")]
    [InlineData("data.tsv", "Table")]
    [InlineData("notes.ipynb", "Notebook")]
    [InlineData("report.pdf", "Pdf")]
    [InlineData("page.html", "Html")]
    [InlineData("page.htm", "Html")]
    [InlineData("readme.md", "Text")]
    [InlineData("source.cs", "Text")]
    [InlineData("archive.tar.gz", "Text")]
    [InlineData("noextension", "Text")]
    [InlineData("", "Text")]
    public void Each_extension_classifies_to_its_preview_kind(string path, string expectedName)
    {
        var expected = Enum.Parse<FilePreviewKind>(expectedName);
        Assert.Equal(expected, FilePreviewClassifier.Classify(path, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void An_LFS_pointer_is_detected_from_the_bytes_before_any_extension()
    {
        var bytes = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 42\n");

        Assert.Equal(FilePreviewKind.Lfs, FilePreviewClassifier.Classify("photo.png", bytes));
    }

    [Fact]
    public void The_LFS_marker_alone_is_enough_to_classify_a_pointer()
    {
        var bytes = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1");

        Assert.Equal(FilePreviewKind.Lfs, FilePreviewClassifier.Classify("model.bin", bytes));
    }

    [Fact]
    public void Bytes_that_only_mention_lfs_without_the_version_marker_stay_text()
    {
        var bytes = Encoding.UTF8.GetBytes("oid sha256:abc\nsize 42\n");

        Assert.Equal(FilePreviewKind.Text, FilePreviewClassifier.Classify("pointer.txt", bytes));
    }

    [Fact]
    public void A_different_lfs_spec_version_does_not_match_the_marker()
    {
        var bytes = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v2");

        Assert.Equal(FilePreviewKind.Text, FilePreviewClassifier.Classify("pointer.txt", bytes));
    }

    [Fact]
    public void Empty_bytes_still_classify_by_extension()
    {
        Assert.Equal(FilePreviewKind.Image, FilePreviewClassifier.Classify("photo.png", ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("a.tsv", '\t')]
    [InlineData("A.TSV", '\t')]
    [InlineData("a.csv", ',')]
    [InlineData("a.txt", ',')]
    public void The_table_separator_follows_the_extension(string path, char expected) =>
        Assert.Equal(expected, FilePreviewClassifier.TableSeparator(path));
}
