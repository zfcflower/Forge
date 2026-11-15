using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.Settings.Prompts;
using Forge.App.ViewModels.Settings;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The skills section's source badge: a discovered skill carries the scope of the folder it came from, and the row badges it
/// with the prompts section's own scope word and colours (the badge convention the two sections share). A row of the
/// approvals section, which has no folder to come from, carries no badge.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SkillsSettingsSectionTests
{
    [Theory]
    [InlineData((int)SkillScope.BuiltIn, PromptTemplateScope.BuiltIn)]
    [InlineData((int)SkillScope.Global, PromptTemplateScope.Global)]
    [InlineData((int)SkillScope.Project, PromptTemplateScope.Project)]
    public void A_skill_row_badges_the_folder_it_came_from_with_the_prompts_scope_word(int scopeValue, PromptTemplateScope promptsScope)
    {
        var scope = (SkillScope)scopeValue;
        var row = SkillsSettingsSectionViewModel.Row(
            new SlashEntry("pdf-tools", "works with PDFs", IsSkill: true, "/ws/.forge/skills/pdf-tools/SKILL.md", scope));

        Assert.Equal("pdf-tools", row.Title);
        Assert.Equal("works with PDFs", row.Detail);
        Assert.Equal("/ws/.forge/skills/pdf-tools/SKILL.md", row.Trailing);
        Assert.True(row.HasScope);
        Assert.Equal(scope == SkillScope.BuiltIn, row.IsBuiltIn);
        Assert.Equal(scope == SkillScope.Global, row.IsGlobal);
        Assert.Equal(scope == SkillScope.Project, row.IsProject);
        // The shared convention: the badge says exactly what a template of the same scope says, in the current language.
        Assert.Equal(PromptRowViewModel.ScopeName(promptsScope), row.ScopeText);
    }

    [Fact]
    public void A_row_without_a_source_has_no_badge_and_no_badge_word()
    {
        var row = new InstrumentRow("deslop · 0.42.0", "Enabled", "2025-06-01");

        Assert.False(row.HasScope);
        Assert.False(row.IsBuiltIn);
        Assert.False(row.IsGlobal);
        Assert.False(row.IsProject);
        Assert.Equal(string.Empty, row.ScopeText);
    }

    /// <summary>The badge is drawn for a scoped row and stays hidden for a row that has no scope.</summary>
    [Fact]
    public async Task The_rendered_row_shows_the_badge_of_a_skill_and_hides_it_for_an_approvals_record()
    {
        await SharedUi.RunAsync(
            () =>
            {
                using var skills = new StubSection(
                [
                    SkillsSettingsSectionViewModel.Row(new SlashEntry("pdf-tools", "works with PDFs", IsSkill: true, "p", SkillScope.Global)),
                ]);
                var skillBadge = BadgeOf(Show(skills));
                Assert.True(skillBadge.IsVisible);
                Assert.Contains("global", skillBadge.Classes);
                Assert.Equal(
                    PromptRowViewModel.ScopeName(PromptTemplateScope.Global),
                    Assert.Single(skillBadge.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()).Text);

                using var approvals = new StubSection([new InstrumentRow("deslop · 0.42.0", "Enabled", "2025-06-01")]);
                Assert.False(BadgeOf(Show(approvals)).IsVisible);
                return Task.CompletedTask;
            });
    }

    private static Border BadgeOf(Window window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => AutomationProperties.GetAutomationId(border) == "instrument-row-scope");

    private static Window Show(InstrumentSettingsSectionViewModel section)
    {
        var window = SharedUi.Show(new InstrumentSectionView { DataContext = section }, 700, 200);
        SharedUi.Settle(window);
        return window;
    }

    /// <summary>The read-only section shape with rows the test hands in, so no approval database or skill folder is needed.</summary>
    private sealed class StubSection : InstrumentSettingsSectionViewModel
    {
        public StubSection(IEnumerable<InstrumentRow> rows) => Replace(rows, "nothing here");

        protected override string HintText => "hint";

        public override void Reload()
        {
        }
    }
}
