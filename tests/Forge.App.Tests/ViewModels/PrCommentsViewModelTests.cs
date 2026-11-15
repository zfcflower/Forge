using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the pull request review-thread panel (wave W16a, L2) without Avalonia: every read and write runs against a scripted
/// <see cref="IPrPlatform"/> and the panel gets the inline <c>post</c> the spec names for tests, so a read finishes inside
/// <c>Start()</c> and a row action inside <c>Pending</c>. Covered here: the three filters and the empty state of each, an
/// outdated thread showing the line it was first placed on, a posted reply and its failure (the typed text survives), the
/// idempotent resolve (a resolved thread is answered, never written twice), the hand-off's three outcomes (ran, queued,
/// no session) and the message it hands over, a failed read that keeps the rows it had, and close/dispose.
/// </summary>
public sealed class PrCommentsViewModelTests
{
    private const int Number = 12;
    private const string WorkDirectory = "work";

    // ------------------------------------------------------------ the filters

    [Fact]
    public async Task The_reading_splits_into_open_resolved_and_outdated_with_the_counts_behind_each()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(
            Open("T1", "src/one.cs", 10),
            Resolved("T2", "src/two.cs", 20, "alice"),
            Outdated("T3", "src/three.cs", originalLine: 42));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        // The open filter is the one the panel opens on, and each thread lands in exactly one bucket.
        Assert.Equal(["T1"], panel.Threads.Select(row => row.Id));
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterOpen, "1"), panel.OpenFilterText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterResolved, "1"), panel.ResolvedFilterText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterOutdated, "1"), panel.OutdatedFilterText);

        panel.ShowResolvedCommand.Execute(null);
        Assert.True(panel.IsResolvedFilter);
        Assert.Equal(["T2"], panel.Threads.Select(row => row.Id));

        panel.ShowOutdatedCommand.Execute(null);
        Assert.True(panel.IsOutdatedFilter);
        Assert.Equal(["T3"], panel.Threads.Select(row => row.Id));
        Assert.False(panel.HasProblem);
    }

    [Fact]
    public async Task An_outdated_thread_shows_the_line_it_was_first_placed_on()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Outdated("T3", "src/three.cs", originalLine: 42));
        using var panel = NewPanel(platform, filter: CommentFilter.Outdated);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        Assert.True(row.IsOutdated);
        Assert.True(row.IsOriginalLocation);
        Assert.Equal("src/three.cs:42(原行)", row.LocationText);

        // The platform takes no reply on an outdated thread, and the strip's own state chip says why it is listed here.
        Assert.False(row.CanReply);
        Assert.Equal(Loc.Instance[PrCommentsStrings.Outdated], row.OutdatedText);
    }

    [Fact]
    public async Task An_empty_reading_is_the_empty_state_of_the_selected_filter()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 1));
        using var panel = NewPanel(platform, filter: CommentFilter.Resolved);
        panel.Start();
        await panel.Watch;

        Assert.Empty(panel.Threads);
        Assert.False(panel.HasThreads);
        Assert.True(panel.IsEmpty);
        Assert.Equal(Loc.Instance[PrCommentsStrings.EmptyResolved], panel.EmptyTitleText);

        panel.ShowOpenCommand.Execute(null);
        Assert.False(panel.IsEmpty);
        Assert.Equal(["T1"], panel.Threads.Select(row => row.Id));
    }

    // ------------------------------------------------------------ the reply

    [Fact]
    public async Task A_reply_is_posted_and_shown_under_its_thread()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10))
        {
            ReplyResult = PrResult<PrReviewComment>.Ok(Comment("C2", "bob", "done")),
        };
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);

        // An empty box is editable but not sendable: the first character has to be possible.
        Assert.True(row.IsReplyEnabled);
        Assert.False(row.CanReply);

        row.ReplyDraft = "  fixed in 4f2a1c9  ";
        Assert.True(row.CanReply);
        row.ReplyCommand.Execute(null);
        await row.Pending;

        // The platform got the trimmed text of this thread, and the row shows the comment it created.
        Assert.Equal(["T1:fixed in 4f2a1c9"], platform.Replies);
        Assert.Equal("done", row.Comments[^1].Body);
        Assert.Equal(string.Empty, row.ReplyDraft);
        Assert.Equal(Loc.Instance[PrCommentsStrings.ReplyDone], row.StatusText);
        Assert.False(row.StatusIsError);
    }

    [Fact]
    public async Task A_failed_reply_keeps_the_typed_text_and_says_why()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10))
        {
            ReplyResult = PrResult<PrReviewComment>.Fail(GitRemoteFailureKind.Network, "gh: could not resolve api.github.com"),
        };
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.ReplyDraft = "the answer";
        row.ReplyCommand.Execute(null);
        await row.Pending;

        // Retrying after a network failure must not cost the typed text.
        Assert.Equal("the answer", row.ReplyDraft);
        Assert.True(row.StatusIsError);
        Assert.Contains("could not resolve api.github.com", row.StatusText, StringComparison.Ordinal);
        Assert.Single(row.Comments);
    }

    [Fact]
    public async Task An_empty_reply_never_reaches_the_platform()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.ReplyDraft = "   ";
        Assert.False(row.CanReply);

        // Whitespace is not an answer: the command refuses to run at all.
        Assert.False(row.ReplyCommand.CanExecute(null));
        Assert.Empty(platform.Replies);
    }

    // ------------------------------------------------------------ resolve

    [Fact]
    public async Task Resolving_an_open_thread_marks_it_resolved()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.ResolveCommand.Execute(null);
        await row.Pending;

        Assert.Equal(["T1"], platform.Resolved);
        Assert.True(row.IsResolved);
        Assert.Equal(Loc.Instance[PrCommentsStrings.ResolveDone], row.StatusText);
        Assert.Equal(CommentFilter.Resolved, row.Filter);

        // The row stays where the user resolved it (its status line is the receipt), and the chips follow the new bucket.
        Assert.Same(row, Assert.Single(panel.Threads));
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterOpen, "0"), panel.OpenFilterText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterResolved, "1"), panel.ResolvedFilterText);
    }

    [Fact]
    public async Task Resolving_a_thread_that_is_already_resolved_writes_nothing()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Resolved("T2", "src/two.cs", 20, "alice"));
        using var panel = NewPanel(platform, filter: CommentFilter.Resolved);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);

        // A stale row (resolved on the platform between two readings) is answered instead of written twice.
        row.ResolveCommand.Execute(null);
        await row.Pending;

        Assert.Empty(platform.Resolved);
        Assert.Equal(Loc.Instance[PrCommentsStrings.ResolveAlready], row.StatusText);
        Assert.False(row.StatusIsError);
        Assert.Equal(Loc.Instance[PrCommentsStrings.Resolved] + " · " + Loc.Instance.Format(PrCommentsStrings.ResolvedBy, "alice"), row.ResolutionText);

        // Reopening has no platform call in this build, so the row says so rather than offering a dead action.
        Assert.False(row.CanReopen);
        Assert.Equal(Loc.Instance[PrCommentsStrings.ReopenUnavailable], row.ReopenTooltip);
    }

    [Fact]
    public async Task A_failed_resolve_leaves_the_thread_open_and_says_why()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10))
        {
            ResolveResult = PrResult<PrThreadResolution>.Fail(GitRemoteFailureKind.Other, "thread was deleted"),
        };
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.ResolveCommand.Execute(null);
        await row.Pending;

        Assert.False(row.IsResolved);
        Assert.True(row.StatusIsError);
        Assert.Contains("thread was deleted", row.StatusText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the hand-off

    [Theory]
    [InlineData("ran")]
    [InlineData("queued")]
    [InlineData("none")]
    public async Task The_hand_off_reports_what_the_session_did_with_the_message(string outcome)
    {
        var handedOff = outcome switch
        {
            "ran" => ChecksHandOff.Ran,
            "queued" => ChecksHandOff.Queued,
            _ => ChecksHandOff.NoSession,
        };
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        var handed = new List<string>();
        using var panel = NewPanel(platform, message =>
        {
            handed.Add(message);
            return Task.FromResult(handedOff);
        });
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.HandOffCommand.Execute(null);
        await row.Pending;

        var message = Assert.Single(handed);
        Assert.Contains("thread_id: T1", message, StringComparison.Ordinal);
        Assert.Contains("src/one.cs:10", message, StringComparison.Ordinal);
        Assert.Contains("pr_comment_reply", message, StringComparison.Ordinal);
        Assert.Equal(
            Loc.Instance[handedOff switch
            {
                ChecksHandOff.Ran => PrCommentsStrings.HandOffRan,
                ChecksHandOff.Queued => PrCommentsStrings.HandOffQueued,
                _ => PrCommentsStrings.HandOffNone,
            }],
            row.StatusText);
    }

    [Fact]
    public async Task A_panel_without_a_session_hides_the_hand_off_instead_of_offering_a_dead_one()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform, handToAgent: null);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        Assert.False(row.HasHandOff);
        Assert.False(row.CanHandOff);
        Assert.False(row.HandOffCommand.CanExecute(null));
    }

    // ------------------------------------------------------------ reading, refreshing, closing

    [Fact]
    public async Task A_failed_read_keeps_the_rows_it_had_and_shows_the_platform_message()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10))
        {
            ThreadsResult = PrResult<IReadOnlyList<PrReviewThread>>.Fail(
                GitRemoteFailureKind.Network,
                "gh: could not resolve api.github.com"),
        };
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        Assert.True(panel.HasProblem);
        Assert.Contains("could not resolve api.github.com", panel.ProblemText, StringComparison.Ordinal);
        Assert.Empty(panel.Threads);
        Assert.False(panel.IsEmpty);

        // The next reading is the one that decides again: one row, no problem line, a clock.
        platform.ThreadsResult = PrResult<IReadOnlyList<PrReviewThread>>.Ok([Open("T1", "src/one.cs", 10)]);
        panel.RefreshCommand.Execute(null);
        await panel.Watch;

        Assert.False(panel.HasProblem);
        Assert.Equal(["T1"], panel.Threads.Select(row => row.Id));
        Assert.True(panel.HasLastRead);
        Assert.Equal(2, platform.ThreadsCalls);
    }

    [Fact]
    public async Task A_refresh_keeps_the_row_it_already_had_and_its_typed_reply()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var before = Assert.Single(panel.Threads);
        before.ReplyDraft = "still typing";
        panel.RefreshCommand.Execute(null);
        await panel.Watch;

        // The same instance comes back: the draft, the status line and the scroll position stay where the user left them.
        Assert.Same(before, Assert.Single(panel.Threads));
        Assert.Equal("still typing", before.ReplyDraft);
        Assert.Equal(2, platform.ThreadsCalls);
    }

    [Fact]
    public async Task Close_clears_the_panel_through_its_host_and_dispose_stops_the_reading()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var closed = 0;
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        var panel = NewPanel(platform, close: () => closed++);
        panel.Start();
        await panel.Watch;

        panel.CloseCommand.Execute(null);
        Assert.Equal(1, closed);

        panel.Dispose();
        panel.Dispose();
        panel.RefreshCommand.Execute(null);

        // A disposed panel does not read the platform again, and its commands say so.
        Assert.False(panel.RefreshCommand.CanExecute(null));
        Assert.Equal(1, platform.ThreadsCalls);
    }

    // ------------------------------------------------------------ buckets, idempotence, refresh identity

    [Fact]
    public async Task A_thread_that_is_both_resolved_and_outdated_is_listed_as_outdated()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var both = new PrReviewThread(
            "T-both", true, true, "src/both.cs", null, null, 77, "RIGHT", "alice",
            [Comment("T-both-c1", "alice", "moved and settled")]);
        var platform = new CommentsPlatform(both);
        using var panel = NewPanel(platform, filter: CommentFilter.Outdated);
        panel.Start();
        await panel.Watch;

        // Outdated wins over resolved: the diff no longer has the line, so that is where the user looks for it.
        var row = Assert.Single(panel.Threads);
        Assert.Equal(CommentFilter.Outdated, row.Filter);
        Assert.True(row.IsOutdated);
        Assert.Equal("src/both.cs:77(\u539f\u884c)", row.LocationText);

        panel.ShowResolvedCommand.Execute(null);
        Assert.Empty(panel.Threads);
    }

    [Fact]
    public async Task Resolving_twice_writes_only_once()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.ResolveCommand.Execute(null);
        await row.Pending;
        Assert.Equal(["T1"], platform.Resolved);
        Assert.True(row.IsResolved);

        // The second click sees the row is already resolved and answers instead of writing again.
        row.ResolveCommand.Execute(null);
        await row.Pending;
        Assert.Single(platform.Resolved);
        Assert.Equal(Loc.Instance[PrCommentsStrings.ResolveAlready], row.StatusText);
        Assert.False(row.StatusIsError);
    }

    [Fact]
    public async Task A_refresh_replaces_removed_threads_and_adds_new_ones_without_duplicates()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10), Open("T2", "src/two.cs", 20));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;
        Assert.Equal(2, panel.Threads.Count);
        var kept = panel.Threads[0];
        kept.ReplyDraft = "keep me";

        // T1 stays (same instance, draft kept), T2 is gone, T3 is new: two rows, no duplicates.
        platform.ThreadsResult = PrResult<IReadOnlyList<PrReviewThread>>.Ok(
            [Open("T1", "src/one.cs", 10), Open("T3", "src/three.cs", 30)]);
        panel.RefreshCommand.Execute(null);
        await panel.Watch;

        Assert.Equal(["T1", "T3"], panel.Threads.Select(static row => row.Id));
        Assert.Same(kept, panel.Threads[0]);
        Assert.Equal("keep me", kept.ReplyDraft);
        Assert.Equal(2, panel.Threads.Select(static row => row.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_initial_read_failure_reports_problem_with_no_rows()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform()
        {
            ThreadsResult = PrResult<IReadOnlyList<PrReviewThread>>.Fail(
                GitRemoteFailureKind.Network, "gh: could not resolve api.github.com"),
        };
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        // Nothing was ever read: the problem line shows, the empty state does not claim "no threads".
        Assert.True(panel.HasProblem);
        Assert.Contains("could not resolve api.github.com", panel.ProblemText, StringComparison.Ordinal);
        Assert.Empty(panel.Threads);
        Assert.False(panel.HasThreads);
        Assert.False(panel.IsEmpty);
        Assert.False(panel.HasLastRead);
        Assert.Equal(string.Empty, panel.LastReadText);
    }

    [Fact]
    public async Task An_empty_reading_reports_zero_counts_and_a_clock()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform();
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        Assert.Empty(panel.Threads);
        Assert.True(panel.IsEmpty);
        Assert.Equal(Loc.Instance[PrCommentsStrings.EmptyOpen], panel.EmptyTitleText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterOpen, "0"), panel.OpenFilterText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterResolved, "0"), panel.ResolvedFilterText);
        Assert.Equal(Loc.Instance.Format(PrCommentsStrings.FilterOutdated, "0"), panel.OutdatedFilterText);
        Assert.True(panel.HasLastRead);
        Assert.NotEqual(string.Empty, panel.LastReadText);
    }

    [Fact]
    public async Task A_failed_hand_off_reports_a_readable_error()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform, _ => Task.FromException<ChecksHandOff>(new InvalidOperationException("queue full")));
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        row.HandOffCommand.Execute(null);
        await row.Pending;

        Assert.True(row.StatusIsError);
        Assert.Contains("queue full", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_without_a_path_reports_an_unknown_location()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var pathless = new PrReviewThread(
            "T-x", false, false, null, 10, null, null, "RIGHT", null,
            [Comment("T-x-c1", "alice", "where?")]);
        var platform = new CommentsPlatform(pathless);
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        Assert.Equal(Loc.Instance[PrCommentsStrings.LocationUnknown], row.LocationText);
        Assert.False(row.IsOriginalLocation);
        Assert.Equal(string.Empty, row.PathText);
    }

    [Fact]
    public async Task A_resolved_thread_without_an_author_reports_a_plain_resolved()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var anonymous = new PrReviewThread(
            "T-r", true, false, "src/r.cs", 5, null, null, "RIGHT", null,
            [Comment("T-r-c1", "alice", "fixed")]);
        var platform = new CommentsPlatform(anonymous);
        using var panel = NewPanel(platform, filter: CommentFilter.Resolved);
        panel.Start();
        await panel.Watch;

        var row = Assert.Single(panel.Threads);
        Assert.Equal(Loc.Instance[PrCommentsStrings.Resolved], row.ResolutionText);
        Assert.True(row.HasResolution);
    }

    [Fact]
    public async Task Resolved_and_outdated_threads_take_no_reply_but_still_hand_off()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var handed = new List<string>();
        var platform = new CommentsPlatform(
            Resolved("T-r", "src/r.cs", 5, "alice"),
            Outdated("T-o", "src/o.cs", originalLine: 9));
        using var panel = NewPanel(
            platform,
            message =>
            {
                handed.Add(message);
                return Task.FromResult(ChecksHandOff.Ran);
            });
        panel.Start();
        await panel.Watch;

        // The open filter lists nothing: both threads sit in their own buckets.
        Assert.Empty(panel.Threads);

        panel.ShowResolvedCommand.Execute(null);
        var resolved = Assert.Single(panel.Threads);
        Assert.False(resolved.IsReplyable);
        Assert.False(resolved.IsReplyEnabled);
        Assert.False(resolved.CanReply);
        Assert.True(resolved.CanHandOff);
        resolved.HandOffCommand.Execute(null);
        await resolved.Pending;
        Assert.Single(handed);

        panel.ShowOutdatedCommand.Execute(null);
        var outdated = Assert.Single(panel.Threads);
        Assert.False(outdated.CanReply);
        Assert.False(outdated.IsReplyEnabled);
        Assert.True(outdated.CanHandOff);
        Assert.False(outdated.CanReopen);
    }

    [Fact]
    public async Task A_language_switch_relabels_the_filters()
    {
        using var outer = new LanguageScope(UiLanguage.Chinese);
        var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
        using var panel = NewPanel(platform);
        panel.Start();
        await panel.Watch;

        var chinese = panel.OpenFilterText;
        Assert.Contains("\u672a\u89e3\u51b3", chinese, StringComparison.Ordinal);

        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Contains("open", panel.OpenFilterText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("resolved", panel.ResolvedFilterText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outdated", panel.OutdatedFilterText, StringComparison.OrdinalIgnoreCase);
        }

        // Back to the outer language: the chips read Chinese again.
        Assert.Equal(chinese, panel.OpenFilterText);
    }

    // ------------------------------------------------------------ helpers

    private static PrCommentsViewModel NewPanel(
        CommentsPlatform platform,
        Func<string, Task<ChecksHandOff>>? handToAgent = null,
        Action? close = null,
        CommentFilter filter = CommentFilter.Open)
    {
        var panel = new PrCommentsViewModel(
            WorkDirectory,
            Number,
            "https://example.test/acme/forge/pull/12",
            platform,
            action => action(),
            _ => { },
            handToAgent,
            close);
        if (filter != CommentFilter.Open)
        {
            panel.ShowOpenCommand.Execute(null);
            switch (filter)
            {
                case CommentFilter.Resolved:
                    panel.ShowResolvedCommand.Execute(null);
                    break;
                case CommentFilter.Outdated:
                    panel.ShowOutdatedCommand.Execute(null);
                    break;
            }
        }

        return panel;
    }

    private static PrReviewComment Comment(string id, string author, string body) =>
        new(id, null, author, body, new DateTimeOffset(2026, 11, 15, 10, 20, 0, TimeSpan.Zero), null);

    private static PrReviewThread Open(string id, string path, int line) =>
        new(id, false, false, path, line, null, null, "RIGHT", null, [Comment(id + "-c1", "alice", "please fix this")]);

    private static PrReviewThread Resolved(string id, string path, int line, string who) =>
        new(id, true, false, path, line, null, null, "RIGHT", who, [Comment(id + "-c1", who, "fixed")]);

    private static PrReviewThread Outdated(string id, string path, int? originalLine) =>
        new(id, false, true, path, null, null, originalLine, "RIGHT", null, [Comment(id + "-c1", "carol", "this moved")]);
}

/// <summary>
/// A <see cref="IPrPlatform"/> that answers the three review-thread calls from a script and refuses everything else (the
/// base class throws), so a test observes exactly what the panel asked for: the replies with their thread ids and bodies,
/// the resolved thread ids and how often the threads were read.
/// </summary>
internal sealed class CommentsPlatform(params PrReviewThread[] threads) : ThrowingPrPlatform
{
    public override string Name => "Test";

    /// <summary>What the next read answers; a test replaces it to script a refresh.</summary>
    public PrResult<IReadOnlyList<PrReviewThread>> ThreadsResult { get; set; } =
        PrResult<IReadOnlyList<PrReviewThread>>.Ok(threads);

    public PrResult<PrReviewComment> ReplyResult { get; set; } =
        PrResult<PrReviewComment>.Ok(new PrReviewComment("C-reply", null, "me", "replied", DateTimeOffset.UnixEpoch, null));

    public PrResult<PrThreadResolution> ResolveResult { get; set; } =
        PrResult<PrThreadResolution>.Ok(new PrThreadResolution("T1", true));

    public int ThreadsCalls { get; private set; }

    /// <summary>Every reply as <c>threadId:body</c>, in order.</summary>
    public List<string> Replies { get; } = [];

    public List<string> Resolved { get; } = [];

    public override Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default)
    {
        ThreadsCalls++;
        return Task.FromResult(ThreadsResult);
    }

    public override Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(
        string workingDirectory,
        string threadId,
        string body,
        CancellationToken ct = default)
    {
        Replies.Add(threadId + ":" + body);
        return Task.FromResult(ReplyResult);
    }

    public override Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(
        string workingDirectory,
        string threadId,
        CancellationToken ct = default)
    {
        Resolved.Add(threadId);
        return Task.FromResult(ResolveResult);
    }
}