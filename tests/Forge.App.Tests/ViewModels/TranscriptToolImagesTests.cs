using Avalonia.Media.Imaging;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the picture half of <see cref="ToolRowViewModel"/> (implemented in <c>Transcript.ToolImages.cs</c>): a
/// finished result's images become <see cref="ToolImageViewModel"/> items with their stored data untouched, a
/// text-only result stays image-free, and the thumbnail decodes lazily — never before the first read and exactly once
/// across repeated reads.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TranscriptToolImagesTests
{
    private const string ScreenshotData = "aGVsbG8tc2NyZWVuc2hvdC1ieXRlcw==";

    [Fact]
    public void A_finished_result_with_an_image_exposes_it_with_its_data_unchanged()
    {
        var row = new ToolRowViewModel("screenshot", "{}");
        row.Complete(new ToolResult([new TextBlock("saved to shots/1.png"), new ImageBlock("image/png", ScreenshotData)]));

        var image = Assert.Single(row.Images);
        Assert.IsType<ToolImageViewModel>(image);
        Assert.Equal("image/png", image.Block.MediaType);
        Assert.Equal(ScreenshotData, image.Block.Data);
        Assert.Equal(ContentPlaceholders.DecodedLength(ScreenshotData), image.EncodedBytes);
        Assert.Equal("PNG · 1 KB", image.Caption);

        Assert.True(row.HasImages);
        Assert.True(row.HasResult);
        Assert.Equal("saved to shots/1.png\n[image: image/png, 1 KB]", row.Result);
    }

    [Fact]
    public void A_text_only_result_leaves_the_row_without_images()
    {
        var row = new ToolRowViewModel("bash", "{\"command\":\"ls\"}");
        row.Complete(new ToolResult("two files listed"));

        Assert.Empty(row.Images);
        Assert.False(row.HasImages);
        Assert.Equal("two files listed", row.Result);
    }

    [Fact]
    public async Task A_tool_image_decodes_its_thumbnail_lazily_and_exactly_once()
    {
        var codec = new CountingCodec();
        var previous = ImageThumbnailViewModel.Codec;
        ImageThumbnailViewModel.Codec = codec;
        try
        {
            var image = new ToolImageViewModel(new ImageBlock("image/png", ScreenshotData), maxLongEdge: 300);

            // Before the first read nothing is decoded.
            Assert.Null(image.LoadedThumbnail);
            Assert.Equal(0, codec.ThumbnailCalls);

            // The first read starts the decode off the UI thread with the configured long edge.
            _ = image.Thumbnail;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (codec.ThumbnailCalls == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(1, codec.ThumbnailCalls);
            Assert.Equal(300, codec.LastMaxLongEdge);

            // A second read reuses the in-flight/lazy result; no second decode.
            _ = image.Thumbnail;
            await Task.Delay(50);
            Assert.Equal(1, codec.ThumbnailCalls);
        }
        finally
        {
            ImageThumbnailViewModel.Codec = previous;
        }
    }

    /// <summary>Counts thumbnail requests and records the requested long edge; decode results are null on purpose.</summary>
    private sealed class CountingCodec : IImageCodec
    {
        private readonly Lock gate = new();
        private int thumbnailCalls;
        private int lastMaxLongEdge = -1;

        public int ThumbnailCalls
        {
            get
            {
                lock (gate)
                {
                    return thumbnailCalls;
                }
            }
        }

        public int LastMaxLongEdge
        {
            get
            {
                lock (gate)
                {
                    return lastMaxLongEdge;
                }
            }
        }

        public PreparedImage Prepare(byte[] data, string mediaType) => throw new NotSupportedException();

        public Bitmap? Thumbnail(byte[] data)
        {
            lock (gate)
            {
                thumbnailCalls++;
            }

            return null;
        }

        public Bitmap? Thumbnail(byte[] data, int maxLongEdge)
        {
            lock (gate)
            {
                thumbnailCalls++;
                lastMaxLongEdge = maxLongEdge;
            }

            return null;
        }
    }
}
