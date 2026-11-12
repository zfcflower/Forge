using System.Diagnostics;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Changes;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

/// <summary>
/// The review half of <see cref="ChangesViewModel"/> against real temp git repositories and a real <see cref="ReviewStore"/>:
/// persisted viewed/kept marks, bulk marking, navigation, reverts and their failures, and mode/layout switches.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ChangesReviewFlowTests
{
    private const string Session = "review-flow-session";
    private const string OtherSession = "review-flow-other-session";

    // ---------------------------------------------------------------- 1. viewed marks

    [Fact]
    public async Task Viewed_mark_persists_per_session_and_drops_when_the_file_content_changes()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var first = await rig.OpenAsync();
            var a = first.File("a.txt");
            Assert.False(a.IsViewed);

            a.IsViewed = true;
            Assert.True(a.IsCollapsed, "viewing a file collapses its body");
            var fingerprint = a.Fingerprint;
            await WaitUntilAsync(
                async () => (await rig.MarksAsync(Session)).Any(mark =>
                    mark.Kind == ReviewMarkKind.Viewed && mark.FilePath == "a.txt" && mark.ItemHash == fingerprint),
                "viewed mark stored");

            using var second = await rig.OpenAsync();
            await WaitUntilAsync(() => second.File("a.txt").IsViewed, "viewed mark reloaded");
            Assert.False(second.File("b.txt").IsViewed);
            Assert.Equal(1, second.ViewedCount);
            Assert.True(second.File("a.txt").IsCollapsed);

            // Another session has no marks for the same workspace.
            using var other = await rig.OpenAsync(OtherSession);
            await Task.Delay(300);
            Assert.Equal(0, other.ViewedCount);

            // The content changes: the stored fingerprint no longer matches, so the file is unviewed again.
            File.AppendAllText(Path.Combine(rig.Dir, "a.txt"), "appended\n");
            await second.LoadAsync(CancellationToken.None);
            await Task.Delay(300);
            Assert.NotEqual(fingerprint, second.File("a.txt").Fingerprint);
            Assert.False(second.File("a.txt").IsViewed);
            Assert.Equal(0, second.ViewedCount);
            Assert.Contains(await rig.MarksAsync(Session), static mark => mark.Kind == ReviewMarkKind.Viewed);
        });
    }

    [Fact]
    public async Task Unviewing_a_file_clears_the_stored_mark_and_expands_it_again()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var a = vm.File("a.txt");
            a.IsViewed = true;
            await WaitUntilAsync(async () => (await rig.MarksAsync(Session)).Count == 1, "mark stored");

            a.IsViewed = false;
            Assert.False(a.IsCollapsed);
            Assert.Equal(0, vm.ViewedCount);
            await WaitUntilAsync(async () => (await rig.MarksAsync(Session)).Count == 0, "mark cleared");

            using var again = await rig.OpenAsync();
            await Task.Delay(300);
            Assert.Equal(0, again.ViewedCount);
        });
    }

    [Fact]
    public async Task Review_marks_read_must_not_overwrite_a_toggle_made_while_it_is_in_flight()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync(settleMarks: false);

            // The user acts right after the rows appear, before the marks read of that load has been applied.
            vm.File("a.txt").IsViewed = true;
            vm.File("a.txt").Blocks[0].KeepCommand.Execute(null);
            await Task.Delay(500);

            Assert.True(vm.File("a.txt").IsViewed);
            Assert.True(vm.File("a.txt").Blocks[0].IsKept);
        });
    }

    // ---------------------------------------------------------------- 2. kept marks

    [Fact]
    public async Task Kept_mark_persists_per_block_and_is_invalidated_only_by_that_blocks_change()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var first = await rig.OpenAsync();
            var a = first.File("a.txt");
            Assert.Equal(2, a.Blocks.Count);
            Assert.Equal(0, a.KeptCount);
            Assert.Equal(ChangesReviewStrings.KeptSummary(0, 2), a.KeptSummary);

            var keptHash = a.Blocks[0].Block.Hash;
            a.Blocks[0].KeepCommand.Execute(null);
            Assert.True(a.Blocks[0].IsKept);
            Assert.Equal(1, a.KeptCount);
            Assert.Equal(ChangesReviewStrings.KeptSummary(1, 2), a.KeptSummary);
            await WaitUntilAsync(
                async () => (await rig.MarksAsync(Session)).Any(mark =>
                    mark.Kind == ReviewMarkKind.Kept && mark.FilePath == "a.txt" && mark.ItemHash == keptHash),
                "kept mark stored");

            using var second = await rig.OpenAsync();
            await WaitUntilAsync(() => second.File("a.txt").Blocks[0].IsKept, "kept mark reloaded");
            Assert.False(second.File("a.txt").Blocks[1].IsKept);
            Assert.Equal(1, second.File("a.txt").KeptCount);

            // Editing the other block keeps the first one kept (new rows, same block hash).
            rig.WriteLines("a.txt", (3, "EDITED-3"), (25, "EDITED-AGAIN-25"));
            await second.LoadAsync(CancellationToken.None);
            await WaitUntilAsync(() => second.File("a.txt").Blocks[0].IsKept, "first block still kept");
            Assert.False(second.File("a.txt").Blocks[1].IsKept);
            Assert.Equal(1, second.File("a.txt").KeptCount);

            // Editing the kept block itself invalidates its mark.
            rig.WriteLines("a.txt", (3, "EDITED-AGAIN-3"), (25, "EDITED-AGAIN-25"));
            await second.LoadAsync(CancellationToken.None);
            await Task.Delay(300);
            Assert.Equal(0, second.File("a.txt").KeptCount);
            Assert.Equal(ChangesReviewStrings.KeptSummary(0, 2), second.File("a.txt").KeptSummary);

            // A different session never sees the mark.
            using var other = await rig.OpenAsync(OtherSession);
            await Task.Delay(300);
            Assert.Equal(0, other.File("a.txt").KeptCount);
        });
    }

    [Fact]
    public async Task Toggling_keep_twice_removes_the_stored_mark()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var block = vm.File("a.txt").Blocks[0];

            block.KeepCommand.Execute(null);
            await WaitUntilAsync(async () => (await rig.MarksAsync(Session)).Count == 1, "kept stored");
            block.KeepCommand.Execute(null);
            Assert.False(block.IsKept);
            Assert.Equal(0, vm.File("a.txt").KeptCount);
            await WaitUntilAsync(async () => (await rig.MarksAsync(Session)).Count == 0, "kept cleared");
        });
    }

    [Fact]
    public async Task Only_hunks_are_read_up_front_and_loading_context_keeps_the_kept_marks()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var a = vm.File("a.txt");

            // Edits at lines 3 and 25 of 30: two 3-line-context hunks, the stretch between them is not loaded.
            Assert.Equal(2, a.Diff.Hunks.Count);
            Assert.False(a.HasFullContext);
            Assert.True(a.CanLoadContext);
            var fingerprint = a.Fingerprint;
            a.Blocks[1].KeepCommand.Execute(null);

            Assert.True(await a.LoadFullContextAsync());
            Assert.True(a.HasFullContext);
            Assert.False(a.CanLoadContext);
            var hunk = Assert.Single(a.Diff.Hunks);
            Assert.Equal(30, hunk.NewCount);
            Assert.Equal(fingerprint, a.Fingerprint);
            Assert.Equal(2, a.Blocks.Count);
            Assert.False(a.Blocks[0].IsKept);
            Assert.True(a.Blocks[1].IsKept);
            Assert.Equal(1, a.KeptCount);

            // A file whose changes moved on since the set was read does not take the reread diff.
            using var stale = await rig.OpenAsync();
            rig.WriteLines("b.txt", (2, "EDITED-B2"), (4, "EDITED-B4"));
            Assert.False(await stale.File("b.txt").LoadFullContextAsync());
            Assert.False(stale.File("b.txt").HasFullContext);
        });
    }

    // ---------------------------------------------------------------- 3. mark all viewed

    [Fact]
    public async Task Mark_all_viewed_updates_the_counters_and_disables_the_commands()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var total = vm.Files.Count;
            Assert.True(total >= 3);
            Assert.Equal(0, vm.ReviewedPercent);
            Assert.True(vm.MarkAllViewedCommand.CanExecute(null));
            Assert.True(vm.NextUnviewedCommand.CanExecute(null));

            vm.File("b.txt").IsViewed = true;
            Assert.Equal(1, vm.ViewedCount);
            Assert.Equal(100.0 / total, vm.ReviewedPercent, 6);

            vm.MarkAllViewedCommand.Execute(null);

            Assert.Equal(total, vm.ViewedCount);
            Assert.Equal(ChangesReviewStrings.ViewedSummary(total, total), vm.ViewedText);
            Assert.Equal(ChangesReviewStrings.ReviewedSummary(total, total), vm.ReviewedText);
            Assert.Equal(100.0, vm.ReviewedPercent, 6);
            Assert.All(vm.Files, static file => Assert.True(file.IsViewed && file.IsCollapsed));
            Assert.False(vm.MarkAllViewedCommand.CanExecute(null));
            Assert.False(vm.NextUnviewedCommand.CanExecute(null));
            await WaitUntilAsync(
                async () => (await rig.MarksAsync(Session)).Count(static mark => mark.Kind == ReviewMarkKind.Viewed) == total,
                "all viewed marks stored");

            // Executing a disabled command is a no-op, and un-viewing one file enables both again.
            var selected = vm.SelectedFile;
            vm.NextUnviewedCommand.Execute(null);
            Assert.Same(selected, vm.SelectedFile);
            vm.File("c.txt").IsViewed = false;
            Assert.True(vm.MarkAllViewedCommand.CanExecute(null));
            Assert.True(vm.NextUnviewedCommand.CanExecute(null));
            Assert.Equal(total - 1, vm.ViewedCount);

            using var reopened = await rig.OpenAsync();
            await WaitUntilAsync(() => reopened.ViewedCount == total - 1, "marks reloaded");
        });
    }

    [Fact]
    public async Task A_clean_repository_has_no_progress_and_nothing_to_mark()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync(dirty: false);
            using var vm = await rig.OpenAsync();

            Assert.Empty(vm.Files);
            Assert.False(vm.HasFiles);
            Assert.Equal(0, vm.ViewedCount);
            Assert.Equal(0, vm.ReviewedPercent);
            Assert.False(vm.MarkAllViewedCommand.CanExecute(null));
            Assert.False(vm.NextUnviewedCommand.CanExecute(null));
            vm.NextUnviewedCommand.Execute(null);
            vm.MarkAllViewedCommand.Execute(null);
            Assert.Null(vm.SelectedFile);
            Assert.Empty(vm.VisibleFiles);
            Assert.Empty(await rig.MarksAsync(Session));
        });
    }

    // ---------------------------------------------------------------- 4. next unviewed

    [Fact]
    public async Task Next_unviewed_skips_viewed_files_wraps_around_and_expands_the_target()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            Assert.True(vm.Files.Count >= 3);
            var focused = new List<FileDiffViewModel>();
            vm.FocusRequested += focused.Add;

            vm.SelectedFile = vm.Files[0];
            vm.Files[1].IsViewed = true;
            Assert.True(vm.Files[1].IsCollapsed);

            vm.NextUnviewedCommand.Execute(null);
            Assert.Same(vm.Files[2], vm.SelectedFile);
            Assert.Same(vm.Files[2], focused[^1]);

            // From the last file the search wraps to the first unviewed one.
            vm.SelectedFile = vm.Files[^1];
            vm.NextUnviewedCommand.Execute(null);
            Assert.Same(vm.Files[0], vm.SelectedFile);

            // A collapsed unviewed file is expanded when navigation lands on it.
            vm.Files[2].IsCollapsed = true;
            vm.SelectedFile = vm.Files[1];
            vm.NextUnviewedCommand.Execute(null);
            Assert.Same(vm.Files[2], vm.SelectedFile);
            Assert.False(vm.Files[2].IsCollapsed);

            // With the selected file the only unviewed one, the search comes back to it.
            foreach (var file in vm.Files.Where(file => !ReferenceEquals(file, vm.SelectedFile)))
            {
                file.IsViewed = true;
            }

            var only = vm.SelectedFile;
            Assert.True(vm.NextUnviewedCommand.CanExecute(null));
            vm.NextUnviewedCommand.Execute(null);
            Assert.Same(only, vm.SelectedFile);
        });
    }

    // ---------------------------------------------------------------- 5. reverts

    [Fact]
    public async Task Revert_block_removes_exactly_that_change_and_reloads_the_view()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var a = vm.File("a.txt");
            Assert.Equal(2, a.Blocks.Count);
            Assert.True(a.Blocks[0].RevertCommand.CanExecute(null));

            a.Blocks[0].RevertCommand.Execute(null);

            await WaitUntilAsync(() => vm.TryFile("a.txt") is { Blocks.Count: 1 }, "view reloaded after block revert");
            Assert.Equal(rig.Text("a.txt", (25, "EDITED-25")), rig.Read("a.txt"));
            Assert.Equal("EDITED-25", vm.File("a.txt").Blocks[0].Block.NewLines.Single());
            Assert.False(vm.HasMessage);
            Assert.Equal(rig.OriginalText("b.txt", (2, "EDITED-B2")), rig.Read("b.txt"));

            // Reverting the last block makes the file clean: it leaves the change set.
            vm.File("a.txt").Blocks[0].RevertCommand.Execute(null);
            await WaitUntilAsync(() => vm.TryFile("a.txt") is null, "file gone after its last block was reverted");
            Assert.Equal(rig.OriginalText("a.txt"), rig.Read("a.txt"));
        });
    }

    [Fact]
    public async Task Revert_file_asks_first_and_leaves_the_file_alone_when_declined()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            var asked = new List<(string Title, string Message, string Confirm, string Cancel)>();
            using var vm = await rig.OpenAsync(confirm: (title, message, confirm, cancel) =>
            {
                asked.Add((title, message, confirm, cancel));
                return Task.FromResult(false);
            });
            var before = rig.Read("a.txt");

            vm.File("a.txt").RevertFileCommand.Execute(null);
            await WaitUntilAsync(() => asked.Count == 1, "confirmation shown");
            await Task.Delay(300);

            Assert.Equal(Loc.Instance[ChangesReviewStrings.RevertFileTitle], asked[0].Title);
            Assert.Equal(ChangesReviewStrings.RevertFileMessage("a.txt"), asked[0].Message);
            Assert.Equal(Loc.Instance[ChangesReviewStrings.RevertFileConfirm], asked[0].Confirm);
            Assert.Equal(before, rig.Read("a.txt"));
            Assert.NotNull(vm.TryFile("a.txt"));
            Assert.False(vm.HasMessage);
        });
    }

    [Fact]
    public async Task Revert_file_restores_a_modified_file_and_deletes_an_added_one_after_confirmation()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync(confirm: static (_, _, _, _) => Task.FromResult(true));
            Assert.NotNull(vm.TryFile("n.txt"));
            Assert.Equal(FileDiffStatus.Added, vm.File("n.txt").Diff.Status);

            vm.File("n.txt").RevertFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.TryFile("n.txt") is null, "added file reverted");
            Assert.False(File.Exists(Path.Combine(rig.Dir, "n.txt")));

            vm.File("a.txt").RevertFileCommand.Execute(null);
            await WaitUntilAsync(() => vm.TryFile("a.txt") is null, "modified file reverted");
            Assert.Equal(rig.OriginalText("a.txt"), rig.Read("a.txt"));

            // Untouched siblings keep their edits.
            Assert.Equal(rig.OriginalText("b.txt", (2, "EDITED-B2")), rig.Read("b.txt"));
            Assert.NotNull(vm.TryFile("b.txt"));
        });
    }

    [Fact]
    public async Task Revert_file_without_a_configured_confirmation_reverts_directly()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync(confirm: null);

            vm.File("c.txt").RevertFileCommand.Execute(null);

            await WaitUntilAsync(() => vm.TryFile("c.txt") is null, "reverted without prompt");
            Assert.Equal(rig.OriginalText("c.txt"), rig.Read("c.txt"));
        });
    }

    // ---------------------------------------------------------------- 6. revert failures

    [Fact]
    public async Task Revert_block_reports_a_changed_file_and_does_not_touch_it()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var block = vm.File("a.txt").Blocks[0];
            var modified = rig.Read("a.txt");

            // The file changes after the diff was read: the block no longer matches anywhere.
            File.WriteAllText(Path.Combine(rig.Dir, "a.txt"), "completely different\ncontent\n");
            block.RevertCommand.Execute(null);

            await WaitUntilAsync(() => vm.HasMessage, "failure message");
            Assert.Equal(Loc.Instance[ChangesReviewStrings.RevertFileChanged], vm.Message);
            Assert.Equal("completely different\ncontent\n", rig.Read("a.txt"));
            Assert.NotNull(vm.TryFile("a.txt"));

            // The guard flag was released: once the file matches again the same block reverts.
            File.WriteAllText(Path.Combine(rig.Dir, "a.txt"), modified);
            block.RevertCommand.Execute(null);
            await WaitUntilAsync(() => vm.TryFile("a.txt") is { Blocks.Count: 1 }, "revert after recovery");
            Assert.False(vm.HasMessage);
        });
    }

    [Fact]
    public async Task Revert_file_reports_a_missing_file()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync(confirm: static (_, _, _, _) => Task.FromResult(true));

            File.Delete(Path.Combine(rig.Dir, "b.txt"));
            vm.File("b.txt").RevertFileCommand.Execute(null);

            await WaitUntilAsync(() => vm.HasMessage, "failure message");
            Assert.Equal(Loc.Instance[ChangesReviewStrings.RevertMissing], vm.Message);
            Assert.False(File.Exists(Path.Combine(rig.Dir, "b.txt")));
        });
    }

    // ---------------------------------------------------------------- 8. mode and layout

    [Fact]
    public async Task Mode_and_layout_commands_flip_the_flags_and_notify()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.True(vm.IsInline);
            Assert.False(vm.IsSplit);
            Assert.True(vm.IsStacked);
            Assert.False(vm.IsSingle);

            vm.SplitModeCommand.Execute(null);
            Assert.True(vm.IsSplit);
            Assert.False(vm.IsInline);
            Assert.Equal(DiffViewMode.Split, vm.ViewMode);
            Assert.Contains(nameof(ChangesViewModel.IsSplit), changed);
            Assert.Contains(nameof(ChangesViewModel.IsInline), changed);

            changed.Clear();
            vm.SplitModeCommand.Execute(null);
            Assert.Empty(changed);

            vm.InlineModeCommand.Execute(null);
            Assert.True(vm.IsInline);
            Assert.False(vm.IsSplit);

            vm.SingleLayoutCommand.Execute(null);
            Assert.True(vm.IsSingle);
            Assert.False(vm.IsStacked);
            Assert.Equal(DiffLayout.Single, vm.Layout);

            vm.StackedLayoutCommand.Execute(null);
            Assert.True(vm.IsStacked);
            Assert.False(vm.IsSingle);

            Assert.False(vm.WordWrap);
            vm.ToggleWrapCommand.Execute(null);
            Assert.True(vm.WordWrap);
            vm.ToggleWrapCommand.Execute(null);
            Assert.False(vm.WordWrap);
        });
    }

    [Fact]
    public async Task Single_layout_shows_only_the_selected_file_and_stacked_shows_all()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync();
            using var vm = await rig.OpenAsync();
            Assert.True(vm.Files.Count >= 2);
            Assert.Equal(vm.Files.Count, vm.VisibleFiles.Count);

            vm.SelectedFile = vm.Files[0];
            vm.SingleLayoutCommand.Execute(null);
            Assert.Same(vm.Files[0], Assert.Single(vm.VisibleFiles));

            vm.SelectedFile = vm.Files[1];
            Assert.Same(vm.Files[1], Assert.Single(vm.VisibleFiles));

            vm.NextFileCommand.Execute(null);
            Assert.Same(vm.SelectedFile, Assert.Single(vm.VisibleFiles));

            // Scroll sync only applies to the stacked layout.
            var selected = vm.SelectedFile;
            vm.SyncSelectionFromScroll(vm.Files[0]);
            Assert.Same(selected, vm.SelectedFile);

            vm.StackedLayoutCommand.Execute(null);
            Assert.Equal(vm.Files.Count, vm.VisibleFiles.Count);
            vm.SyncSelectionFromScroll(vm.Files[0]);
            Assert.Same(vm.Files[0], vm.SelectedFile);
        });
    }

    [Fact]
    public async Task Single_layout_with_no_changes_shows_nothing()
    {
        await RunAsync(async () =>
        {
            using var rig = await Rig.CreateAsync(dirty: false);
            using var vm = await rig.OpenAsync();

            vm.SingleLayoutCommand.Execute(null);

            Assert.Empty(vm.VisibleFiles);
        });
    }

    // ---------------------------------------------------------------- harness

    private static Task RunAsync(Func<Task> body) => SharedUi.RunAsync(body);

    private static async Task WaitUntilAsync(Func<bool> condition, string what) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

    private sealed class Rig : IDisposable
    {
        private const int LineCount = 30;

        private Rig(GitTestSandbox sandbox, string dir, string storeDir)
        {
            Sandbox = sandbox;
            Dir = dir;
            StoreDir = storeDir;
            Store = new ReviewStore(storeDir);
            Services = new ReviewServices(Store, static (_, _, _) => true, static () => { });
        }

        public GitTestSandbox Sandbox { get; }

        public string Dir { get; }

        public string StoreDir { get; }

        public ReviewStore Store { get; }

        public ReviewServices Services { get; }

        /// <summary>
        /// a.txt (30 lines, lines 3 and 25 edited = two blocks), b.txt (line 2 edited), c.txt (line 1 edited) and an untracked n.txt.
        /// </summary>
        public static async Task<Rig> CreateAsync(bool dirty = true)
        {
            var sandbox = GitTestSandbox.Create();
            try
            {
                var dir = sandbox.CloneTracked("session");
                sandbox.CommitFile(dir, "a.txt", Lines("line", LineCount), "add a");
                sandbox.CommitFile(dir, "b.txt", Lines("b", 5), "add b");
                sandbox.CommitFile(dir, "c.txt", Lines("c", 5), "add c");
                if (dirty)
                {
                    File.WriteAllText(Path.Combine(dir, "a.txt"), Lines("line", LineCount, (3, "EDITED-3"), (25, "EDITED-25")));
                    File.WriteAllText(Path.Combine(dir, "b.txt"), Lines("b", 5, (2, "EDITED-B2")));
                    File.WriteAllText(Path.Combine(dir, "c.txt"), Lines("c", 5, (1, "EDITED-C1")));
                    File.WriteAllText(Path.Combine(dir, "n.txt"), "brand new\nfile\n");
                }

                var storeDir = Path.Combine(sandbox.Root, "review-data");
                await Task.CompletedTask;
                return new Rig(sandbox, dir, storeDir);
            }
            catch
            {
                sandbox.Dispose();
                throw;
            }
        }

        public async Task<ChangesViewModel> OpenAsync(
            string sessionId = Session,
            Func<string, string, string, string, Task<bool>>? confirm = null,
            bool settleMarks = true)
        {
            var vm = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
            vm.ConfigureReview(Services, confirm);
            vm.Attach(new PanelContext(sessionId, Dir, null));
            await vm.LoadAsync(CancellationToken.None);
            if (settleMarks)
            {
                // The marks read starts fire-and-forget after every load and has no completion signal; let it land so that
                // it cannot overwrite what the test does next (see the in-flight toggle test).
                await Task.Delay(300);
            }

            return vm;
        }

        public Task<IReadOnlyList<ReviewMark>> MarksAsync(string sessionId) => Store.GetMarksAsync(Dir, sessionId);

        public string Read(string path) => File.ReadAllText(Path.Combine(Dir, path));

        /// <summary>The committed text of a.txt/b.txt/c.txt with optional line replacements.</summary>
        public string OriginalText(string path, params (int Line, string Text)[] edits) => path switch
        {
            "a.txt" => Lines("line", LineCount, edits),
            "b.txt" => Lines("b", 5, edits),
            "c.txt" => Lines("c", 5, edits),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

        /// <summary>a.txt with only the given edits applied (the original with those lines replaced).</summary>
        public string Text(string path, params (int Line, string Text)[] edits) => OriginalText(path, edits);

        /// <summary>Rewrites a.txt as the original plus the given line edits.</summary>
        public void WriteLines(string path, params (int Line, string Text)[] edits) =>
            File.WriteAllText(Path.Combine(Dir, path), OriginalText(path, edits));

        public void Dispose()
        {
            try
            {
                Sandbox.Dispose();
            }
            catch (IOException)
            {
                // A pooled sqlite handle may still hold the store file; the temp dir is cleaned by the OS later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Lines(string prefix, int count, params (int Line, string Text)[] edits)
        {
            var text = new System.Text.StringBuilder();
            for (var n = 1; n <= count; n++)
            {
                var replaced = edits.Where(edit => edit.Line == n).Select(static edit => edit.Text).FirstOrDefault();
                text.Append(replaced ?? prefix + n).Append('\n');
            }

            return text.ToString();
        }
    }

}

internal static class ChangesViewModelTestExtensions
{
    public static FileDiffViewModel File(this ChangesViewModel vm, string path) =>
        vm.TryFile(path) ?? throw new InvalidOperationException("No changed file " + path + " in " + string.Join(", ", vm.Files.Select(static f => f.Path)));

    public static FileDiffViewModel? TryFile(this ChangesViewModel vm, string path) =>
        vm.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));
}
