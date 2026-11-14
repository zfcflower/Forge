using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Services;
using Forge.Core.Storage;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// The project flows of the shell (wave W9): the add-project picker, the clone/new-repository dialog, and the registration
/// every one of them ends in. The creation itself is <see cref="ProjectCreationService"/>'s; this file only decides which
/// mode the user meant, hands the modal to the host and registers what came back.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The add-project button's flow: one pick for the mode, then the folder picker (an existing folder), or the creation
    /// dialog (a clone or a new repository). Every branch ends by registering one directory and refreshing the trees, so the
    /// new project is visible without a manual refresh.
    /// </summary>
    private async Task AddProjectFlowAsync(CancellationToken ct)
    {
        if (dialogs is null)
        {
            return;
        }

        // A picker rather than three buttons: the project panel's title bar already carries refresh and new-session, and a
        // fourth icon would say nothing about what it does.
        var choice = await dialogs.PickAsync(
                ProjectFlowStrings.AddTitle,
                ProjectFlowStrings.AddQuestion,
                [
                    new PickEntry(ProjectFlowStrings.AddExisting),
                    new PickEntry(ProjectFlowStrings.AddClone),
                    new PickEntry(ProjectFlowStrings.AddCreate),
                ])
            .ConfigureAwait(true);

        switch (choice)
        {
            case 0:
                await AddExistingProjectAsync(ct).ConfigureAwait(true);
                break;
            case 1:
                await CreateProjectAsync(ProjectCreationMode.Clone, ct).ConfigureAwait(true);
                break;
            case 2:
                await CreateProjectAsync(ProjectCreationMode.Create, ct).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// An existing folder: register it, offering <c>git init</c> when it is not a repository yet. A folder that never
    /// becomes one is not registered, because every session lifecycle step after this one assumes a repository.
    /// </summary>
    private async Task AddExistingProjectAsync(CancellationToken ct)
    {
        if (dialogs is null)
        {
            return;
        }

        var folder = await dialogs.PickFolderAsync(ShellStrings.PickRepositoryTitle).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var isRepository = await Task.Run(() => composition.Git.IsRepositoryAsync(folder, ct), ct).ConfigureAwait(true);
        if (!isRepository)
        {
            var confirmed = await dialogs.ConfirmAsync(
                    ShellStrings.NotRepositoryTitle,
                    ShellStrings.NotRepositoryMessage(folder),
                    ShellStrings.GitInit,
                    ShellStrings.Cancel)
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            var init = await Task.Run(() => composition.Git.RunAsync(folder, ["init"], ct), ct).ConfigureAwait(true);
            if (!init.Success)
            {
                Status.SetWorkspaceProblem(ShellStrings.GitInitFailed(init.StandardError.Trim()));
                return;
            }
        }

        await RegisterProjectAsync(folder, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A clone or a brand-new repository: the host shows the creation dialog, and only a directory it hands back is
    /// registered — a cancelled or failed run changes nothing.
    /// </summary>
    private async Task CreateProjectAsync(ProjectCreationMode mode, CancellationToken ct)
    {
        if (dialogs is null)
        {
            return;
        }

        var service = new ProjectCreationService(
            composition.Git,
            composition.Preferences,
            ProjectCreationDefaults.ParentDirectoryOf(composition.Paths.GeneralWorkspaceDirectory));
        using var model = new ProjectCreationViewModel(mode, service);
        var directory = await dialogs.RunProjectCreationAsync(model, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        await RegisterProjectAsync(directory, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Registers one directory as a project and refreshes what shows projects. A registration that fails (a database that
    /// refuses the row, a path git cannot read) is reported once instead of escaping into the dispatcher.
    /// </summary>
    private async Task RegisterProjectAsync(string directory, CancellationToken ct)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (name.Length == 0)
        {
            name = trimmed;
        }

        try
        {
            var registry = composition.Projects;
            await BackgroundRead.RunAsync(token => registry.AddAsync(trimmed, name, ProjectKind.Repository, token), ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            composition.Notifications.Notify(
                NotificationLevel.Error,
                new LocText(ProjectFlowStrings.AddFailedTitleKey),
                ProjectFlowStrings.AddFailed(exception.Message),
                "project-add-failed");
            return;
        }

        RefreshProjectCacheAsync();
        await RefreshTreesAsync(ct).ConfigureAwait(true);
    }
}

/// <summary>Where a project goes when the preference names no directory: the general workspace's parent.</summary>
internal static class ProjectCreationDefaults
{
    /// <summary>
    /// The parent of <paramref name="directory"/>, or <paramref name="directory"/> itself when it has none (a path at the
    /// drive root): a fresh install already owns that place, and creating a project beside the general workspace keeps the
    /// user's own documents out of it.
    /// </summary>
    public static string ParentDirectoryOf(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? trimmed : parent;
    }
}
