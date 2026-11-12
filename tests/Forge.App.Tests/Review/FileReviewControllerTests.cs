using Avalonia.Headless;
using Forge.App.Review;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Review;

[Collection("forge-app-ui")]
public sealed class FileReviewControllerTests
{
    [Fact]
    public Task Stored_drafts_of_this_file_and_session_reload_attached_in_line_order() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.SeedAsync("A", "a.cs", 3, 3, "third");
        await rig.SeedAsync("A", "a.cs", 1, 2, "first");
        await rig.SeedAsync("B", "a.cs", 2, 2, "other session");
        await rig.SeedAsync("A", "b.cs", 2, 2, "other file");
        await rig.AttachAsync("A");
        var drafts = rig.Controller.Drafts;
        Assert.Equal(["first", "third"], drafts.Select(static d => d.Body));
        Assert.All(drafts, static d => Assert.True(d.IsAttached));
        Assert.All(drafts, static d => Assert.False(d.IsSent));
        Assert.Equal([(1, 2), (3, 3)], drafts.Select(static d => (d.StoredStart, d.StoredEnd)));
        Assert.True(drafts[0].TryGetLines(out var start, out var end));
        Assert.Equal((1, 2), (start, end));
    });

    [Fact]
    public Task Saving_persists_the_exact_file_range_follows_later_edits_and_rejects_an_empty_body() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var draft = rig.Controller.BeginDraft(2, 3)!;
        Assert.False(await rig.Controller.SaveAsync(draft, "   "));
        Assert.Equal(ReviewStrings.BodyEmpty, rig.Controller.Status.Key);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        Assert.True(await rig.Controller.SaveAsync(draft, "  explain  "));
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        Assert.Equal("explain", stored.Body);
        Assert.Equal((2, 3), (stored.NewLineStart, stored.NewLineEnd));
        Assert.Same(draft, Assert.Single(rig.Controller.Drafts));
        Assert.Equal(ReviewStrings.Saved, rig.Controller.Status.Key);
        rig.Controller.Document!.Insert(0, "new\n");
        Assert.True(await rig.Controller.SaveAsync(draft, "explain"));
        var moved = (await rig.Store.GetAsync(stored.Id))!;
        Assert.Equal((3, 4), (moved.NewLineStart, moved.NewLineEnd));
    });

    [Fact]
    public Task Two_drafts_on_the_same_line_persist_separately_in_a_stable_order() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var first = rig.Controller.BeginDraft(2, 2)!;
        var second = rig.Controller.BeginDraft(2, 2)!;
        Assert.True(await rig.Controller.SaveAsync(first, "one"));
        Assert.True(await rig.Controller.SaveAsync(second, "two"));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs")).Count);
        Assert.Equal(["one", "two"], rig.Controller.Drafts.Select(static d => d.Body));
    });

    [Fact]
    public Task Different_owner_scopes_keep_drafts_isolated() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(1, 1)!, "from A"));
        await rig.AttachAsync("B");
        Assert.Empty(rig.Controller.Drafts);
        Assert.True(await rig.Controller.SaveAsync(rig.Controller.BeginDraft(2, 2)!, "from B"));
        var a = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        var b = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "B", "a.cs"));
        Assert.Equal(("from A", "A"), (a.Body, a.SessionId));
        Assert.Equal(("from B", "B"), (b.Body, b.SessionId));
        await rig.AttachAsync("A");
        Assert.Equal(["from A"], rig.Controller.Drafts.Select(static d => d.Body));
    });

    [Fact]
    public Task Single_send_saves_the_file_first_queues_then_marks_and_batch_send_queues_once() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var single = rig.Controller.BeginDraft(2, 2)!;
        rig.Controller.Document!.Insert(rig.Controller.Document.TextLength, "five\n");
        Assert.True(rig.Model.IsDirty);
        DateTimeOffset? sentAtWhenQueued = DateTimeOffset.MinValue;
        string? diskWhenQueued = null;
        rig.OnSend = () =>
        {
            sentAtWhenQueued = rig.SentAtOf(single.Id);
            diskWhenQueued = File.ReadAllText(rig.FilePath);
        };
        Assert.True(await rig.Controller.SendAsync(single, "please rename"));
        var queued = Assert.Single(rig.Sent);
        Assert.Equal("A", queued.Session);
        Assert.Contains("please rename", queued.Text, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\nthree\nfour\nfive\n", diskWhenQueued);
        Assert.False(rig.Model.IsDirty);
        Assert.Null(sentAtWhenQueued);
        Assert.NotNull((await rig.Store.GetAsync(single.Id))!.SentAt);
        Assert.Empty(rig.Controller.Drafts);
        var one = rig.Controller.BeginDraft(1, 1)!;
        var two = rig.Controller.BeginDraft(3, 3)!;
        Assert.True(await rig.Controller.SaveAsync(one, "batch one"));
        Assert.True(await rig.Controller.SaveAsync(two, "batch two"));
        rig.OnSend = null;
        Assert.True(await rig.Controller.SendFileAsync());
        Assert.Equal(2, rig.Sent.Count);
        Assert.Contains("batch one", rig.Sent[1].Text, StringComparison.Ordinal);
        Assert.Contains("batch two", rig.Sent[1].Text, StringComparison.Ordinal);
        Assert.NotNull((await rig.Store.GetAsync(one.Id))!.SentAt);
        Assert.NotNull((await rig.Store.GetAsync(two.Id))!.SentAt);
        Assert.Empty(rig.Controller.Drafts);
    });

    [Fact]
    public Task A_session_that_cannot_accept_the_message_leaves_the_draft_unsent_and_unmarked() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        rig.Accept = false;
        var draft = rig.Controller.BeginDraft(2, 2)!;
        Assert.False(await rig.Controller.SendAsync(draft, "hello"));
        Assert.Equal(ReviewStrings.SessionGone, rig.Controller.Status.Key);
        Assert.Null((await rig.Store.GetAsync(draft.Id))!.SentAt);
        Assert.False(draft.IsSent);
        Assert.Same(draft, Assert.Single(rig.Controller.Drafts));
    });

    [Fact]
    public Task An_operation_started_while_a_send_is_in_flight_is_refused_without_touching_the_store() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var first = rig.Controller.BeginDraft(1, 1)!;
        var second = rig.Controller.BeginDraft(2, 2)!;
        Task<bool>? refused = null;
        var busyDuringSend = false;
        string? statusDuringSend = null;
        rig.OnSend = () =>
        {
            refused = rig.Controller.SaveAsync(second, "second");
            busyDuringSend = rig.Controller.IsBusy;
            statusDuringSend = rig.Controller.Status.Key;
        };
        Assert.True(await rig.Controller.SendAsync(first, "first"));
        Assert.True(busyDuringSend);
        Assert.Equal(ReviewStrings.Busy, statusDuringSend);
        Assert.NotNull(refused);
        Assert.True(refused.IsCompleted);
        Assert.False(await refused);
        Assert.Equal(0, second.Id);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        Assert.Single(rig.Sent);
        Assert.False(rig.Controller.IsBusy);
    });

    [Fact]
    public Task A_failed_file_save_keeps_the_draft_stored_but_never_queues_or_marks_it() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var draft = rig.Controller.BeginDraft(1, 1)!;
        rig.Controller.Document!.Insert(rig.Controller.Document.TextLength, "five\n");
        File.SetAttributes(rig.FilePath, FileAttributes.ReadOnly);
        Assert.False(await rig.Controller.SendAsync(draft, "needs save"));
        Assert.Equal(ReviewStrings.SaveFailed, rig.Controller.Status.Key);
        Assert.True(rig.Model.HasSaveError);
        Assert.Empty(rig.Sent);
        var stored = Assert.Single(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        Assert.Null(stored.SentAt);
    });

    [Fact]
    public Task A_draft_whose_lines_were_deleted_cannot_be_saved_or_sent() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var draft = rig.Controller.BeginDraft(2, 2)!;
        var doc = rig.Controller.Document!;
        var line = doc.GetLineByNumber(2);
        doc.Remove(line.Offset, line.TotalLength);
        Assert.True(draft.IsLost(doc));
        Assert.False(await rig.Controller.SaveAsync(draft, "gone"));
        Assert.Equal(ReviewStrings.Lost, rig.Controller.Status.Key);
        Assert.False(await rig.Controller.SendAsync(draft, "gone"));
        Assert.Equal(ReviewStrings.Lost, rig.Controller.Status.Key);
        Assert.Empty(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"));
        Assert.Empty(rig.Sent);
    });

    [Fact]
    public Task A_detached_stored_draft_blocks_batch_send_but_can_be_deleted() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        var id = await rig.SeedDetachedAsync("A", 2, "code that is no longer here");
        await rig.AttachAsync("A");
        var draft = Assert.Single(rig.Controller.Drafts);
        Assert.False(draft.IsAttached);
        Assert.False(await rig.Controller.SendFileAsync());
        Assert.Equal(ReviewStrings.Lost, rig.Controller.Status.Key);
        Assert.Empty(rig.Sent);
        Assert.True(await rig.Controller.DeleteAsync(draft));
        Assert.Empty(rig.Controller.Drafts);
        Assert.Null(await rig.Store.GetAsync(id));
    });

    [Fact]
    public Task A_message_withdrawn_after_it_was_marked_sent_restores_the_draft() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var draft = rig.Controller.BeginDraft(2, 2)!;
        Assert.True(await rig.Controller.SendAsync(draft, "hello"));
        Assert.NotNull((await rig.Store.GetAsync(draft.Id))!.SentAt);
        Assert.NotNull(rig.Withdraw);
        rig.Withdraw();
        await rig.UntilAsync(() => !rig.Controller.IsBusy && rig.Controller.Drafts.Count == 1);
        Assert.Null((await rig.Store.GetAsync(draft.Id))!.SentAt);
        Assert.False(rig.Controller.Drafts[0].IsSent);
        Assert.Equal(ReviewStrings.Restored, rig.Controller.Status.Key);
    });

    [Fact]
    public Task A_message_withdrawn_before_the_mark_is_restored_once_it_is_marked() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var draft = rig.Controller.BeginDraft(2, 2)!;
        rig.OnSend = () => rig.Withdraw!();
        Assert.True(await rig.Controller.SendAsync(draft, "hello"));
        await rig.UntilAsync(() => !rig.Controller.IsBusy && rig.Controller.Drafts.Count == 1);
        Assert.Null((await rig.Store.GetAsync(draft.Id))!.SentAt);
        Assert.False(rig.Controller.Drafts[0].IsSent);
        Assert.Equal(ReviewStrings.Restored, rig.Controller.Status.Key);
    });

    [Fact]
    public Task A_withdrawn_batch_restores_every_draft_and_can_be_sent_again() => Ui(async () =>
    {
        await using var rig = await Rig.OpenAsync();
        await rig.AttachAsync("A");
        var one = rig.Controller.BeginDraft(1, 1)!;
        var two = rig.Controller.BeginDraft(3, 3)!;
        Assert.True(await rig.Controller.SaveAsync(one, "one"));
        Assert.True(await rig.Controller.SaveAsync(two, "two"));
        Assert.True(await rig.Controller.SendFileAsync());
        rig.Withdraw!();
        await rig.UntilAsync(() => !rig.Controller.IsBusy && rig.Controller.Drafts.Count == 2);
        Assert.All(await rig.Store.GetDraftsAsync(rig.Root, "A", "a.cs"), static d => Assert.Null(d.SentAt));
        Assert.True(await rig.Controller.SendFileAsync());
        Assert.Equal(2, rig.Sent.Count);
    });

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(string root, string filePath, FileDocumentViewModel model)
        {
            Root = root;
            FilePath = filePath;
            Model = model;
            Store = new ReviewStore(Path.Combine(root, ".review-data"));
        }
        public string Root { get; }
        public string FilePath { get; }
        public FileDocumentViewModel Model { get; }
        public ReviewStore Store { get; }
        public List<(string Session, string Text)> Sent { get; } = [];
        public Action? OnSend { get; set; }
        public Action? Withdraw { get; private set; }
        public bool Accept { get; set; } = true;
        public FileReviewController Controller => Model.ReviewController;
        public static async Task<Rig> OpenAsync(string text = "one\ntwo\nthree\nfour\n")
        {
            var root = Directory.CreateTempSubdirectory("forge-review-").FullName;
            var file = Path.Combine(root, "a.cs");
            await File.WriteAllTextAsync(file, text);
            var model = await TestDocuments.LoadAsync(file);
            Assert.False(model.HasError, model.Error);
            return new Rig(root, file, model);
        }
        public async Task AttachAsync(string session)
        {
            Model.ConfigureReview(
                new ReviewServices(Store, (target, text, withdrawn) =>
                {
                    Withdraw = withdrawn;
                    OnSend?.Invoke();
                    if (Accept)
                    {
                        Sent.Add((target, text));
                    }
                    return Accept;
                }, static () => { }),
                new ReviewOwner(Root, session));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Controller.IsBusy)
            {
                Assert.True(DateTime.UtcNow < deadline, "The review controller never became idle.");
                await Task.Delay(10);
            }
        }
        public async Task SeedAsync(string session, string file, int start, int end, string body)
        {
            var lines = ReviewTextAnchors.RangeLines(Model.EditorMirror!.Document, start, end);
            await Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = Root, SessionId = session, FilePath = file, NewLineStart = start, NewLineEnd = end,
                Body = body, Anchor = ReviewTextAnchors.Create(file, lines, start),
            });
        }
        public async Task<long> SeedDetachedAsync(string session, int line, string missingText)
        {
            var added = await Store.AddAsync(new NewReviewComment
            {
                WorkspacePath = Root, SessionId = session, FilePath = "a.cs", NewLineStart = line, NewLineEnd = line,
                Body = "detached", Anchor = ReviewTextAnchors.Create("a.cs", [missingText], line),
            });
            return added.Id;
        }
        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "The condition was never met.");
                await Task.Delay(10);
            }
        }

        public DateTimeOffset? SentAtOf(long id) =>
            Task.Run(() => Store.GetAsync(id)).GetAwaiter().GetResult()?.SentAt;
        public async ValueTask DisposeAsync()
        {
            Model.DisposeReview();
            await Model.ReleaseDocumentAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(FilePath))
            {
                File.SetAttributes(FilePath, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
