using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>projects</c> settings section (wave W9): the clone timeout and the default project directory edit the live
/// preference rather than a copy, so the next add-project dialog uses them without a restart. The timeout is clamped to the
/// range the spinner offers, the directory field trims what it is given, an empty field falls back to the directory shown
/// instead of reading as "nowhere", and the clear button follows the field's content.
/// </summary>
public sealed class ProjectsSettingsSectionTests
{
    private const string Fallback = @"C:\forge-workspace-parent";

    [Fact]
    public void The_timeout_round_trips_to_the_preference()
    {
        var preferences = new AppPreferences();
        var section = new ProjectsSettingsSectionViewModel(preferences, Fallback);
        Assert.Equal(AppPreferences.DefaultProjectsCloneTimeoutSeconds, section.CloneTimeoutSeconds);

        section.CloneTimeoutSeconds = 30;

        Assert.Equal(30, preferences.ProjectsCloneTimeoutSeconds);
        Assert.Equal(30, section.CloneTimeoutSeconds);

        // Written from elsewhere it is read back without a reload: the dialog reads the live value.
        preferences.ProjectsCloneTimeoutSeconds = 90;
        Assert.Equal(90, section.CloneTimeoutSeconds);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(7200, AppPreferences.MaxProjectsCloneTimeoutSeconds)]
    [InlineData(int.MaxValue, AppPreferences.MaxProjectsCloneTimeoutSeconds)]
    public void A_timeout_outside_the_range_lands_and_re_reads_clamped(int written, int expected)
    {
        var preferences = new AppPreferences();
        var section = new ProjectsSettingsSectionViewModel(preferences, Fallback);

        section.CloneTimeoutSeconds = written;

        Assert.Equal(expected, preferences.ProjectsCloneTimeoutSeconds);
        Assert.Equal(expected, section.CloneTimeoutSeconds);
    }

    [Fact]
    public void The_field_range_is_the_preference_range()
    {
        var section = new ProjectsSettingsSectionViewModel(new AppPreferences(), Fallback);

        // One range for the spinner and the setter: a narrower field would hide values the preference takes, a wider one would
        // offer values it silently rejects.
        Assert.Equal(AppPreferences.MinProjectsCloneTimeoutSeconds, section.MinCloneTimeoutSeconds);
        Assert.Equal(AppPreferences.MaxProjectsCloneTimeoutSeconds, section.MaxCloneTimeoutSeconds);
        Assert.InRange(section.CloneTimeoutSeconds, section.MinCloneTimeoutSeconds, section.MaxCloneTimeoutSeconds);
    }

    [Fact]
    public void The_directory_is_trimmed_and_an_empty_one_falls_back()
    {
        var preferences = new AppPreferences();
        var section = new ProjectsSettingsSectionViewModel(preferences, Fallback);
        Assert.True(section.UsesDefaultDirectory);
        Assert.Equal(Fallback, section.EffectiveDirectoryText);
        Assert.Equal(string.Empty, section.CloneDirectory);

        section.CloneDirectory = @"  D:\projects  ";

        Assert.Equal(@"D:\projects", preferences.ProjectsCloneDirectory);
        Assert.Equal(@"D:\projects", section.CloneDirectory);
        Assert.False(section.UsesDefaultDirectory);
        Assert.Equal(@"D:\projects", section.EffectiveDirectoryText);

        section.CloneDirectory = "   ";

        Assert.Equal(string.Empty, preferences.ProjectsCloneDirectory);
        Assert.True(section.UsesDefaultDirectory);
        Assert.Equal(Fallback, section.EffectiveDirectoryText);
    }

    [Fact]
    public void Changing_the_directory_announces_everything_that_depends_on_it()
    {
        var section = new ProjectsSettingsSectionViewModel(new AppPreferences(), Fallback);
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        var canExecuteChanged = 0;
        section.ClearCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        section.CloneDirectory = @"D:\projects";

        Assert.Contains(nameof(ProjectsSettingsSectionViewModel.CloneDirectory), raised);
        Assert.Contains(nameof(ProjectsSettingsSectionViewModel.EffectiveDirectoryText), raised);
        Assert.Contains(nameof(ProjectsSettingsSectionViewModel.UsesDefaultDirectory), raised);
        Assert.Equal(1, canExecuteChanged);
        Assert.True(section.ClearCommand.CanExecute(null));
    }

    [Fact]
    public void The_clear_button_empties_the_field_and_disables_itself_again()
    {
        var section = new ProjectsSettingsSectionViewModel(new AppPreferences(), Fallback);
        Assert.False(section.ClearCommand.CanExecute(null));

        // A disabled command must not clear anything, however it is invoked.
        section.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, section.CloneDirectory);

        section.CloneDirectory = @"D:\projects";
        Assert.True(section.ClearCommand.CanExecute(null));

        section.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, section.CloneDirectory);
        Assert.True(section.UsesDefaultDirectory);
        Assert.Equal(Fallback, section.EffectiveDirectoryText);
        Assert.False(section.ClearCommand.CanExecute(null));
    }

    [Fact]
    public void The_browse_button_asks_the_view_for_a_folder()
    {
        var section = new ProjectsSettingsSectionViewModel(new AppPreferences(), Fallback);
        var requests = 0;
        section.BrowseRequested += (_, _) => requests++;

        section.BrowseCommand.Execute(null);

        Assert.Equal(1, requests);

        // The view answers by setting the field, which is the only writer of the preference.
        section.CloneDirectory = @"E:\picked";
        Assert.Equal(@"E:\picked", section.EffectiveDirectoryText);
        Assert.Equal(1, requests);
    }
}