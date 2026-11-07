using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Xunit;

namespace Forge.App.Tests;

[Collection("forge-app-ui")]
public sealed class MainWindowStartupTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 1, 16, 16)]
    [InlineData(0, 0, 1366, 728, 1, 16, 16)]
    [InlineData(0, 40, 1920, 1040, 1.25, 16, 16)]
    [InlineData(0, 0, 1366, 728, 1.5, 16, 16)]
    [InlineData(0, 0, 1920, 1040, 2, 16, 16)]
    [InlineData(-1920, -200, 1920, 1040, 1.5, 16, 16)]
    [InlineData(1920, 48, 1280, 672, 1.25, 0, 0)]
    [InlineData(0, 0, 800, 480, 1, 0, 0)]
    public async Task Startup_fits_and_centres_the_entire_frame_in_the_working_area(
        int x, int y, int width, int height, double scaling, double frameWidth, double frameHeight)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                var window = new MainWindow();
                try
                {
                    window.Width = 1440;
                    window.Height = 900;
                    window.MinWidth = 900;
                    window.MinHeight = 560;
                    var area = new PixelRect(x, y, width, height);
                    window.ApplyStartupPlacement(area, scaling, new Size(frameWidth, frameHeight));

                    var outerWidth = (window.Width + frameWidth) * scaling;
                    var outerHeight = (window.Height + frameHeight) * scaling;
                    Assert.InRange(window.Position.X, area.X, area.Right);
                    Assert.InRange(window.Position.Y, area.Y, area.Bottom);
                    Assert.True(window.Position.X + outerWidth <= area.Right);
                    Assert.True(window.Position.Y + outerHeight <= area.Bottom);
                    Assert.InRange(Math.Abs(window.Position.X + (outerWidth / 2) - (area.X + (area.Width / 2d))), 0, 0.5);
                    Assert.InRange(Math.Abs(window.Position.Y + (outerHeight / 2) - (area.Y + (area.Height / 2d))), 0, 0.5);
                    Assert.Equal(Math.Min(1440, (width / scaling) - 16 - frameWidth), window.Width);
                    Assert.Equal(Math.Min(900, (height / scaling) - 16 - frameHeight), window.Height);
                    Assert.Equal(Math.Min(900, (width / scaling) - 16 - frameWidth), window.MinWidth);
                    Assert.Equal(Math.Min(560, (height / scaling) - 16 - frameHeight), window.MinHeight);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Opening_the_shell_applies_the_logo_and_screen_placement()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                var window = new MainWindow();
                var dialog = new Window { Width = 300, Height = 200 };
                try
                {
                    Assert.Equal("Forge", window.Title);
                    Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
                    var logo = window.FindControl<Image>("AppLogo");
                    Assert.NotNull(logo);
                    Assert.NotNull(logo.Source);
                    Assert.Equal(18, logo.Width);
                    Assert.Equal(18, logo.Height);
                    window.Show();
                    dialog.Show(window);
                    Assert.NotNull(window.Icon);
                    Assert.NotNull(dialog.Icon);
                    var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
                    Assert.NotNull(screen);
                    var area = screen.WorkingArea;
                    var frame = window.FrameSize ?? window.ClientSize;
                    var width = frame.Width * window.DesktopScaling;
                    var height = frame.Height * window.DesktopScaling;
                    Assert.True(window.Position.X >= area.X && window.Position.X + width <= area.Right);
                    Assert.True(window.Position.Y >= area.Y && window.Position.Y + height <= area.Bottom);
                    Assert.InRange(Math.Abs(window.Position.X + (width / 2) - (area.X + (area.Width / 2d))), 0, 0.5);
                    Assert.InRange(Math.Abs(window.Position.Y + (height / 2) - (area.Y + (area.Height / 2d))), 0, 0.5);
                }
                finally
                {
                    dialog.Close();
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Embedded_windows_icon_contains_all_shell_and_high_DPI_sizes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                using var stream = AssetLoader.Open(new Uri("avares://Forge.App/Assets/forge.ico"));
                using var reader = new BinaryReader(stream);
                Assert.Equal(0, reader.ReadUInt16());
                Assert.Equal(1, reader.ReadUInt16());
                int[] sizes = [16, 24, 32, 48, 64, 128, 256];
                Assert.Equal(sizes.Length, reader.ReadUInt16());
                foreach (var size in sizes)
                {
                    var dimension = size == 256 ? 0 : size;
                    Assert.Equal(dimension, reader.ReadByte());
                    Assert.Equal(dimension, reader.ReadByte());
                    Assert.Equal(0, reader.ReadByte());
                    Assert.Equal(0, reader.ReadByte());
                    Assert.Equal(1, reader.ReadUInt16());
                    Assert.Equal(32, reader.ReadUInt16());
                    var length = reader.ReadUInt32();
                    var offset = reader.ReadUInt32();
                    Assert.True(length > 0);
                    Assert.True(offset + length <= stream.Length);
                    var nextEntry = stream.Position;
                    stream.Position = offset;
                    using var bitmap = SkiaSharp.SKBitmap.Decode(reader.ReadBytes(checked((int)length)));
                    Assert.NotNull(bitmap);
                    Assert.Equal(size, bitmap.Width);
                    Assert.Equal(size, bitmap.Height);
                    Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
                    Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 0);
                    stream.Position = nextEntry;
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Embedded_logo_preserves_transparent_background_and_visible_artwork()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                using var stream = AssetLoader.Open(new Uri("avares://Forge.App/Assets/forge.png"));
                using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
                Assert.NotNull(bitmap);
                Assert.Equal(256, bitmap.Width);
                Assert.Equal(256, bitmap.Height);
                Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
                Assert.Equal(0, bitmap.GetPixel(255, 0).Alpha);
                Assert.Equal(0, bitmap.GetPixel(0, 255).Alpha);
                Assert.Equal(0, bitmap.GetPixel(255, 255).Alpha);
                Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 0);
            },
            CancellationToken.None);
    }
}
