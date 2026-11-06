using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins the workbench wiring in <c>App.axaml</c> (forge-app spec, "App/TerminalViewModel" and the approved workbench
/// wiring): a <see cref="TerminalViewModel"/> resolves to a <see cref="TerminalView"/> through the application's own
/// data templates, and the settings view model resolves to the terminal settings body carrying the documented
/// automation ids.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalViewTemplateTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Terminal_view_model_resolves_to_the_terminal_view()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var found = await TerminalUi.RunTestAsync(
                session,
                async () =>
                {
                    var viewModel = new TerminalViewModel(
                        "s1",
                        2,
                        new FakeTerminalHost(),
                        new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 30)),
                        new AppPreferences());
                    try
                    {
                        var window = new Window
                        {
                            Width = 1000,
                            Height = 700,
                            Content = new ContentControl { Content = viewModel },
                        };
                        TerminalUi.Show(window);
                        try
                        {
                            await Task.CompletedTask;
                            return window.GetVisualDescendants().OfType<TerminalView>().Any();
                        }
                        finally
                        {
                            window.Close();
                        }
                    }
                    finally
                    {
                        await viewModel.DisposeAsync();
                    }
                },
                Budget);

        Assert.True(found, "a TerminalViewModel must template into a TerminalView (App.axaml)");
    }

    [Fact]
    public async Task Terminal_settings_view_model_resolves_to_the_settings_body()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var ids = await TerminalUi.RunTestAsync(
                session,
                async () =>
                {
                    var window = new Window
                    {
                        Width = 800,
                        Height = 600,
                        Content = new ContentControl { Content = new TerminalSettingsViewModel(new AppPreferences()) },
                    };
                    TerminalUi.Show(window);
                    try
                    {
                        await Task.CompletedTask;
                        return window.GetVisualDescendants()
                            .OfType<Control>()
                            .Select(control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) ?? string.Empty)
                            .Where(id => id.StartsWith("terminal-settings-", StringComparison.Ordinal))
                            .OrderBy(id => id)
                            .ToList();
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                Budget);

            Assert.Equal(
                ["terminal-settings-arguments", "terminal-settings-scrollback", "terminal-settings-shell"],
                ids);
    }
}
