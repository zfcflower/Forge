using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The <c>projects</c> settings section's real view, run headless (wave W9): the spinner, the read-only directory field and
/// the two buttons have to exist, the spinner has to be bounded by the preference's range and write what it is given, and the
/// clear button has to follow the field. A view that failed to load would leave the settings page blank while every view model
/// test still passed.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectsSettingsSectionViewTests
{
    private const string Fallback = @"C:\forge-workspace-parent";

    [Fact]
    public async Task The_section_renders_its_spinner_directory_field_and_buttons()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var section = new ProjectsSettingsSectionViewModel(preferences, Fallback);
                var window = SharedUi.Show(new ProjectsSettingsSectionView { DataContext = section }, 900, 400);
                SharedUi.Settle(window);

                var spinner = Control(window, "settings-projects-timeout") as NumericUpDown;
                var directory = Control(window, "settings-projects-directory") as TextBox;
                var browse = Control(window, "settings-projects-browse") as Button;
                var clear = Control(window, "settings-projects-clear") as Button;

                var initial = new Snapshot(
                    HasSpinner: spinner is not null,
                    HasDirectory: directory is not null,
                    HasBrowse: browse is not null,
                    HasClear: clear is not null,
                    Minimum: spinner?.Minimum,
                    Maximum: spinner?.Maximum,
                    Value: spinner?.Value,
                    DirectoryText: directory?.Text,
                    ExpectedDirectoryText: section.EffectiveDirectoryText,
                    ClearEnabled: clear?.IsEffectivelyEnabled ?? true,
                    BrowseEnabled: browse?.IsEffectivelyEnabled ?? false);

                // Typing in the spinner is the write: the field edits the live preference, which the next dialog reads.
                spinner!.Value = 45;
                SharedUi.Settle(window);
                var writtenTimeout = preferences.ProjectsCloneTimeoutSeconds;
                var sectionTimeout = section.CloneTimeoutSeconds;

                // The clear button empties the field and the section falls back to the directory it shows.
                section.CloneDirectory = @"D:\picked";
                SharedUi.Settle(window);
                var picked = directory?.Text;
                var clearNowEnabled = clear?.IsEffectivelyEnabled ?? false;
                clear!.Command?.Execute(null);
                SharedUi.Settle(window);
                var cleared = section.EffectiveDirectoryText;
                var emptyField = directory?.Text;
                var clearAfterClearing = clear.IsEffectivelyEnabled;

                var snapshot = (Initial: initial, WrittenTimeout: writtenTimeout, SectionTimeout: sectionTimeout, Picked: picked, ClearNowEnabled: clearNowEnabled, Cleared: cleared, EmptyField: emptyField, ClearAfterClearing: clearAfterClearing);
                window.Close();
                return snapshot;
            },
            budget.Token);

        // The four controls the section is made of are there.
        Assert.True(rendered.Initial.HasSpinner, "no clone timeout spinner was rendered");
        Assert.True(rendered.Initial.HasDirectory, "no directory field was rendered");
        Assert.True(rendered.Initial.HasBrowse, "no browse button was rendered");
        Assert.True(rendered.Initial.HasClear, "no clear button was rendered");

        // The spinner offers exactly the range the preference accepts, and opens on the stored value.
        Assert.Equal((decimal)AppPreferences.MinProjectsCloneTimeoutSeconds, rendered.Initial.Minimum);
        Assert.Equal((decimal)AppPreferences.MaxProjectsCloneTimeoutSeconds, rendered.Initial.Maximum);
        Assert.Equal(0m, rendered.Initial.Value);

        // An empty field shows what it falls back to instead of reading as "nowhere", and the clear button is disabled.
        Assert.Equal(rendered.Initial.ExpectedDirectoryText, rendered.Initial.DirectoryText);
        Assert.Equal(Fallback, rendered.Initial.DirectoryText);
        Assert.False(rendered.Initial.ClearEnabled);
        Assert.True(rendered.Initial.BrowseEnabled);

        // The spinner is bound two ways: the value typed in the view lands in the live preference.
        Assert.Equal(45, rendered.WrittenTimeout);
        Assert.Equal(45, rendered.SectionTimeout);

        // A picked directory shows in the field and enables the clear button, which empties it again.
        Assert.Equal(@"D:\picked", rendered.Picked);
        Assert.True(rendered.ClearNowEnabled);
        Assert.Equal(Fallback, rendered.Cleared);
        Assert.Equal(Fallback, rendered.EmptyField);
        Assert.False(rendered.ClearAfterClearing);
    }

    private static Visual? Control(Window window, string automationId) =>
        window.GetVisualDescendants()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private sealed record Snapshot(
        bool HasSpinner,
        bool HasDirectory,
        bool HasBrowse,
        bool HasClear,
        decimal? Minimum,
        decimal? Maximum,
        decimal? Value,
        string? DirectoryText,
        string ExpectedDirectoryText,
        bool ClearEnabled,
        bool BrowseEnabled);
}