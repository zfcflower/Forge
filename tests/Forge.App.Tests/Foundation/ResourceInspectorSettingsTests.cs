using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Pins the <c>inspector</c> settings section (wave W8): the two fields write the live preferences, the preference setters
/// clamp what they write (so the field reads back the clamped value and the shown range is the real range), the section is
/// registered by the composition root, and its view renders both inputs bound to it.
/// </summary>
public sealed class ResourceInspectorSettingsTests
{
    [Fact]
    public void The_fields_edit_the_live_preferences()
    {
        var preferences = new AppPreferences();
        var model = new ResourceInspectorSettingsViewModel(preferences);

        model.TailLines = 120;
        model.RefreshMilliseconds = 1500;

        Assert.Equal(120, preferences.InspectorLogTailLines);
        Assert.Equal(1500, preferences.InspectorRefreshMilliseconds);
        Assert.Equal(120, model.TailLines);
        Assert.Equal(1500, model.RefreshMilliseconds);

        // The section offers exactly the range the preference setter clamps to.
        Assert.Equal(AppPreferences.MinInspectorLogTailLines, model.MinTailLines);
        Assert.Equal(AppPreferences.MaxInspectorLogTailLines, model.MaxTailLines);
        Assert.Equal(AppPreferences.MinInspectorRefreshMilliseconds, model.MinRefreshMilliseconds);
        Assert.Equal(AppPreferences.MaxInspectorRefreshMilliseconds, model.MaxRefreshMilliseconds);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_read_back_clamped()
    {
        var preferences = new AppPreferences();
        var model = new ResourceInspectorSettingsViewModel(preferences);

        model.TailLines = AppPreferences.MinInspectorLogTailLines - 1;
        var shortTail = (model.TailLines, preferences.InspectorLogTailLines);
        model.TailLines = AppPreferences.MaxInspectorLogTailLines + 1;
        var longTail = (model.TailLines, preferences.InspectorLogTailLines);

        model.RefreshMilliseconds = AppPreferences.MinInspectorRefreshMilliseconds - 1;
        var fastRefresh = (model.RefreshMilliseconds, preferences.InspectorRefreshMilliseconds);
        model.RefreshMilliseconds = AppPreferences.MaxInspectorRefreshMilliseconds + 1;
        var slowRefresh = (model.RefreshMilliseconds, preferences.InspectorRefreshMilliseconds);

        Assert.Equal((AppPreferences.MinInspectorLogTailLines, AppPreferences.MinInspectorLogTailLines), shortTail);
        Assert.Equal((AppPreferences.MaxInspectorLogTailLines, AppPreferences.MaxInspectorLogTailLines), longTail);
        Assert.Equal((AppPreferences.MinInspectorRefreshMilliseconds, AppPreferences.MinInspectorRefreshMilliseconds), fastRefresh);
        Assert.Equal((AppPreferences.MaxInspectorRefreshMilliseconds, AppPreferences.MaxInspectorRefreshMilliseconds), slowRefresh);
    }

    [Fact]
    public async Task The_composition_registers_the_inspector_section()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-inspector-settings-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;

                var section = Assert.Single(
                    composition.Settings.Items,
                    item => string.Equals(item.Id, SettingsSectionIds.Inspector, StringComparison.Ordinal));

                Assert.Equal(SettingsSectionIds.InspectorOrder, section.Order);
                Assert.Equal(SettingsGroups.Interface, section.Group);
                Assert.Equal(ResourceInspectorStrings.SettingsTitleKey, section.TitleKey);
                Assert.Equal("IconLayers", section.IconKey);

                var model = Assert.IsType<ResourceInspectorSettingsViewModel>(section.ViewModel);
                Assert.Equal(composition.Preferences.InspectorLogTailLines, model.TailLines);
                Assert.Equal(composition.Preferences.InspectorRefreshMilliseconds, model.RefreshMilliseconds);
            }
            finally
            {
                await composition.DisposeAsync();
            }
        }
        finally
        {
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }
}

/// <summary>The section body as a screen reader and a mouse see it: the two numeric fields and their bound values.</summary>
[Collection("forge-app-ui")]
public sealed class ResourceInspectorSettingsHeadlessTests
{
    [Fact]
    public async Task The_section_view_renders_both_inputs_bound_to_the_preferences()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var preferences = new AppPreferences { InspectorLogTailLines = 120, InspectorRefreshMilliseconds = 1500 };
                var model = new ResourceInspectorSettingsViewModel(preferences);
                var window = SharedUi.Show(new ResourceInspectorSettingsSectionView { DataContext = model }, 900, 400);
                SharedUi.Settle(window);

                var tail = Find<NumericUpDown>(window, "inspector-tail-lines");
                var refresh = Find<NumericUpDown>(window, "inspector-refresh");
                Assert.NotNull(tail);
                Assert.NotNull(refresh);
                var ids = AutomationIds(window, onlyVisible: true);
                var labels = Texts(window);
                var beforeWrite = tail!.Value;

                model.TailLines = 40;
                SharedUi.Settle(window);
                var afterWrite = tail!.Value;

                window.Close();
                return (
                    Tail: beforeWrite,
                    Refresh: refresh!.Value,
                    TailRange: (tail.Minimum, tail.Maximum),
                    RefreshRange: (refresh.Minimum, refresh.Maximum),
                    AfterWrite: afterWrite,
                    Ids: ids,
                    Labels: labels,
                    Label: ResourceInspectorStrings.SettingsTailLines);
            },
            CancellationToken.None);

        Assert.Equal(120m, state.Tail);
        Assert.Equal(1500m, state.Refresh);
        Assert.Equal((20m, 1000m), state.TailRange);
        Assert.Equal((250m, 10000m), state.RefreshRange);
        Assert.Equal(40m, state.AfterWrite);
        Assert.Contains("inspector-tail-lines", state.Ids);
        Assert.Contains("inspector-refresh", state.Ids);
        Assert.Contains(state.Label, state.Labels);
    }

    private static T? Find<T>(Visual from, string id)
        where T : Control =>
        from.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal));

    private static List<string> AutomationIds(Window window, bool onlyVisible = false) =>
        window.GetVisualDescendants()
            .Where(control => !onlyVisible || control.IsEffectivelyVisible)
            .Select(AutomationProperties.GetAutomationId)
            .Where(static id => !string.IsNullOrEmpty(id))
            .Select(static id => id!)
            .ToList();

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList();
}