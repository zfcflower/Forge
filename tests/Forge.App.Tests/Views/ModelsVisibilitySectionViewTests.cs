using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The <c>models-visibility</c> section's real view, run headless: the XAML has to load and its compiled bindings have to reach
/// the section view model, because App.axaml only picks the view up by the view model's type and a template that binds to
/// nothing would fail silently. One switch per row, and typing in the search box narrows the rendered list.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ModelsVisibilitySectionViewTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-models-visibility-view-");

    [Fact]
    public async Task The_view_renders_one_switch_per_model_and_the_search_filters_the_list()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var services = Services();
                using var page = new ModelsVisibilitySectionViewModel(services, preferences);
                page.Reload();
                var models = page.Rows.Count;

                var window = SharedUi.Show(new ModelsVisibilitySectionView { DataContext = page }, 900, 700);
                SharedUi.Settle(window);

                var switches = Switches(window);
                var search = window.GetVisualDescendants().OfType<TextBox>().Single();

                search.Text = "beta";
                SharedUi.Settle(window);
                var narrowed = page.Rows.Select(static row => row.Name).ToList();
                var narrowedSwitches = Switches(window);

                search.Text = string.Empty;
                SharedUi.Settle(window);
                var restored = Switches(window);

                window.Close();
                return (Models: models, Switches: switches.Count, Narrowed: narrowed, NarrowedSwitches: narrowedSwitches.Count, Restored: restored.Count);
            },
            budget.Token);

        Assert.Equal(5, rendered.Models);
        Assert.Equal(5, rendered.Switches);
        Assert.Equal(["beta-1", "shared"], rendered.Narrowed);
        Assert.Equal(2, rendered.NarrowedSwitches);
        Assert.Equal(5, rendered.Restored);
    }

    private static List<CheckBox> Switches(Window window) =>
        window.GetVisualDescendants()
            .OfType<CheckBox>()
            .Where(static box => AutomationProperties.GetAutomationId(box)?.StartsWith("models-visibility-", StringComparison.Ordinal) == true)
            .ToList();

    private ProviderServices Services()
    {
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, """
        { "providers": { "version": 1, "defaultId": "alpha", "items": [
          { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
            "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["alpha-1", "alpha-2", "shared"], "defaultModel": "alpha-1" },
          { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
            "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["beta-1", "shared"], "defaultModel": "beta-1" }
        ] } }
        """);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        return new ProviderServices(settingsPath, data, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    }

    public void Dispose() => TestTempFiles.DeleteQuietly(root);
}
