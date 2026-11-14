using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The checks panel's view (wave W5): the template in <c>App.axaml</c> loads, the verdict, the counts and one row per check
/// reach the visual tree, the failing row offers its log, and the actions appear only when they can act — and one of them,
/// when invoked, really runs. A missing <c>StaticResource</c>, a binding to a member that does not exist or an item template
/// that never realizes fails here; nothing else in the suite instantiates this view.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ChecksPanelViewTests
{
    [Fact]
    public async Task The_panel_loads_and_renders_the_verdict_the_counts_and_every_check()
    {
        await SharedUi.RunAsync(async () =>
        {
            var platform = new ChecksPanelPlatform(ChecksSample.Poll(
                PrMergeStateStatus.Clean,
                ChecksSample.Check("lint", PrCheckBucket.Fail),
                ChecksSample.Check("build", PrCheckBucket.Pending),
                ChecksSample.Check("test", PrCheckBucket.Pass)));
            using var panel = NewPanel(platform);
            panel.Start();
            await panel.Watch;

            var window = SharedUi.Show(new ChecksPanelView { DataContext = panel }, 720, 640);
            SharedUi.Settle(window);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ToList();
            Assert.Contains(Loc.Instance[ChecksStrings.Title], texts);
            Assert.Contains(panel.NumberText, texts);
            Assert.Contains(Loc.Instance[ChecksStrings.VerdictFailed], texts);
            Assert.Contains(panel.SummaryText, texts);
            Assert.Contains("lint", texts);
            Assert.Contains("build", texts);
            Assert.Contains("test", texts);

            var rows = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(static border => border.Classes.Contains("checks-row"))
                .ToList();
            Assert.Equal(3, rows.Count);

            // The failing row is the one that offers a log, and the panel offers the refresh and the close it always has.
            var visibleButtons = VisibleButtonIds(window);
            Assert.Contains("checks-refresh", visibleButtons);
            Assert.Contains("checks-close", visibleButtons);
            Assert.Contains("checks-row-log", visibleButtons);

            // Mark-ready only exists for a draft; this pull request is not one.
            Assert.DoesNotContain("checks-mark-ready", visibleButtons);
        });
    }

    [Fact]
    public async Task The_actions_the_panel_can_offer_are_drawn_and_the_fix_runs()
    {
        await SharedUi.RunAsync(async () =>
        {
            var platform = new ChecksPanelPlatform(ChecksSample.Poll(
                ChecksSample.Merge(PrMergeStateStatus.Draft, draft: true),
                ChecksSample.Check("lint", PrCheckBucket.Fail, run: 11, job: 22),
                ChecksSample.Check("build", PrCheckBucket.Pass)));
            var handed = 0;
            using var panel = new ChecksPanelViewModel(
                "/work",
                12,
                "https://github.com/acme/forge/pull/12",
                platform,
                () => new PrChecksPollerOptions(),
                () => 200,
                action => action(),
                _ => { },
                _ =>
                {
                    handed++;
                    return Task.FromResult(ChecksHandOff.Ran);
                });
            panel.Start();
            await panel.Watch;

            var window = SharedUi.Show(new ChecksPanelView { DataContext = panel }, 720, 640);
            SharedUi.Settle(window);

            var visibleButtons = VisibleButtonIds(window);
            Assert.Contains("checks-mark-ready", visibleButtons);
            Assert.Contains("checks-fix", visibleButtons);
            Assert.Contains("checks-open", visibleButtons);

            var fix = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(static button => AutomationProperties.GetAutomationId(button) == "checks-fix");
            fix.Command?.Execute(null);
            SharedUi.Settle(window);

            Assert.Equal(1, handed);
        });
    }

    private static List<string?> VisibleButtonIds(Window window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(static button => button.IsEffectivelyVisible)
            .Select(static button => AutomationProperties.GetAutomationId(button))
            .ToList();

    private static ChecksPanelViewModel NewPanel(IPrPlatform platform) => new(
        "/work",
        12,
        "https://github.com/acme/forge/pull/12",
        platform,
        () => new PrChecksPollerOptions(),
        () => 200,
        action => action());
}
