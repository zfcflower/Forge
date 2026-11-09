using AvaloniaEdit.Document;
using Forge.App.Changes;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

[Collection("forge-app-ui")]
public sealed class DiffReviewControllerTests
{
    // Document lines of Replace("a.cs"): 1 header, 2 fold(1-6), 3-5 context 7-9, 6 bar, 7 removed old10, 8 added new10,
    // 9-11 context 11-13, 12 fold. Replace("b.cs") follows from line 13 on.
    private const int Header = 1;
    private const int Context7 = 3;
    private const int Context8 = 4;
    private const int Bar = 6;
    private const int Removed10 = 7;
    private const int Added10 = 8;
    private const int Context11 = 9;
    private const int SecondFileContext7 = 15;

    [Fact]
    public Task Only_lines_that_carry_a_new_side_line_of_one_file_can_be_commented_on() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        Assert.True(rig.Controller.CanCommentOn(Context7, Context8));
        Assert.True(rig.Controller.CanCommentOn(Added10, Added10));
        Assert.True(rig.Controller.CanCommentOn(Context7, Context11));
        Assert.False(rig.Controller.CanCommentOn(Header, Header));
        Assert.False(rig.Controller.CanCommentOn(Bar, Bar));
        Assert.False(rig.Controller.CanCommentOn(Removed10, Removed10));
        Assert.False(rig.Controller.CanCommentOn(Context11, SecondFileContext7));
        Assert.Null(rig.Controller.BeginDraft(Removed10, Removed10));
        Assert.Null(rig.Controller.BeginDraft(Header, Header));
        Assert.NotNull(rig.Controller.BeginDraft(Added10, Added10));
    });

    [Fact]
    public Task Without_a_review_context_nothing_can_be_composed() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(attach: false);
        Assert.False(rig.Controller.CanCompose);
        Assert.False(rig.Controller.CanCommentOn(Context7, Context7));
        Assert.Null(rig.Controller.BeginDraft(Context7, Context7));
        Assert.True(rig.Controller.Status.IsEmpty);
    });

    [Fact]
    public Task Saving_stores_the_new_side_range_the_file_path_and_the_base_commit() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Added10)!;
        Assert.True(await rig.Controller.SaveAsync(draft, "  look here  "));
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
        Assert.Equal("look here", stored.Body);
        Assert.Equal((7, 10), (stored.NewLineStart, stored.NewLineEnd));
        Assert.Equal("base123", stored.BaseCommit);
        Assert.Same(draft, Assert.Single(rig.Controller.Drafts));
        Assert.Equal(ReviewStrings.Saved, rig.Controller.Status.Key);
        Assert.Equal(1, rig.Changed);
    });

    [Fact]
    public Task A_comment_on_the_second_file_belongs_to_that_file() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(SecondFileContext7, SecondFileContext7)!;
        Assert.True(await rig.Controller.SaveAsync(draft, "second"));
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/b.cs"));
        Assert.Equal((7, 7), (stored.NewLineStart, stored.NewLineEnd));
    });

    [Fact]
    public Task An_empty_body_is_rejected_and_nothing_is_stored() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        Assert.False(await rig.Controller.SaveAsync(draft, "   "));
        Assert.Equal(ReviewStrings.BodyEmpty, rig.Controller.Status.Key);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
        Assert.Empty(rig.Controller.Drafts);
    });

    [Fact]
    public Task Saving_again_updates_the_same_comment_instead_of_adding_one() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        Assert.True(await rig.Controller.SaveAsync(draft, "first"));
        Assert.True(await rig.Controller.SaveAsync(draft, "second"));
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
        Assert.Equal("second", stored.Body);
        Assert.Single(rig.Controller.Drafts);
    });

    [Fact]
    public Task Stored_drafts_of_this_session_and_the_shown_files_reload_in_new_file_coordinates() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.SeedAsync("A", rig.Files[0], 10, 10, "on the change");
        await rig.SeedAsync("A", rig.Files[1], 8, 8, "second file");
        await rig.SeedAsync("B", rig.Files[0], 8, 8, "other session");
        await rig.SeedAsync("A", new FakeFile(DiffFixtures.Replace("src/other.cs")), 8, 8, "not shown");
        await rig.ReloadAsync();
        Assert.Equal(["on the change", "second file"], rig.Controller.Drafts.Select(static d => d.Body).Order());
        var first = rig.Controller.Drafts.Single(static d => d.Body == "on the change");
        Assert.True(first.IsAttached);
        Assert.True(first.TryGetLines(out var start, out var end));
        Assert.Equal((Added10, Added10), (start, end));
        var second = rig.Controller.Drafts.Single(static d => d.Body == "second file");
        Assert.True(second.TryGetLines(out start, out end));
        Assert.Equal((SecondFileContext7 + 1, SecondFileContext7 + 1), (start, end));
    });

    [Fact]
    public Task A_draft_in_a_folded_or_collapsed_region_stays_and_shows_on_the_row_that_hides_it() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.SeedAsync("A", rig.Files[0], 3, 3, "hidden in the fold");
        await rig.SeedAsync("A", rig.Files[0], 10, 10, "visible");
        await rig.ReloadAsync();
        var hidden = rig.Controller.Drafts.Single(static d => d.Body == "hidden in the fold");
        Assert.True(hidden.TryGetLines(out var start, out _));
        Assert.Equal(2, start);

        rig.Files[0].IsCollapsed = true;
        await rig.RebuildAsync();
        Assert.Equal(2, rig.Controller.Drafts.Count);
        Assert.All(rig.Controller.Drafts, static d => Assert.True(d.IsAttached));
        Assert.All(rig.Controller.Drafts, d =>
        {
            Assert.True(d.TryGetLines(out var line, out _));
            Assert.Equal(Header, line);
        });
        Assert.Equal(2, (await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs")).Count);

        rig.Files[0].IsCollapsed = false;
        await rig.RebuildAsync();
        var back = rig.Controller.Drafts.Single(static d => d.Body == "visible");
        Assert.True(back.TryGetLines(out start, out _));
        Assert.Equal(Added10, start);
    });

    [Fact]
    public Task Expanding_a_fold_moves_the_draft_from_the_fold_row_to_its_own_line() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.SeedAsync("A", rig.Files[0], 3, 3, "in the fold");
        await rig.ReloadAsync();
        var draft = Assert.Single(rig.Controller.Drafts);
        Assert.True(draft.TryGetLines(out var folded, out _));
        Assert.Equal(2, folded);
        await rig.RebuildAsync(expand: (0, 1));
        draft = Assert.Single(rig.Controller.Drafts);
        Assert.True(draft.TryGetLines(out var shown, out _));
        Assert.Equal(4, shown);
    });

    [Fact]
    public Task Deleting_removes_the_stored_comment_and_the_draft() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        Assert.True(await rig.Controller.SaveAsync(draft, "gone soon"));
        Assert.True(await rig.Controller.DeleteAsync(draft));
        Assert.Empty(rig.Controller.Drafts);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task Discarding_an_unsaved_draft_leaves_no_trace() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        rig.Controller.Discard(draft);
        Assert.True(draft.IsReleased);
        Assert.Empty(rig.Controller.Drafts);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task Sending_one_comment_saves_it_queues_the_message_with_path_and_quote_and_marks_it_sent() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Added10, Added10)!;
        Assert.True(await rig.Controller.SendAsync(draft, "rename this"));
        var sent = Assert.Single(rig.Sent);
        Assert.Equal("A", sent.Session);
        Assert.Contains("src/a.cs", sent.Text);
        Assert.Contains("rename this", sent.Text);
        Assert.Contains("new10", sent.Text);
        Assert.True(draft.IsSent);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task Send_all_sends_one_message_per_file_and_nothing_when_there_is_nothing_to_send() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        Assert.False(await rig.Controller.SendFileAsync());
        Assert.Equal(ReviewStrings.NoDrafts, rig.Controller.Status.Key);
        Assert.Empty(rig.Sent);

        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(Context7, Context7)!, "one"));
        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(Added10, Added10)!, "two"));
        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(SecondFileContext7, SecondFileContext7)!, "three"));
        Assert.True(await rig.Controller.SendFileAsync());
        Assert.Equal(2, rig.Sent.Count);
        Assert.Contains(rig.Sent, static s => s.Text.Contains("src/a.cs") && s.Text.Contains("one") && s.Text.Contains("two"));
        Assert.Contains(rig.Sent, static s => s.Text.Contains("src/b.cs") && s.Text.Contains("three"));
    });

    [Fact]
    public Task A_refused_send_keeps_the_draft_unsent_and_reports_the_gone_session() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        rig.Accept = false;
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        Assert.False(await rig.Controller.SendAsync(draft, "hello"));
        Assert.False(draft.IsSent);
        Assert.Equal(ReviewStrings.SessionGone, rig.Controller.Status.Key);
        Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task A_draft_whose_document_was_replaced_is_released_and_cannot_be_saved() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var draft = rig.Controller.BeginDraft(Context7, Context7)!;
        await rig.RebuildAsync();
        Assert.False(await rig.Controller.SaveAsync(draft, "late"));
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task Reconfiguring_with_no_context_clears_the_drafts_and_disables_commenting() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(Context7, Context7)!, "kept"));
        rig.Controller.Configure(null);
        await rig.IdleAsync();
        Assert.Empty(rig.Controller.Drafts);
        Assert.False(rig.Controller.CanCompose);
        Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
    });

    [Fact]
    public Task In_split_mode_comments_use_the_new_side_document() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync(split: true);
        // New side of Replace: 1 header, 2 fold, 3-5 context 7-9, 6 bar, 7 added new10 (paired with the removed row on the left).
        Assert.False(rig.Controller.CanCommentOn(6, 6));
        Assert.True(rig.Controller.CanCommentOn(7, 7));
        var draft = rig.Controller.BeginDraft(7, 7)!;
        Assert.True(await rig.Controller.SaveAsync(draft, "new side"));
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "src/a.cs"));
        Assert.Equal((10, 10), (stored.NewLineStart, stored.NewLineEnd));
    });

    [Fact]
    public Task Disposing_stops_the_controller_from_composing() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        rig.Controller.Dispose();
        Assert.False(rig.Controller.CanCompose);
        Assert.Null(rig.Controller.BeginDraft(Context7, Context7));
    });

    private static async Task Ui(Func<Task> body)
    {
        var session = SharedUi.Session;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly bool split;
        private TextDocument document = new();

        private Rig(bool split)
        {
            this.split = split;
            Root = Directory.CreateTempSubdirectory("forge-diff-review-").FullName;
            Store = new ReviewStore(Path.Combine(Root, ".review-data"));
            Files = [DiffFixtures.File("src/a.cs"), DiffFixtures.File("src/b.cs")];
        }

        public string Root { get; }

        public ReviewStore Store { get; }

        public List<FakeFile> Files { get; }

        public DiffReviewController Controller { get; } = new();

        public List<(string Session, string Text)> Sent { get; } = [];

        public bool Accept { get; set; } = true;

        public int Changed { get; private set; }

        public static async Task<Rig> OpenAsync(bool attach = true, bool split = false)
        {
            var rig = new Rig(split);
            await rig.RebuildAsync();
            if (attach)
            {
                rig.Controller.Configure(rig.Context());
                await rig.IdleAsync();
            }

            return rig;
        }

        public DiffReviewContext Context() =>
            new(new ReviewServices(Store, (session, text, _) =>
            {
                if (Accept)
                {
                    Sent.Add((session, text));
                }

                return Accept;
            }, () => Changed++), new ReviewOwner(Root, "A"), "base123");

        public async Task RebuildAsync(params (int File, int Key)[] expand)
        {
            var specs = Files.Select((file, index) =>
                new DiffFileSpec(file.Diff, file.IsCollapsed, false, true,
                    expand.Where(e => e.File == index).Select(static e => e.Key).ToHashSet())).ToList();
            var built = DiffDocumentBuilder.Build(specs, split);
            var model = built.Secondary ?? built.Primary;
            document = new TextDocument(model.Text);
            Controller.Rebind(document, model.Map, Files);
            await IdleAsync();
        }

        public Task ReloadAsync() => RebuildAsync();

        public async Task SeedAsync(string session, FakeFile file, int start, int end, string body)
        {
            await Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = Root,
                SessionId = session,
                FilePath = file.Path,
                NewLineStart = start,
                NewLineEnd = end,
                Body = body,
                Anchor = DiffAnchor.Create(file.Diff, start, end - start + 1),
            });
        }

        public async Task IdleAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                await Task.Delay(10);
                Assert.True(DateTime.UtcNow < deadline, "The diff review controller never became idle.");
            }
            while (Controller.IsBusy);
        }

        public ValueTask DisposeAsync()
        {
            Controller.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
