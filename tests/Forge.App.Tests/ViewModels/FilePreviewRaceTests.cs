using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Two loads of one document competing: the loser gives up its decoded bitmap and the winner survives until the document
/// closes. A real bitmap is what makes the loser observable at all — a cancelled load never reaches
/// <see cref="FileDocumentViewModel.Preview"/>, so the disposed bitmap is the evidence that it was released — which is why
/// this suite runs on the headless session and <see cref="FilePreviewDocumentTests"/> does not.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FilePreviewRaceTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x00];

    private readonly string root = TestTempFiles.NewDirectory("forge-preview-race-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    /// <summary>
    /// The overlapping case: the image decode is in flight when a second load takes the document over, so the first load is
    /// cancelled and its preview is released by the load that built it, not by the document.
    /// </summary>
    [Fact]
    public async Task A_preview_that_loses_the_race_disposes_its_bitmap_and_the_winner_is_not_released()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var slow = Path.Combine(root, "slow.png");
                await File.WriteAllBytesAsync(slow, PngBytes);
                var fast = TestTempFiles.WriteFile(root, "fast.csv", "c,d\n3,4\n");

                // Created here, on the session's thread; the decode below only hands it out.
                var bitmap = NewBitmap();
                using var gate = new ManualResetEventSlim(false);
                using var entered = new ManualResetEventSlim(false);
                var model = FileDocumentViewModel.Create(imageDecoder: Decoder(bitmap, gate, entered));

                // Start the image load and wait until its decode is in flight, then let a second load take the document
                // over: the first one finishes later and must release the preview nobody shows.
                var losing = model.LoadAsync(slow, CancellationToken.None);
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), "the image decode never started");
                await model.LoadAsync(fast, CancellationToken.None);
                var winner = Assert.IsType<TableFilePreviewViewModel>(model.Preview);
                gate.Set();
                await losing;

                Assert.Same(winner, model.Preview);
                Assert.Equal(0, winner.ReleaseCount);

                // The loser's own release: it held the decoded bitmap, and that bitmap is gone.
                Assert.Throws<ObjectDisposedException>(() => bitmap.PixelSize);

                await model.ReleaseDocumentAsync();
                Assert.Equal(1, winner.ReleaseCount);
            });
    }

    /// <summary>The superseded case: a preview that did reach the screen is released exactly once, by the load after it.</summary>
    [Fact]
    public async Task A_preview_the_next_load_replaces_is_released_exactly_once()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var image = Path.Combine(root, "shot.png");
                await File.WriteAllBytesAsync(image, PngBytes);
                var table = TestTempFiles.WriteFile(root, "table.csv", "c,d\n3,4\n");

                var bitmap = NewBitmap();
                var model = FileDocumentViewModel.Create(imageDecoder: Decoder(bitmap));

                await model.LoadAsync(image, CancellationToken.None);
                var loser = Assert.IsType<ImageFilePreviewViewModel>(model.Preview);
                Assert.Same(bitmap, loser.Image);
                Assert.Equal(0, loser.ReleaseCount);

                await model.LoadAsync(table, CancellationToken.None);
                var winner = Assert.IsType<TableFilePreviewViewModel>(model.Preview);

                Assert.Equal(1, loser.ReleaseCount);
                Assert.Throws<ObjectDisposedException>(() => bitmap.PixelSize);

                // Closing the document releases what it shows, never what it already let go of.
                await model.ReleaseDocumentAsync();
                Assert.Equal(1, loser.ReleaseCount);
                Assert.Equal(1, winner.ReleaseCount);
            });
    }

    private static Bitmap NewBitmap() =>
        new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>
    /// Hands out the one bitmap the test keeps a reference to, so its disposal is observable; with a <paramref name="gate"/>
    /// the decode stays in flight until the test lets it finish, so two loads really overlap.
    /// </summary>
    private static Func<byte[], Bitmap?> Decoder(Bitmap bitmap, ManualResetEventSlim? gate = null, ManualResetEventSlim? entered = null) =>
        _ =>
        {
            entered?.Set();
            gate?.Wait(TimeSpan.FromSeconds(30));
            return bitmap;
        };
}
