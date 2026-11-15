using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests;
using Forge.App.Tests.Localization;
using Forge.App.Tests.ViewModels;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The review-thread panel's view (wave W16a, L2): the overlay template in <c>App.axaml</c> draws this view for a
/// <see cref="PrCommentsViewModel"/>, the threads reach the visual tree with their locations and Markdown bodies, and
/// the row actions appear only when they can act. A missing <c>StaticResource</c>, a binding to a member that does not
/// exist or an item template that never realizes fails here; nothing else in the suite instantiates this view.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PrCommentsViewTests
{
    [Fact]
    public async Task The_panel_loads_and_renders_the_title_the_threads_and_the_filters()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        await SharedUi.RunAsync(async () =>
        {
            var platform = new CommentsPlatform(
                Open("T1", "src/one.cs", 10),
                Resolved("T2", "src/two.cs", 20, "alice"));
            using var panel = NewPanel(platform);
            panel.Start();
            await panel.Watch;

            var window = SharedUi.Show(new PrCommentsView { DataContext = panel }, 700, 640);
            SharedUi.Settle(window);

            try
            {
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ToList();
                Assert.Contains(Loc.Instance[PrCommentsStrings.Title], texts);
                Assert.Contains(panel.NumberText, texts);
                Assert.Contains("src/one.cs:10", texts);

                // The three filter chips carry their counts, and only the open thread is listed.
                var visibleButtons = VisibleButtonIds(window);
                Assert.Contains("pr-comments-filter-open", visibleButtons);
                Assert.Contains("pr-comments-filter-resolved", visibleButtons);
                Assert.Contains("pr-comments-filter-outdated", visibleButtons);
                Assert.Contains("pr-comments-refresh", visibleButtons);
                Assert.Contains("pr-comments-close", visibleButtons);

                var threads = window.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(static border => border.Classes.Contains("thread"))
                    .ToList();
                Assert.Single(threads);

                // The comment body is rendered Markdown, not raw source.
                var bodies = window.GetVisualDescendants()
                    .OfType<MarkdownCommentBody>()
                    .ToList();
                Assert.Single(bodies);
                Assert.NotNull(bodies[0].Child);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task An_outdated_thread_shows_the_original_line_and_offers_no_reply_box()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        await SharedUi.RunAsync(async () =>
        {
            var platform = new CommentsPlatform(Outdated("T3", "src/three.cs", originalLine: 42));
            using var panel = NewPanel(platform, CommentFilter.Outdated);
            panel.Start();
            await panel.Watch;

            var window = SharedUi.Show(new PrCommentsView { DataContext = panel }, 700, 640);
            SharedUi.Settle(window);

            try
            {
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ToList();
                Assert.Contains("src/three.cs:42(原行)", texts);

                var visibleButtons = VisibleButtonIds(window);
                Assert.Contains("pr-comments-handoff", visibleButtons);
                Assert.DoesNotContain("pr-comments-reply", visibleButtons);
                // An outdated thread is still open, so it can be resolved; only the reply box is gone.
                Assert.Contains("pr-comments-resolve", visibleButtons);

                // No reply box is drawn for a thread the diff moved past.
                var boxes = window.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Where(static box => string.Equals(
                        AutomationProperties.GetAutomationId(box),
                        "pr-comments-reply-box",
                        StringComparison.Ordinal))
                    .Where(static box => box.IsEffectivelyVisible)
                    .ToList();
                Assert.Empty(boxes);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task A_resolved_thread_offers_reopen_as_a_disabled_reason_not_a_dead_action()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        await SharedUi.RunAsync(async () =>
        {
            var platform = new CommentsPlatform(Resolved("T2", "src/two.cs", 20, "alice"));
            using var panel = NewPanel(platform, CommentFilter.Resolved);
            panel.Start();
            await panel.Watch;

            var window = SharedUi.Show(new PrCommentsView { DataContext = panel }, 700, 640);
            SharedUi.Settle(window);

            try
            {
                var buttons = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(static button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "pr-comments-reopen",
                        StringComparison.Ordinal))
                    .ToList();
                var reopen = Assert.Single(buttons);
                Assert.False(reopen.IsEnabled);
                Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(reopen)?.ToString()));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task The_title_follows_a_language_switch()
    {
        var session = SharedUi.Session;
        var previous = Loc.Instance.Language;
        try
        {
            await session.Dispatch(
                async () =>
                {
                    Loc.Instance.Language = UiLanguage.Chinese;
                    var platform = new CommentsPlatform(Open("T1", "src/one.cs", 10));
                    using var panel = NewPanel(platform);
                    panel.Start();
                    await panel.Watch;

                    var window = SharedUi.Show(new PrCommentsView { DataContext = panel }, 700, 640);
                    SharedUi.Settle(window);
                    try
                    {
                        var chinese = window.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Select(static block => block.Text)
                            .FirstOrDefault(static text => !string.IsNullOrEmpty(text) && text.Contains("评论", StringComparison.Ordinal));
                        Assert.NotNull(chinese);

                        Loc.Instance.Language = UiLanguage.English;
                        SharedUi.Settle(window);

                        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ToList();
                        Assert.Contains("Review comments", texts);
                    }
                    finally
                    {
                        window.Close();
                    }

                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    return 0;
                },
                CancellationToken.None);
        }
    }

    [Fact]
    public void The_window_wires_the_comments_overlay_and_the_app_template_draws_this_view()
    {
        var root = RepoRoot.Find();
        Assert.NotNull(root);

        // The sixth overlay slot: MainWindow draws whatever MainWindowViewModel.Comments holds.
        var mainWindow = File.ReadAllText(Path.Combine(root, "src/Forge.App/MainWindow.axaml"));
        Assert.Contains("Content=\"{Binding Comments}\"", mainWindow, StringComparison.Ordinal);

        // App.axaml maps the panel's view model to this view; without it the overlay slot stays blank.
        var app = File.ReadAllText(Path.Combine(root, "src/Forge.App/App.axaml"));
        Assert.Contains("DataType=\"vm:PrCommentsViewModel\"", app, StringComparison.Ordinal);
        Assert.Contains("<views:PrCommentsView />", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_markdown_body_renders_code_and_lists_and_an_empty_body_draws_nothing()
    {
        await SharedUi.RunAsync(async () =>
        {
            await Task.Yield();

            var code = new MarkdownCommentBody { Markdown = "```cs\nvar x = 1;\n```\n\n- a\n- b" };
            var empty = new MarkdownCommentBody { Markdown = string.Empty };
            var panel = new StackPanel();
            panel.Children.Add(code);
            panel.Children.Add(empty);

            var window = SharedUi.Show(panel, 700, 400);
            SharedUi.Settle(window);

            try
            {
                Assert.NotNull(code.Child);
                Assert.Null(empty.Child);

                // Re-attaching with the same text does not rebuild the tree.
                var first = code.Child;
                code.Markdown = "```cs\nvar x = 1;\n```\n\n- a\n- b";
                SharedUi.Settle(window);
                Assert.Same(first, code.Child);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<string?> VisibleButtonIds(Window window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(static button => button.IsEffectivelyVisible)
            .Select(static button => AutomationProperties.GetAutomationId(button))
            .ToList();

    private static PrCommentsViewModel NewPanel(
        CommentsPlatform platform,
        CommentFilter filter = CommentFilter.Open) =>
        PanelFor(platform, filter);

    private static PrCommentsViewModel PanelFor(CommentsPlatform platform, CommentFilter filter)
    {
        var panel = new PrCommentsViewModel(
            "work",
            12,
            "https://example.test/acme/forge/pull/12",
            platform,
            action => action(),
            _ => { },
            _ => Task.FromResult(ChecksHandOff.NoSession),
            null);
        if (filter == CommentFilter.Resolved)
        {
            panel.ShowResolvedCommand.Execute(null);
        }
        else if (filter == CommentFilter.Outdated)
        {
            panel.ShowOutdatedCommand.Execute(null);
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
