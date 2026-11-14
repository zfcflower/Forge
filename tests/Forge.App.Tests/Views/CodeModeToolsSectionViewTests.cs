using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The <c>tools</code> section's real view, run headless (wave W14): the run_code script block has to render its four
/// controls, each spinner has to offer exactly the range its preference clamps to, an edit has to write through to the
/// live preferences, and a reload has to show a value changed behind the view's back.
/// </summary>
[Collection("forge-app-ui")]
public sealed class CodeModeToolsSectionViewTests : IAsyncDisposable
{
    private readonly (string Root, EnvironmentSnapshot Previous) temp = TempDataRoot.Redirect("forge-code-mode-view-");
    private readonly AppComposition composition;

    public CodeModeToolsSectionViewTests()
    {
        composition = AppComposition.Create();
    }

    public async ValueTask DisposeAsync()
    {
        await composition.DisposeAsync();
        temp.Previous.Restore();
        TestTempFiles.DeleteTree(temp.Root);
    }

    [Fact]
    public async Task The_script_block_renders_bounded_spinners_and_writes_through_to_the_preferences()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                using var section = new Forge.App.ViewModels.Settings.ToolsSettingsSectionViewModel(composition);
                section.Reload();
                var window = SharedUi.Show(new ToolsSettingsSectionView { DataContext = section }, 900, 700);
                SharedUi.Settle(window);

                var concurrency = Control(window, "settings-code-concurrency") as NumericUpDown;
                var resultChars = Control(window, "settings-code-result-chars") as NumericUpDown;
                var innerResultChars = Control(window, "settings-code-inner-result-chars") as NumericUpDown;
                var tools = Control(window, "settings-code-tools") as TextBox;

                var initial = new Snapshot(
                    HasConcurrency: concurrency is not null,
                    HasResultChars: resultChars is not null,
                    HasInnerResultChars: innerResultChars is not null,
                    HasTools: tools is not null,
                    ConcurrencyMinimum: concurrency?.Minimum,
                    ConcurrencyMaximum: concurrency?.Maximum,
                    ResultCharsMinimum: resultChars?.Minimum,
                    ResultCharsMaximum: resultChars?.Maximum,
                    InnerResultCharsMinimum: innerResultChars?.Minimum,
                    InnerResultCharsMaximum: innerResultChars?.Maximum,
                    ConcurrencyValue: concurrency?.Value,
                    ToolsText: tools?.Text);

                // A field edit is the write: the values land in the live preferences the sessions read.
                concurrency!.Value = 16;
                innerResultChars!.Value = 777;
                tools!.Text = "read, mcp__*";
                SharedUi.Settle(window);
                var written = new Snapshot(
                    HasConcurrency: true,
                    HasResultChars: true,
                    HasInnerResultChars: true,
                    HasTools: true,
                    ConcurrencyMinimum: null,
                    ConcurrencyMaximum: null,
                    ResultCharsMinimum: null,
                    ResultCharsMaximum: null,
                    InnerResultCharsMinimum: null,
                    InnerResultCharsMaximum: null,
                    ConcurrencyValue: concurrency.Value,
                    ToolsText: tools.Text);
                var storedConcurrency = composition.Preferences.CodeScriptMaxConcurrentCalls;
                var storedInnerResultChars = composition.Preferences.CodeScriptInnerResultChars;
                var storedTools = composition.Preferences.CodeScriptTools;

                // A change made outside the view reaches the fields through Reload.
                composition.Preferences.CodeScriptResultChars = 5555;
                section.Reload();
                SharedUi.Settle(window);
                var reloadedValue = resultChars!.Value;
                var concurrencyAfterReload = concurrency.Value;

                window.Close();
                return (initial, written, storedConcurrency, storedInnerResultChars, storedTools, reloadedValue, concurrencyAfterReload);
            },
            budget.Token);

        Assert.True(rendered.initial.HasConcurrency, "no concurrency spinner was rendered");
        Assert.True(rendered.initial.HasResultChars, "no result-chars spinner was rendered");
        Assert.True(rendered.initial.HasInnerResultChars, "no inner-result-chars spinner was rendered");
        Assert.True(rendered.initial.HasTools, "no tools field was rendered");

        // The spinners offer exactly the range the preference setter clamps to, and open on the stored values.
        Assert.Equal((decimal)AppPreferences.MinCodeScriptMaxConcurrentCalls, rendered.initial.ConcurrencyMinimum);
        Assert.Equal((decimal)AppPreferences.MaxCodeScriptMaxConcurrentCalls, rendered.initial.ConcurrencyMaximum);
        Assert.Equal((decimal)AppPreferences.MinCodeScriptResultChars, rendered.initial.ResultCharsMinimum);
        Assert.Equal((decimal)AppPreferences.MaxCodeScriptResultChars, rendered.initial.ResultCharsMaximum);
        Assert.Equal((decimal)AppPreferences.MinCodeScriptInnerResultChars, rendered.initial.InnerResultCharsMinimum);
        Assert.Equal((decimal)AppPreferences.MaxCodeScriptInnerResultChars, rendered.initial.InnerResultCharsMaximum);
        Assert.Equal((decimal)AppPreferences.DefaultCodeScriptMaxConcurrentCalls, rendered.initial.ConcurrencyValue);
        Assert.Equal(AppPreferences.DefaultCodeScriptTools, rendered.initial.ToolsText);

        // The fields are bound two ways: the edits land in the live preferences.
        Assert.Equal(16, rendered.storedConcurrency);
        Assert.Equal(777, rendered.storedInnerResultChars);
        Assert.Equal("read, mcp__*", rendered.storedTools);

        // Reload re-reads the preferences, in both directions.
        Assert.Equal(5555m, rendered.reloadedValue);
        Assert.Equal(16m, rendered.concurrencyAfterReload);
    }

    private static Avalonia.Visual? Control(Window window, string automationId) =>
        window.GetVisualDescendants()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private sealed record Snapshot(
        bool HasConcurrency,
        bool HasResultChars,
        bool HasInnerResultChars,
        bool HasTools,
        decimal? ConcurrencyMinimum,
        decimal? ConcurrencyMaximum,
        decimal? ResultCharsMinimum,
        decimal? ResultCharsMaximum,
        decimal? InnerResultCharsMinimum,
        decimal? InnerResultCharsMaximum,
        decimal? ConcurrencyValue,
        string? ToolsText);
}
