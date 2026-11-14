using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Forge.App.Services;
using Forge.App.Views;
using MermaidKinds = Mermaider.Models.DiagramTypes;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// Pins the vector diagram view (wave W11) under a real Skia backend: the picture is laid out at its natural size, shrunk to
/// the width it is given but never upscaled (so a narrow column keeps the aspect ratio), and the custom draw operation really
/// replays the picture into the frame - a backend without the Skia lease would show an empty box.
/// </summary>
public sealed class MermaidDiagramViewTests
{
    private const string Wide =
        "flowchart LR\n  A[Start] --> B{Decision}\n  B -->|yes| C[Done]\n  B -->|no| D[Retry]\n";

    [Fact]
    public async Task A_picture_is_shrunk_to_the_available_width_and_never_upscaled()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(TreeScreenshotAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var picture = await RenderAsync();
                var natural = new Size(picture.Size.Width, picture.Size.Height);

                var wide = new Window
                {
                    Width = (int)natural.Width + 200,
                    Height = 600,
                    Content = Show(picture),
                };
                wide.Show();
                SharedUi.Settle(wide);
                var wideView = Assert.IsType<MermaidDiagramView>(wide.Content);
                Assert.Equal(natural.Width, wideView.Bounds.Width, 3);
                Assert.Equal(natural.Height, wideView.Bounds.Height, 3);

                var narrowWidth = Math.Max(40, natural.Width / 2);
                var narrow = new Window
                {
                    Width = (int)narrowWidth,
                    Height = 600,
                    Content = Show(picture),
                };
                narrow.Show();
                SharedUi.Settle(narrow);
                var narrowView = Assert.IsType<MermaidDiagramView>(narrow.Content);
                Assert.True(narrowView.Bounds.Width < natural.Width);
                Assert.True(narrowView.Bounds.Width <= narrowWidth + 0.5);
                Assert.Equal(natural.Height * (narrowView.Bounds.Width / natural.Width), narrowView.Bounds.Height, 3);

                wide.Close();
                narrow.Close();
                return 0;
            },
            new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
    }

    [Fact]
    public async Task The_diagram_really_paints_into_the_frame()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(TreeScreenshotAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var window = new Window
                {
                    Width = 640,
                    Height = 700,
                    // The padding keeps the top-left corner a pure background sample, so "painted" means "not the host".
                    Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = Show(await RenderAsync()) },
                };
                window.Show();
                SharedUi.Settle(window);

                using var frame = window.CaptureRenderedFrame();
                var bitmap = Assert.IsType<WriteableBitmap>(frame);
                var painted = PaintedPixels(bitmap);
                Assert.True(painted > 1000, $"the diagram painted only {painted} pixels into the frame");

                window.Close();
                return 0;
            },
            new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
    }

    private static MermaidDiagramView Show(MermaidPicture picture)
    {
        var view = new MermaidDiagramView();
        view.SetPicture(picture);
        return view;
    }

    private static async Task<MermaidPicture> RenderAsync()
    {
        var result = await new MermaidRenderService().RenderAsync(
            new MermaidRenderRequest(
                Wide,
                MermaidPalette.Fallback,
                MermaidKinds.Flowchart,
                TimeSpan.FromSeconds(20)),
            CancellationToken.None);
        return Assert.IsType<MermaidPicture>(result.Picture);
    }

    /// <summary>Counts the pixels that differ from the frame's own top-left pixel, i.e. what the diagram drew over its host.</summary>
    private static int PaintedPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var stride = buffer.RowBytes;

        // BGRA: sample the corner of the white host's padding.
        var b0 = bytes[0];
        var g0 = bytes[1];
        var r0 = bytes[2];
        var painted = 0;
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            for (var x = 0; x < buffer.Size.Width; x++)
            {
                var at = (y * stride) + (x * 4);
                var delta = Math.Abs(bytes[at] - b0) + Math.Abs(bytes[at + 1] - g0) + Math.Abs(bytes[at + 2] - r0);
                if (delta > 30)
                {
                    painted++;
                }
            }
        }

        return painted;
    }
}
