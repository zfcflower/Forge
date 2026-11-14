using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The <c>rate-limit</c> section's real view, run headless (wave W15): the XAML has to load, its compiled bindings have to
/// reach the section view model, the three fields have to offer exactly the range the preferences clamp to, and every field
/// has to write the live preference (there is no apply step). The labels come from the string table, so a language switch
/// has to reach them too — a view model test would pass with none of this working.
/// </summary>
[Collection("forge-app-ui")]
public sealed class RateLimitSettingsSectionViewTests
{
    [Fact]
    public async Task The_section_renders_its_switch_and_three_fields_bound_to_the_preferences()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var preferences = new AppPreferences();
                var section = new RateLimitSettingsSectionViewModel(preferences);
                var window = SharedUi.Show(new RateLimitSettingsSectionView { DataContext = section }, 900, 600);
                SharedUi.Settle(window);

                var enabled = Control<CheckBox>(window, "rate-limit-enabled");
                var requests = Control<NumericUpDown>(window, "rate-limit-requests");
                var tokens = Control<NumericUpDown>(window, "rate-limit-tokens");
                var gap = Control<NumericUpDown>(window, "rate-limit-automation-gap");
                Assert.NotNull(enabled);
                Assert.NotNull(requests);
                Assert.NotNull(tokens);
                Assert.NotNull(gap);

                var initial = new Snapshot(
                    HasEnabled: enabled is not null,
                    HasRequests: requests is not null,
                    HasTokens: tokens is not null,
                    HasGap: gap is not null,
                    Enabled: enabled!.IsChecked,
                    Requests: requests!.Value,
                    Tokens: tokens!.Value,
                    Gap: gap!.Value,
                    RequestsRange: (requests.Minimum, requests.Maximum),
                    TokensRange: (tokens.Minimum, tokens.Maximum),
                    GapRange: (gap.Minimum, gap.Maximum),
                    ChineseLabel: AutomationProperties.GetName(requests),
                    ExpectedChineseLabel: Loc.Instance[RateLimitStrings.SettingsRequestsKey]);

                // Typing in a field is the write: the section edits the preference the composition publishes from.
                requests.Value = 750;
                tokens.Value = 5_500_000;
                gap.Value = 30;
                enabled.IsChecked = false;
                SharedUi.Settle(window);
                var written = (
                    Requests: preferences.RateLimitRequestsPerMinute,
                    Tokens: preferences.RateLimitTokensPerMinute,
                    Gap: preferences.RateLimitAutomationGapSeconds,
                    Enabled: preferences.RateLimitEnabled);

                // The binding is two-way in the direction the page uses it: the view model raises the change and the field
                // follows, while the section itself holds no copy (it reads the live preference).
                section.RequestsPerMinute = 321;
                SharedUi.Settle(window);
                var reflected = requests.Value;
                preferences.RateLimitRequestsPerMinute = 99;
                var readThrough = section.RequestsPerMinute;

                // …and a value beyond the field's range never comes back unclamped: the preference clamps it on the way in
                // and the field shows what was really stored.
                section.AutomationGapSeconds = AppPreferences.MaxRateLimitAutomationGapSeconds + 5;
                SharedUi.Settle(window);
                var clampedShown = gap.Value;
                var clampedPreference = preferences.RateLimitAutomationGapSeconds;

                // The labels follow the language: the same key resolves to the English text and the bound name follows it.
                Loc.Instance.Language = UiLanguage.English;
                SharedUi.Settle(window);
                var englishLabel = AutomationProperties.GetName(requests);
                var expectedEnglishLabel = Loc.Instance[RateLimitStrings.SettingsRequestsKey];
                var englishTexts = Texts(window);

                window.Close();
                return new Result(
                    initial,
                    written,
                    reflected,
                    readThrough,
                    clampedShown,
                    clampedPreference,
                    englishLabel,
                    expectedEnglishLabel,
                    englishTexts);
            },
            budget.Token);

        // All four controls exist, and the three fields offer the range the preferences clamp to.
        Assert.True(rendered.Initial.HasEnabled, "no enable switch was rendered");
        Assert.True(rendered.Initial.HasRequests, "no requests field was rendered");
        Assert.True(rendered.Initial.HasTokens, "no tokens field was rendered");
        Assert.True(rendered.Initial.HasGap, "no automation gap field was rendered");
        Assert.Equal((decimal)AppPreferences.MinRateLimitRequestsPerMinute, rendered.Initial.RequestsRange.Minimum);
        Assert.Equal((decimal)AppPreferences.MaxRateLimitRequestsPerMinute, rendered.Initial.RequestsRange.Maximum);
        Assert.Equal((decimal)AppPreferences.MinRateLimitTokensPerMinute, rendered.Initial.TokensRange.Minimum);
        Assert.Equal((decimal)AppPreferences.MaxRateLimitTokensPerMinute, rendered.Initial.TokensRange.Maximum);
        Assert.Equal((decimal)AppPreferences.MinRateLimitAutomationGapSeconds, rendered.Initial.GapRange.Minimum);
        Assert.Equal((decimal)AppPreferences.MaxRateLimitAutomationGapSeconds, rendered.Initial.GapRange.Maximum);

        // It opens on the stored values.
        Assert.True(rendered.Initial.Enabled);
        Assert.Equal(600m, rendered.Initial.Requests);
        Assert.Equal(4_000_000m, rendered.Initial.Tokens);
        Assert.Equal(10m, rendered.Initial.Gap);

        // Every field writes its own preference, and turning the switch off reaches the preference as well.
        Assert.Equal((750, 5_500_000, 30, false), rendered.Written);

        // The control follows the view model, and the view model reads the live preference instead of a copy.
        Assert.Equal(321m, rendered.Reflected);
        Assert.Equal(99, rendered.ReadThrough);
        Assert.Equal((decimal)AppPreferences.MaxRateLimitAutomationGapSeconds, rendered.ClampedShown);
        Assert.Equal(AppPreferences.MaxRateLimitAutomationGapSeconds, rendered.ClampedPreference);

        // The Chinese label really was the table's Chinese text, and the switch to English changed it.
        Assert.Equal(rendered.Initial.ExpectedChineseLabel, rendered.Initial.ChineseLabel);
        Assert.Equal(rendered.ExpectedEnglishLabel, rendered.EnglishLabel);
        Assert.NotEqual(rendered.Initial.ChineseLabel, rendered.EnglishLabel);
        Assert.Contains(rendered.ExpectedEnglishLabel, rendered.EnglishTexts);
        Assert.Contains("Automation gap (seconds)", rendered.EnglishTexts);
    }

    /// <summary>
    /// The per-provider table: one row per connected provider, both fields blank when that provider inherits, and typing in a
    /// field writes that provider's entry (the only thing the composition needs to publish a per-provider ceiling).
    /// </summary>
    [Fact]
    public async Task The_provider_table_renders_a_row_per_provider_and_writes_its_entry()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var preferences = new AppPreferences();
                preferences.SetRateLimitOverride("priced", 20, null);
                Func<IReadOnlyList<(string Id, string Name)>> providers = () => [("priced", "Priced"), ("other", "Other")];
                var section = new RateLimitSettingsSectionViewModel(preferences, providers);
                section.Reload();
                var window = SharedUi.Show(new RateLimitSettingsSectionView { DataContext = section }, 900, 700);
                SharedUi.Settle(window);

                var pricedRequests = Control<NumericUpDown>(window, "rate-limit-requests-priced");
                var pricedTokens = Control<NumericUpDown>(window, "rate-limit-tokens-priced");
                var otherRequests = Control<NumericUpDown>(window, "rate-limit-requests-other");
                Assert.NotNull(pricedRequests);
                Assert.NotNull(pricedTokens);
                Assert.NotNull(otherRequests);

                var initial = (
                    PricedRequests: pricedRequests!.Value,
                    PricedTokens: pricedTokens!.Value,
                    OtherRequests: otherRequests!.Value,
                    PricedRange: (pricedRequests.Minimum, pricedRequests.Maximum));

                // Blank inherits, so the token field of a provider that only overrides requests starts empty.
                otherRequests.Value = 12;
                SharedUi.Settle(window);
                var written = preferences.RateLimitOverrideFor("other");

                // Typing the empty field back to nothing clears that dimension, and clearing both removes the whole entry.
                otherRequests.Value = null;
                SharedUi.Settle(window);
                var afterClear = preferences.RateLimitOverrideFor("other");
                preferences.SetRateLimitOverride("priced", null, null);
                SharedUi.Settle(window);

                // A catalogue with no provider hides the table and says so instead.
                var empty = new RateLimitSettingsSectionViewModel(preferences, static () => []);
                empty.Reload();
                var emptyWindow = SharedUi.Show(new RateLimitSettingsSectionView { DataContext = empty }, 900, 700);
                SharedUi.Settle(emptyWindow);
                var emptyRows = emptyWindow.GetVisualDescendants().OfType<NumericUpDown>()
                    .Count(field => AutomationProperties.GetAutomationId(field)?.StartsWith("rate-limit-requests-", StringComparison.Ordinal) == true);
                var emptySaysSo = Texts(emptyWindow).Contains(RateLimitStrings.SettingsNoProviders);

                window.Close();
                emptyWindow.Close();
                return (initial, written, afterClear, emptyRows, emptySaysSo);
            },
            budget.Token);

        // The stored request ceiling is shown, the unstored dimension is blank, and the field keeps the same range as the global one.
        Assert.Equal(20m, rendered.initial.PricedRequests);
        Assert.Null(rendered.initial.PricedTokens);
        Assert.Null(rendered.initial.OtherRequests);
        Assert.Equal((0m, (decimal)AppPreferences.MaxRateLimitRequestsPerMinute), rendered.initial.PricedRange);

        // Typing one dimension stores that one and leaves the other inheriting.
        Assert.NotNull(rendered.written);
        Assert.Equal(12, rendered.written!.Value.RequestsPerMinute);
        Assert.Null(rendered.written.Value.TokensPerMinute);

        // Emptying the field takes the dimension back out, and with both empty the entry is gone.
        Assert.Null(rendered.afterClear);

        // No provider connected: no row fields, and the section says why.
        Assert.Equal(0, rendered.emptyRows);
        Assert.True(rendered.emptySaysSo, "the empty state text was not shown");
    }

    private static T? Control<T>(Visual from, string automationId)
        where T : Control =>
        from.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(static block => block.Text ?? string.Empty)
            .ToList();

    private sealed record Snapshot(
        bool HasEnabled,
        bool HasRequests,
        bool HasTokens,
        bool HasGap,
        bool? Enabled,
        decimal? Requests,
        decimal? Tokens,
        decimal? Gap,
        (decimal Minimum, decimal Maximum) RequestsRange,
        (decimal Minimum, decimal Maximum) TokensRange,
        (decimal Minimum, decimal Maximum) GapRange,
        string? ChineseLabel,
        string ExpectedChineseLabel);

    private sealed record Result(
        Snapshot Initial,
        (int Requests, int Tokens, int Gap, bool Enabled) Written,
        decimal? Reflected,
        int ReadThrough,
        decimal? ClampedShown,
        int ClampedPreference,
        string? EnglishLabel,
        string ExpectedEnglishLabel,
        List<string> EnglishTexts);
}
