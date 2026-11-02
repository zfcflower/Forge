using Xunit;

namespace Forge.Workspace.Tests.Review;

public sealed class ReviewDispatcherTests
{
    private const string Workspace = "/work/dispatch";
    private const string Session = "s-dispatch";
    private const string Base = "0123456789abcdef0123456789abcdef01234567";

    private const string Diff = """
        diff --git a/src/a.cs b/src/a.cs
        --- a/src/a.cs
        +++ b/src/a.cs
        @@ -1,4 +1,4 @@
         using System;
        -var old = 1;
        +var value = 2;
         // keep
         class A {}
        diff --git a/src/b.cs b/src/b.cs
        --- a/src/b.cs
        +++ b/src/b.cs
        @@ -1 +1 @@
        -x
        +y
        """;

    private static IReadOnlyList<FileDiff> Files => UnifiedDiffParser.Parse(Diff);

    private static NewReviewComment Draft(string file, int line, string body, DiffAnchor? anchor = null, int? end = null) => new()
    {
        WorkspacePath = Workspace,
        SessionId = Session,
        FilePath = file,
        NewLineStart = line,
        NewLineEnd = end,
        Body = body,
        BaseCommit = Base,
        Anchor = anchor,
    };

    [Fact]
    public async Task Build_single_renders_one_comment_with_quote_and_base_without_marking_sent()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = DiffAnchor.Create(Files, "src/a.cs", 2);
        var comment = await store.AddAsync(Draft("src/a.cs", 2, "Rename this.", anchor));
        var dispatcher = new ReviewDispatcher(store);

        var dispatch = await dispatcher.BuildSingleAsync(comment.Id, Files);

        Assert.NotNull(dispatch);
        Assert.Equal([comment.Id], dispatch.CommentIds);
        Assert.StartsWith("## Review comment\n", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("### `src/a.cs:2`", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains($"base: `{Base}`", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("```diff\n", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("+ var value = 2;", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("- var old = 1;", dispatch.Text, StringComparison.Ordinal);
        Assert.EndsWith("Rename this.\n", dispatch.Text, StringComparison.Ordinal);
        Assert.Equal(ReviewCommentStatus.Draft, (await store.GetAsync(comment.Id))!.Status);
        Assert.DoesNotContain("1. ", dispatch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_single_unknown_id_returns_null()
    {
        using var sandbox = new TempDirectory();

        Assert.Null(await new ReviewDispatcher(new ReviewStore(sandbox.Path)).BuildSingleAsync(777, Files));
    }

    [Fact]
    public async Task Send_single_marks_only_that_comment_as_sent()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var first = await store.AddAsync(Draft("src/a.cs", 2, "first"));
        var second = await store.AddAsync(Draft("src/b.cs", 1, "second"));
        var dispatcher = new ReviewDispatcher(store);

        var dispatch = await dispatcher.SendSingleAsync(first.Id);

        Assert.Equal([first.Id], dispatch!.CommentIds);
        Assert.Equal(ReviewCommentStatus.Sent, (await store.GetAsync(first.Id))!.Status);
        Assert.Equal(ReviewCommentStatus.Draft, (await store.GetAsync(second.Id))!.Status);
        Assert.Null(await dispatcher.SendSingleAsync(999));
    }

    [Fact]
    public async Task Single_comment_without_anchor_or_file_has_no_quote_block()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var comment = await store.AddAsync(Draft("src/a.cs", 2, "plain"));

        var withoutDiff = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id);
        var withDiffButNoAnchor = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id, Files);

        Assert.DoesNotContain("```", withoutDiff!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("```", withDiffButNoAnchor!.Text, StringComparison.Ordinal);
        Assert.Contains("plain", withDiffButNoAnchor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Range_comment_renders_line_span_in_heading()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var comment = await store.AddAsync(Draft("src/a.cs", 2, "block", end: 3));

        var dispatch = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id);

        Assert.Contains("`src/a.cs:2-3`", dispatch!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_all_orders_by_file_then_line_then_id_and_numbers_the_entries()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var bLate = await store.AddAsync(Draft("src/b.cs", 1, "b-one"));
        var aLater = await store.AddAsync(Draft("src/a.cs", 5, "a-five"));
        var aEarly = await store.AddAsync(Draft("src/a.cs", 2, "a-two"));
        var dispatcher = new ReviewDispatcher(store);

        var dispatch = await dispatcher.BuildAllAsync(Workspace, Session);

        Assert.NotNull(dispatch);
        Assert.Equal([aEarly.Id, aLater.Id, bLate.Id], dispatch.CommentIds);
        Assert.StartsWith("## Review comments (3)\n", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("### 1. `src/a.cs:2`", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("### 2. `src/a.cs:5`", dispatch.Text, StringComparison.Ordinal);
        Assert.Contains("### 3. `src/b.cs:1`", dispatch.Text, StringComparison.Ordinal);
        Assert.True(dispatch.Text.IndexOf("a-two", StringComparison.Ordinal) < dispatch.Text.IndexOf("a-five", StringComparison.Ordinal));
        Assert.True(dispatch.Text.IndexOf("a-five", StringComparison.Ordinal) < dispatch.Text.IndexOf("b-one", StringComparison.Ordinal));
        Assert.Equal(ReviewCommentStatus.Draft, (await store.GetAsync(aEarly.Id))!.Status);
    }

    [Fact]
    public async Task Build_all_returns_null_when_there_are_no_drafts()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var sent = await store.AddAsync(Draft("src/a.cs", 2, "already"));
        await store.MarkSentAsync([sent.Id]);

        Assert.Null(await new ReviewDispatcher(store).BuildAllAsync(Workspace, Session));
        Assert.Null(await new ReviewDispatcher(store).SendAllAsync(Workspace, Session));
    }

    [Fact]
    public async Task Send_all_marks_every_draft_in_scope_and_ignores_other_sessions()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var one = await store.AddAsync(Draft("src/a.cs", 2, "one"));
        var two = await store.AddAsync(Draft("src/b.cs", 1, "two"));
        var outsider = await store.AddAsync(new NewReviewComment
        {
            WorkspacePath = Workspace,
            SessionId = "other-session",
            FilePath = "src/a.cs",
            NewLineStart = 2,
            Body = "elsewhere",
        });
        var dispatcher = new ReviewDispatcher(store);

        var dispatch = await dispatcher.SendAllAsync(Workspace, Session, Files);

        Assert.Equal([one.Id, two.Id], dispatch!.CommentIds);
        Assert.Equal(ReviewCommentStatus.Sent, (await store.GetAsync(one.Id))!.Status);
        Assert.Equal(ReviewCommentStatus.Sent, (await store.GetAsync(two.Id))!.Status);
        Assert.Equal(ReviewCommentStatus.Draft, (await store.GetAsync(outsider.Id))!.Status);
        Assert.Null(await dispatcher.BuildAllAsync(Workspace, Session));
    }

    [Fact]
    public async Task Stale_anchor_adds_a_shifted_lines_warning_below_the_quote()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        var anchor = DiffAnchor.Create(Files, "src/a.cs", 2);
        var comment = await store.AddAsync(Draft("src/a.cs", 2, "watch out", anchor));
        const string changed = """
            diff --git a/src/a.cs b/src/a.cs
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1,4 +1,4 @@
             using System;
            -var old = 1;
            +var value = 99;
             // keep
             class A {}
            """;

        var dispatch = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id, UnifiedDiffParser.Parse(changed));

        Assert.Contains("> Lines may have shifted since this comment was written.", dispatch!.Text, StringComparison.Ordinal);
        Assert.Contains("+ var value = 99;", dispatch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quoted_text_containing_triple_backticks_is_fenced_with_a_longer_fence()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        const string fenced = """
            diff --git a/doc.md b/doc.md
            --- a/doc.md
            +++ b/doc.md
            @@ -1 +1,3 @@
            -plain
            +```csharp
            +var x = 1;
            +```
            """;
        var files = UnifiedDiffParser.Parse(fenced);
        var anchor = DiffAnchor.Create(files, "doc.md", 1, 3);
        var comment = await store.AddAsync(Draft("doc.md", 1, "fenced body", anchor, end: 3));

        var dispatch = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id, files, contextLines: 0);

        var lines = dispatch!.Text.Split('\n');
        var opening = Array.FindIndex(lines, static line => line.StartsWith("````", StringComparison.Ordinal) && line.EndsWith("diff", StringComparison.Ordinal));
        Assert.True(opening >= 0, dispatch.Text);
        var closing = Array.FindIndex(lines, opening + 1, static line => line == "````");
        Assert.True(closing > opening, dispatch.Text);
        var quoted = lines[(opening + 1)..closing];
        Assert.Contains("+ ```csharp", quoted);
        Assert.Contains("+ ```", quoted);
        Assert.Contains("fenced body", dispatch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fence_grows_past_the_longest_backtick_run_in_the_quote()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        const string fenced = """
            diff --git a/q.md b/q.md
            --- a/q.md
            +++ b/q.md
            @@ -1 +1 @@
            -a
            +````five
            """;
        var files = UnifiedDiffParser.Parse(fenced);
        var anchor = DiffAnchor.Create(files, "q.md", 1);
        var comment = await store.AddAsync(Draft("q.md", 1, "long run", anchor));

        var dispatch = await new ReviewDispatcher(store).BuildSingleAsync(comment.Id, files, contextLines: 0);

        Assert.Contains("`````diff\n", dispatch!.Text, StringComparison.Ordinal);
        Assert.Contains("\n`````\n", dispatch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_all_without_diff_omits_quotes_but_keeps_bodies()
    {
        using var sandbox = new TempDirectory();
        var store = new ReviewStore(sandbox.Path);
        await store.AddAsync(Draft("src/a.cs", 2, "body only", DiffAnchor.Create(Files, "src/a.cs", 2)));

        var dispatch = await new ReviewDispatcher(store).BuildAllAsync(Workspace, Session);

        Assert.DoesNotContain("```", dispatch!.Text, StringComparison.Ordinal);
        Assert.Contains("body only", dispatch.Text, StringComparison.Ordinal);
        Assert.EndsWith("\n", dispatch.Text, StringComparison.Ordinal);
        Assert.False(dispatch.Text.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_rejects_null_store()
    {
        Assert.Throws<ArgumentNullException>(() => new ReviewDispatcher(null!));
    }
}
