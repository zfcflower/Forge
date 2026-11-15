using System.Text;
using Avalonia.Headless;
using Forge.App.Changes;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

/// <summary>
/// The image half of the diff (wave W17a): an image change is the two pictures, so the two sides come from the change set's own
/// refs - the base blob and the working-tree file (or the newer tree of a historical change set) - and they decode through the
/// file preview's decode path. The repository cases run against real temp git repositories.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ImageDiffTests
{
    private const string Session = "image-diff-session";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A file that only starts with the PNG signature: the decode path is stubbed, so the tag is all that matters.</summary>
    private static byte[] Png(string tag) => [.. PngSignature, .. Encoding.UTF8.GetBytes(tag)];

    [Fact]
    public async Task The_two_sides_are_the_base_blob_and_the_working_tree_file()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = GitTestSandbox.Create();
            var work = sandbox.Clone("session");
            WriteBytes(work, "photo.png", Png("before"));
            Commit(work, "add the screenshot");
            WriteBytes(work, "photo.png", Png("after"));

            var changes = await OpenAsync(work);
            var file = Assert.Single(changes.Files);
            Assert.True(file.IsImage, "a .png change is an image change");

            var sides = await file.ReadImageSidesAsync();

            Assert.Equal(Png("before"), sides.Before);
            Assert.Equal(Png("after"), sides.After);
        });
    }

    [Fact]
    public async Task An_added_image_has_no_before_side_and_a_deleted_one_no_after()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = GitTestSandbox.Create();
            var work = sandbox.Clone("session");
            WriteBytes(work, "gone.png", Png("gone"));
            Commit(work, "add the screenshot that will be deleted");
            File.Delete(Path.Combine(work, "gone.png"));
            WriteBytes(work, "fresh.png", Png("fresh"));

            var changes = await OpenAsync(work);

            var deleted = changes.Files.Single(file => file.Path == "gone.png");
            var deletedSides = await deleted.ReadImageSidesAsync();
            Assert.Equal(Png("gone"), deletedSides.Before);
            Assert.Null(deletedSides.After);

            var added = changes.Files.Single(file => file.Path == "fresh.png");
            var addedSides = await added.ReadImageSidesAsync();
            Assert.Null(addedSides.Before);
            Assert.Equal(Png("fresh"), addedSides.After);
        });
    }

    /// <summary>
    /// A change set whose new side is not the working tree (here: "all changes", base against HEAD) reads the new side from that
    /// ref. Today's file on disk is a third version and must not show up as either picture.
    /// </summary>
    [Fact]
    public async Task A_change_set_that_ends_before_the_working_tree_reads_the_new_side_from_its_ref()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = GitTestSandbox.Create();
            var work = sandbox.CloneTracked("session");
            WriteBytes(work, "photo.png", Png("base"));
            Commit(work, "add the screenshot");
            GitTestShell.RunChecked(work, "push", "-q", "origin", "main");
            GitTestShell.RunChecked(work, "switch", "-q", "-c", "feature/screens");
            WriteBytes(work, "photo.png", Png("branch"));
            Commit(work, "change the screenshot");
            // Nothing of this version is in either ref of the change set below.
            WriteBytes(work, "photo.png", Png("working-now"));

            var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();
            changes.SelectedBranch = Assert.Single(changes.LocalBranches, static branch => branch.Name == "main");
            changes.AllChangesCommand.Execute(null);
            // The command starts the load itself; awaiting one more pass guarantees the rows and the refs belong together.
            await changes.LoadAsync(CancellationToken.None);
            Assert.True(changes.IsAllChanges, "the branch scope never took effect");

            var file = Assert.Single(changes.Files);
            var sides = await file.ReadImageSidesAsync();

            Assert.Equal(Png("base"), sides.Before);
            Assert.Equal(Png("branch"), sides.After);
        });
    }

    [Fact]
    public async Task A_non_image_change_has_no_image_sides()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = GitTestSandbox.Create();
            var work = sandbox.Clone("session");
            sandbox.CommitFile(work, "notes.txt", "one\n", "add notes");
            File.WriteAllText(Path.Combine(work, "notes.txt"), "two\n");

            var changes = await OpenAsync(work);
            var file = Assert.Single(changes.Files);

            Assert.False(file.IsImage);
            Assert.Same(ImageDiffSides.None, await file.ReadImageSidesAsync());
        });
    }

    /// <summary>
    /// The side-by-side view model decodes both sides through the preview's decode path, once, and reports a side without bytes
    /// as absent instead of drawing an empty frame.
    /// </summary>
    [Fact]
    public async Task Both_sides_decode_through_the_preview_decoder_and_a_missing_side_stays_absent()
    {
        await SharedUi.RunAsync(async () =>
        {
            var decoded = new List<byte[]>();
            var file = new FakeFile(DiffFixtures.Replace("photo.png"))
            {
                Sides = new ImageDiffSides(Png("before"), Png("after")),
            };
            var viewModel = new ImageDiffViewModel(
                file,
                data =>
                {
                    decoded.Add(data);
                    return null;
                });

            await viewModel.LoadAsync();

            // The stub returns null, so both sides read as "decode failed" - the decoder saw each side's exact bytes, and
            // the view shows "nothing here" instead of an empty box with a 0×0 caption (wave W17a review fix).
            Assert.Equal([Png("before"), Png("after")], decoded);
            Assert.False(viewModel.HasBefore);
            Assert.False(viewModel.HasAfter);
            Assert.False(viewModel.IsLoading);

            var missing = new FakeFile(DiffFixtures.Replace("photo.png"))
            {
                Sides = new ImageDiffSides(null, Png("after")),
            };
            var missingViewModel = new ImageDiffViewModel(missing, _ => null);
            await missingViewModel.LoadAsync();

            Assert.False(missingViewModel.HasBefore);
            Assert.False(missingViewModel.HasAfter);
        });
    }

    // ---------------------------------------------------------------- helpers

    private static void WriteBytes(string repository, string relativePath, byte[] data) =>
        File.WriteAllBytes(Path.Combine(repository, relativePath), data);

    /// <summary>Commits everything in <paramref name="repository"/> (binary files included) through the shared git runner.</summary>
    private static void Commit(string repository, string message)
    {
        GitTestShell.RunChecked(repository, "add", "-A");
        GitTestShell.RunChecked(repository, "commit", "-q", "-m", message);
    }

    private static async Task<ChangesViewModel> OpenAsync(string work)
    {
        var changes = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
        changes.Attach(new PanelContext(Session, work, null));
        await changes.LoadAsync(CancellationToken.None);
        return changes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }
}