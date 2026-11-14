using Avalonia.Platform.Storage;
using ZLogger;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The clone / new-project dialog: owns nothing but the window. It starts the view model's one run from the primary
/// button, opens the folder picker for the parent directory, and closes with the created directory when the view model
/// completes — null when the user cancelled, and a plain stop request when the user closes while a clone runs (the
/// view model's owner, the shell, disposes it).
/// </summary>
internal partial class ProjectCreationDialog : Window
{
    private ProjectCreationViewModel? model;

    public ProjectCreationDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        model = DataContext as ProjectCreationViewModel;
        if (model is not null)
        {
            model.Completed += OnModelCompleted;
        }

        // The first box is what the user edits first: the remote for a clone, the name otherwise.
        if (model?.IsClone == true)
        {
            UrlBox.Focus();
        }
        else
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // A programmatic close is the model's own answer ("done, here is the folder") or the shell taking the dialog away;
        // only a user close during a run is a stop request. Refusing the former would leave a finished creation invisible:
        // the window stays open, ShowDialog never completes and the project is never registered.
        if (model?.IsRunning == true && !e.IsProgrammatic)
        {
            // Closing mid-clone is a stop request, not an abandonment of a half-written folder: the close is held
            // back while the creation is being cancelled, and the dialog then stays with its result.
            e.Cancel = true;
            model.Cancel();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.Completed -= OnModelCompleted;
        }

        model = null;
        base.OnClosed(e);
    }

    private void OnModelCompleted(object? sender, EventArgs e) => Close(model?.CreatedDirectory);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || model is null)
        {
            return;
        }

        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = ProjectCreationStrings.PickerTitle, AllowMultiple = false });
            if (folders.Count > 0)
            {
                model.ParentDirectory = folders[0].Path.LocalPath;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            // The picker is the platform's; a failure leaves the previous directory in place and is only reported.
            AppLogs.Settings.ZLogWarning($"the folder picker failed: {exception.Message}");
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (model?.IsRunning == true)
        {
            model.Cancel();
            return;
        }

        Close(null);
    }
}
