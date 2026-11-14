using Forge.App.Preferences;
using Forge.App.Services;
using MermaidKinds = Mermaider.Models.DiagramTypes;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the live Mermaid settings (wave W11). Both readers are projections of <see cref="AppPreferences"/> that the
/// composition root installs before the first window opens, so a test - or a host without preferences - gets the same
/// values the preferences ship with, and an edit is read by the next diagram without a restart.
/// <para><see cref="MermaidSettings"/> is static; every test restores it through <see cref="MermaidSettings.Reset"/>.</para>
/// </summary>
public sealed class MermaidSettingsTests
{
    [Fact]
    public void Reset_restores_the_defaults_the_preferences_ship_with()
    {
        try
        {
            MermaidSettings.DiagramTypes = static () => "pie";
            MermaidSettings.TimeoutMilliseconds = static () => 1234;

            MermaidSettings.Reset();

            Assert.Equal(AppPreferences.DefaultChatMermaidDiagramTypes, MermaidSettings.DiagramTypes());
            Assert.Equal(AppPreferences.DefaultChatMermaidTimeoutMilliseconds, MermaidSettings.TimeoutMilliseconds());
            Assert.Equal(
                MermaidKinds.Flowchart | MermaidKinds.Sequence | MermaidKinds.State | MermaidKinds.Class | MermaidKinds.Er,
                MermaidSettings.EnabledTypes());
            Assert.Equal(
                TimeSpan.FromMilliseconds(AppPreferences.DefaultChatMermaidTimeoutMilliseconds),
                MermaidSettings.Timeout());
        }
        finally
        {
            MermaidSettings.Reset();
        }
    }

    [Fact]
    public void A_replaced_reader_is_followed_on_the_next_call()
    {
        try
        {
            MermaidSettings.DiagramTypes = static () => "pie";
            Assert.Equal(MermaidKinds.Pie, MermaidSettings.EnabledTypes());

            // The preference behind the projection changes; the block must see it without a restart or a reset.
            var setting = "flowchart,gantt";
            MermaidSettings.DiagramTypes = () => setting;
            Assert.Equal(MermaidKinds.Flowchart | MermaidKinds.Gantt, MermaidSettings.EnabledTypes());

            MermaidSettings.DiagramTypes = static () => "flowchart,typo";
            Assert.Equal(MermaidKinds.Flowchart, MermaidSettings.EnabledTypes());

            MermaidSettings.DiagramTypes = static () => string.Empty;
            Assert.Equal((MermaidKinds)0, MermaidSettings.EnabledTypes());
        }
        finally
        {
            MermaidSettings.Reset();
        }
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(499, 500)]
    [InlineData(4000, 4000)]
    [InlineData(30001, 30000)]
    [InlineData(999_999, 30000)]
    public void The_timeout_is_clamped_to_the_preference_bounds(int configured, int expected)
    {
        try
        {
            MermaidSettings.TimeoutMilliseconds = () => configured;

            Assert.Equal(TimeSpan.FromMilliseconds(expected), MermaidSettings.Timeout());
        }
        finally
        {
            MermaidSettings.Reset();
        }
    }
}
