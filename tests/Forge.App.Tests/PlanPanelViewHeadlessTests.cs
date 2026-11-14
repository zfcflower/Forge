using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests;

/// <summary>
/// Headless render of <see cref="PlanPanelView"/> (forge-app spec, "计划面板(W7)"): the panel loads and the summary
/// Markdown really becomes controls, Escape and the scrim close it while a press inside the card does not, clicking a
/// revision row previews it, and a step row shows its title plus the 改动:/验证: lines with the hand-off button only
/// on unfinished steps.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PlanPanelViewHeadlessTests
{
    private const string SessionId = "s1";

    private const int PumpIterations = 400;

    private const int PumpDelayMilliseconds = 5;

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_panel_renders_its_parts_and_the_summary_markdown()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "## Summary\n\n正文段落。", At);
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Pending, null, 0, At, GroupId: "g1"));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                Assert.True(await PumpUntilAsync(() => panel.Rows.Count == 2 && panel.HasSummary), "the panel never loaded");
                SharedUi.Settle(window);

                var ids = AutomationIds(window);
                foreach (var id in new[] { "plan-panel", "plan-close", "plan-refresh", "plan-export", "plan-open-pr", "plan-rows", "plan-status" })
                {
                    Assert.Contains(id, ids);
                }

                // The summary is not one text blob: "## Summary" became a level-2 heading control.
                var heading = window.GetVisualDescendants()
                    .OfType<SelectableTextBlock>()
                    .FirstOrDefault(block => InlineText(block) == "Summary" && block.Classes.Contains("md-h2"));
                Assert.NotNull(heading);
                var paragraph = window.GetVisualDescendants()
                    .OfType<SelectableTextBlock>()
                    .FirstOrDefault(block => InlineText(block) == "正文段落。" && block.Classes.Contains("md-p"));
                Assert.NotNull(paragraph);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Escape_closes_the_panel()
    {
        var store = new FakePlanStore();

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                SharedUi.Settle(window);

                var close = Assert.IsType<Button>(view.FindControl<Button>("CloseButton"));
                if (!close.IsFocused)
                {
                    close.Focus();
                }

                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                SharedUi.Settle(window);
                Assert.Equal(1, host.CloseCalls);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_press_on_the_scrim_closes_the_panel_and_a_press_inside_the_card_does_not()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "# 摘要\n\n正文", At);
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Pending, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            // The card is 1120 px wide, centred in the 1200 px window: 40 px of scrim on each side.
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                Assert.True(await PumpUntilAsync(() => panel.Rows.Count == 2), "the panel never loaded");
                SharedUi.Settle(window);

                // A press inside the card (press and release, so the pointer does not stay captured).
                window.MouseDown(new Point(600, 400), MouseButton.Left);
                window.MouseUp(new Point(600, 400), MouseButton.Left);
                SharedUi.Settle(window);
                Assert.Equal(0, host.CloseCalls);

                window.MouseDown(new Point(5, 400), MouseButton.Left);
                window.MouseUp(new Point(5, 400), MouseButton.Left);
                SharedUi.Settle(window);
                Assert.Equal(1, host.CloseCalls);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Clicking_a_revision_row_previews_that_version()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "当前摘要", At);
        store.Revisions.Add(new PlanRevision(SessionId, "旧版本内容", At.AddMinutes(-5)));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                Assert.True(await PumpUntilAsync(() => panel.Revisions.Count == 1), "the revision never loaded");
                SharedUi.Settle(window);

                var row = window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => button.Classes.Contains("plan-revision"));
                Assert.NotNull(row);
                var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("the revision row is not in the window");
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                SharedUi.Settle(window);

                Assert.True(panel.IsViewingRevision);
                Assert.Equal("旧版本内容", panel.SummaryText);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_step_row_shows_its_title_change_and_verification_and_only_unfinished_steps_offer_a_hand_off()
    {
        var store = new FakePlanStore();
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "已完成的步骤", TodoStatus.Done, "备注", 0, At, GroupId: "g1", Summary: "改了布局", Verification: "测试通过", CommitId: "abc1234567890"));
        store.Seed(new TodoItem("t2", SessionId, "待收尾的步骤", TodoStatus.Pending, null, 1, At, GroupId: "g1"));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                Assert.True(await PumpUntilAsync(() => panel.Rows.Count == 3), "the panel never loaded");
                SharedUi.Settle(window);

                var stepRows = window.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(static border => string.Equals(AutomationProperties.GetAutomationId(border), "plan-step-row", StringComparison.Ordinal) && border.IsEffectivelyVisible)
                    .ToList();
                Assert.Equal(2, stepRows.Count);

                var done = stepRows.Single(row => TextInside(row, "plan-step-title") == "已完成的步骤");
                Assert.Equal(PlanStrings.StepSummary("改了布局").Resolve(), TextInside(done, "plan-step-summary"));
                Assert.Equal(PlanStrings.Verification("测试通过").Resolve(), TextInside(done, "plan-step-verification"));
                Assert.Equal("abc1234", TextInside(done, "plan-step-commit"));
                Assert.False(VisibleInside(done, "plan-handoff"));

                var pending = stepRows.Single(row => TextInside(row, "plan-step-title") == "待收尾的步骤");
                Assert.True(VisibleInside(pending, "plan-handoff"));

                // One hand-off button per unfinished step, nowhere else.
                var handoffs = window.GetVisualDescendants()
                    .Where(control => string.Equals(AutomationProperties.GetAutomationId(control), "plan-handoff", StringComparison.Ordinal) && control.IsEffectivelyVisible)
                    .ToList();
                Assert.Single(handoffs);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_refresh_with_an_unchanged_summary_keeps_the_markdown_controls()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "## Summary\n\n正文段落。", At);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            var view = new PlanPanelView { DataContext = panel };
            var window = SharedUi.Show(view, 1200, 800);
            try
            {
                var summaryHost = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("SummaryHost"));
                Assert.True(await PumpUntilAsync(() => panel.HasSummary && summaryHost.Children.Count > 0), "the summary never rendered");
                SharedUi.Settle(window);

                var before = summaryHost.Children.ToList();
                Assert.NotEmpty(before);
                var readsBefore = store.GetPlanSummaryCalls;

                await panel.RefreshAsync(CancellationToken.None);
                SharedUi.Settle(window);

                // The refresh read the store again, but the text did not change: the same controls stay on screen.
                Assert.Equal(readsBefore + 1, store.GetPlanSummaryCalls);
                Assert.Equal(before.Count, summaryHost.Children.Count);
                for (var index = 0; index < before.Count; index++)
                {
                    Assert.Same(before[index], summaryHost.Children[index]);
                }

                // Sensitivity: a text that really changed replaces the rendered controls.
                store.Summary = new PlanSummary(SessionId, "## Summary\n\n换了正文。", At.AddMinutes(1));
                await panel.RefreshAsync(CancellationToken.None);
                SharedUi.Settle(window);

                Assert.Equal(before.Count, summaryHost.Children.Count);
                Assert.NotSame(before[0], summaryHost.Children[0]);
            }
            finally
            {
                window.Close();
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        return condition();
    }

    private static List<string> AutomationIds(Window window) =>
        window.GetVisualDescendants()
            .Select(static control => AutomationProperties.GetAutomationId(control))
            .Where(static id => id is { Length: > 0 })
            .Select(static id => id!)
            .ToList();

    /// <summary>The rendered text of a Markdown block: <c>SelectableTextBlock.Text</c> stays empty when
    /// <c>Inlines</c> were set directly, so the runs are read instead.</summary>
    private static string InlineText(SelectableTextBlock block) =>
        string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(static run => run.Text) ?? []);

    /// <summary>The text of the descendant carrying <paramref name="id"/>, empty when it has none.</summary>
    private static string TextInside(Control root, string id) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => string.Equals(AutomationProperties.GetAutomationId(block), id, StringComparison.Ordinal))
            ?.Text ?? string.Empty;

    /// <summary>Whether the descendant carrying <paramref name="id"/> exists and is on screen.</summary>
    private static bool VisibleInside(Control root, string id) =>
        root.GetVisualDescendants()
            .Any(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal) && control.IsEffectivelyVisible);
}
