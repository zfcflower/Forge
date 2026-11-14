using Avalonia.Platform.Storage;
using ZLogger;
using Avalonia.Controls;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Settings;

namespace Forge.App.Views;

/// <summary>
/// The <c>projects</c> section's view: one timeout number and one directory row, both writing the live preferences
/// through the view model. The folder picker cannot live in the view model (it needs a window), so the view model raises
/// <see cref="ProjectsSettingsSectionViewModel.BrowseRequested"/> and the view answers by setting
/// <see cref="ProjectsSettingsSectionViewModel.CloneDirectory"/>.
/// </summary>
internal sealed partial class ProjectsSettingsSectionView : UserControl
{
    private ProjectsSettingsSectionViewModel? model;

    public ProjectsSettingsSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // The section's view model is assigned by the settings page's template; the picker follows whoever is current.
        if (model is not null)
        {
            model.BrowseRequested -= OnBrowseRequested;
        }

        model = DataContext as ProjectsSettingsSectionViewModel;
        if (model is not null)
        {
            model.BrowseRequested += OnBrowseRequested;
        }
    }

    private async void OnBrowseRequested(object? sender, EventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || model is null)
        {
            return;
        }

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = ProjectCreationStrings.SettingsPickerTitle, AllowMultiple = false });
            if (folders.Count > 0)
            {
                model.CloneDirectory = folders[0].Path.LocalPath;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            // The picker is the platform's; a failure leaves the configured directory untouched and is only reported.
            AppLogs.Settings.ZLogWarning($"the folder picker failed: {exception.Message}");
        }
    }
}
