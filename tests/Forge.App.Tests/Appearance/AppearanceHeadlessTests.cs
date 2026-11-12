using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Appearance;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// Headless proof of TASK-fonts: after a preference change the real style sheet (<c>Controls.axaml</c> +
/// <c>AppearanceStyles.axaml</c>) makes a plain <see cref="TextBlock"/> and a <c>mono</c> one pick up the new family and
/// size at once, the composition root wires the applier and the settings section, and a stored font preference reaches the
/// editors even though the load raises no change notification.
/// </summary>
[Collection("forge-app-ui")]
public sealed class AppearanceHeadlessTests
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_preference_change_restyles_plain_and_mono_text_at_once()
    {
        var session = SharedUi.Session;
        var token = new CancellationTokenSource(StepBudget).Token;

        var result = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var resources = Application.Current!.Resources;
                var applier = new AppearanceApplier(preferences, resources);
                var plain = new TextBlock { Text = "plain" };
                var mono = new TextBlock { Text = "mono" };
                mono.Classes.Add("mono");
                var window = new Window { Content = new StackPanel { Children = { plain, mono } } };
                try
                {
                    window.Show();
                    SharedUi.Settle(3);
                    var before = (Names(plain), plain.FontSize, Names(mono), mono.FontSize);

                    preferences.UiFontFamily = "Georgia, serif";
                    preferences.UiFontSize = 20;
                    preferences.CodeFontFamily = "Menlo, monospace";
                    preferences.CodeFontSize = 17;
                    SharedUi.Settle(3);
                    var after = (Names(plain), plain.FontSize, Names(mono), mono.FontSize, Names(window));

                    return (before, after);
                }
                finally
                {
                    window.Close();
                    applier.Dispose();
                    RemoveTokens(resources);
                }
            },
            token);

        Assert.Equal(13, result.before.Item2);
        Assert.Equal(13, result.before.Item4);
        Assert.Equal("Georgia|serif", result.after.Item1);
        Assert.Equal(20, result.after.Item2);
        Assert.Equal("Menlo|monospace", result.after.Item3);
        Assert.Equal(17, result.after.Item4);
        Assert.Equal("Georgia|serif", result.after.Item5);
    }

    [Fact]
    public async Task A_second_change_after_the_first_is_picked_up_again()
    {
        var session = SharedUi.Session;
        var token = new CancellationTokenSource(StepBudget).Token;

        var sizes = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var resources = Application.Current!.Resources;
                var applier = new AppearanceApplier(preferences, resources);
                var plain = new TextBlock { Text = "plain" };
                var window = new Window { Content = plain };
                try
                {
                    window.Show();
                    preferences.UiFontSize = 20;
                    SharedUi.Settle(3);
                    var first = plain.FontSize;
                    preferences.UiFontSize = 9;
                    SharedUi.Settle(3);
                    return (first, plain.FontSize);
                }
                finally
                {
                    window.Close();
                    applier.Dispose();
                    RemoveTokens(resources);
                }
            },
            token);

        Assert.Equal(20, sizes.first);
        Assert.Equal(9, sizes.Item2);
    }

    [Fact]
    public async Task Families_reach_the_groups_on_the_ui_thread()
    {
        var session = SharedUi.Session;
        var token = new CancellationTokenSource(StepBudget).Token;

        var offThread = await session.Dispatch(
            async () =>
            {
                var viewModel = new AppearanceSettingsViewModel(
                    new AppPreferences(),
                    new SystemFontCatalog(static () => ["B", "A"]));
                var assignedOffThread = false;
                viewModel.Ui.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FontGroupViewModel.Families) && !Dispatcher.UIThread.CheckAccess())
                    {
                        assignedOffThread = true;
                    }
                };
                try
                {
                    await viewModel.FamiliesLoaded;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(["A", "B"], viewModel.Ui.Families);
                    return assignedOffThread;
                }
                finally
                {
                    viewModel.Dispose();
                }
            },
            token);

        Assert.False(offThread);
    }

    [Fact]
    public async Task The_composition_registers_the_appearance_section_in_order_and_applies_stored_fonts()
    {
        var seen = await ComposeWithStoredFontsAsync();

        Assert.Equal(SettingsSectionIds.AppearanceOrder, seen.Order);
        var provider = seen.Ids.IndexOf(SettingsSectionIds.Provider);
        var appearance = seen.Ids.IndexOf(SettingsSectionIds.Appearance);
        Assert.True(provider >= 0 && provider < appearance, "appearance comes after provider");
        Assert.True(appearance < seen.Ids.IndexOf(SettingsSectionIds.Paths), "appearance comes before paths");
        Assert.Equal(18d, seen.AppliedBase);
        Assert.Equal(15d, seen.AppliedCode);
    }

    [Fact]
    public async Task Stored_font_preferences_reach_the_editors_after_the_load()
    {
        var seen = await ComposeWithStoredFontsAsync();

        Assert.Equal("Georgia", seen.UiFamily);
        Assert.Equal(18m, seen.UiSize);
        Assert.Equal("Menlo", seen.CodeFamily);
        Assert.Equal(15m, seen.CodeSize);
    }

    private async Task<Seen> ComposeWithStoredFontsAsync()
    {
        var session = SharedUi.Session;
        var token = new CancellationTokenSource(StepBudget).Token;
        var root = TestTempFiles.NewDirectory("forge-appearance-");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        Directory.CreateDirectory(Path.Combine(root, "general"));
        await File.WriteAllTextAsync(
            Path.Combine(data, "settings.json"),
            """{ "preferences": { "uiFontFamily": "Georgia", "uiFontSize": 18, "codeFontFamily": "Menlo", "codeFontSize": 15, "language": "en" } }""",
            token);
        var saved = (
            Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
            General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
            Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
        var previousLanguage = Loc.Instance.Language;
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));

        AppComposition? composition = null;
        try
        {
            var seen = await session.Dispatch(
                async () =>
                {
                    composition = AppComposition.Create();
                    var model = new MainWindowViewModel(composition);
                    var window = new MainWindow { DataContext = model };
                    try
                    {
                        window.Show();
                        await composition.Initialization;
                        SharedUi.Settle(3);
                        // That the panel renders every registered section in order is SettingsPanelTests' job (its shared
                        // session); a per-test session here only proves the registration and the applied fonts.

                        var items = model.Settings.Sections.Items;
                        var section = items.Single(static item => item.Id == SettingsSectionIds.Appearance);
                        var editors = Assert.IsType<AppearanceSettingsViewModel>(section.ViewModel);
                        var resources = Application.Current!.Resources;
                        resources.TryGetResource("FontSizeBase", null, out var baseSize);
                        resources.TryGetResource("FontSizeCode", null, out var codeSize);
                        return new Seen(
                            items.Select(static item => item.Id).ToList(),
                            section.Order,
                            editors.Ui.FamilyText,
                            editors.Ui.Size,
                            editors.Code.FamilyText,
                            editors.Code.Size,
                            baseSize as double?,
                            codeSize as double?);
                    }
                    finally
                    {
                        window.Close();
                        model.Dispose();
                    }
                },
                token);

            return seen;
        }
        finally
        {
            if (composition is not null)
            {
                await composition.DisposeAsync();
            }

            await session.Dispatch(
                () =>
                {
                    RemoveTokens(Application.Current!.Resources);
                    Loc.Instance.Language = previousLanguage;
                    return 0;
                },
                CancellationToken.None);
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
            TestTempFiles.DeleteTree(root);
        }
    }

    private static string Names(StyledElement element) =>
        element switch
        {
            TextBlock block => string.Join("|", block.FontFamily.FamilyNames),
            Window window => string.Join("|", window.FontFamily.FamilyNames),
            _ => string.Empty,
        };


    /// <summary>Removes every merged dictionary an applier left behind in the shared application resources.</summary>
    private static void RemoveTokens(IResourceDictionary resources)
    {
        for (var i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (resources.MergedDictionaries[i] is ResourceDictionary child && child.ContainsKey("FontUi"))
            {
                resources.MergedDictionaries.RemoveAt(i);
            }
        }
    }


    private sealed record Seen(
        List<string> Ids,
        int Order,
        string UiFamily,
        decimal? UiSize,
        string CodeFamily,
        decimal? CodeSize,
        double? AppliedBase,
        double? AppliedCode);
}
