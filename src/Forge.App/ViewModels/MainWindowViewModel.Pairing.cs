using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// The project row's "pair a remote" button: a repository project without a remote either takes the URL of an existing
/// repository or gets a new GitHub repository created through <c>gh</c>. The dialogs live here, the git and gh work in
/// <see cref="RemotePairing"/>. On success <see cref="RepositoryRemotes.Added"/> tells the live sessions and the git row of
/// that repository, and the tree refreshes so the button goes away. A remote added from outside FORGE is not watched.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private RemotePairing? remotePairing;

    private void WirePairing(ProjectTreeViewModel tree) => tree.PairRemote = PairRemoteAsync;

    private RemotePairing Pairing => remotePairing ??= new RemotePairing(
        composition.Git,
        new ConfiguredGh(
            () => composition.Preferences.GitGhPath,
            composition.Git,
            () => TimeSpan.FromSeconds(composition.Preferences.GitNetworkTimeoutSeconds)));

    private async Task PairRemoteAsync(ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (dialogs is null)
        {
            return;
        }

        var title = PairingStrings.Title;
        var how = await dialogs.ChooseAsync(
                title,
                PairingStrings.Question(project.Name),
                [PairingStrings.ByUrl, PairingStrings.ByGitHub, PairingStrings.Cancel])
            .ConfigureAwait(true);
        Func<CancellationToken, Task<GitRemoteResult>> run;
        switch (how)
        {
            case 0:
                var url = await dialogs.PromptTextAsync(PairingStrings.UrlPrompt, string.Empty).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                run = token => Pairing.PairByUrlAsync(project.RootPath, url, token);
                break;
            case 1:
                var name = await dialogs.PromptTextAsync(
                        PairingStrings.NamePrompt,
                        Path.GetFileName(project.RootPath.TrimEnd('\\', '/')))
                    .ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                var visibility = await dialogs.ChooseAsync(
                        PairingStrings.VisibilityTitle,
                        PairingStrings.VisibilityQuestion(name.Trim()),
                        [PairingStrings.Private, PairingStrings.Public, PairingStrings.Cancel])
                    .ConfigureAwait(true);
                if (visibility is not (0 or 1))
                {
                    return;
                }

                var chosen = visibility == 0 ? PairingVisibility.Private : PairingVisibility.Public;
                run = token => Pairing.PairByGitHubAsync(project.RootPath, name, chosen, token);
                break;
            default:
                return;
        }

        var result = await BackgroundRead.RunAsync(run, ct).ConfigureAwait(true);
        if (!result.Success)
        {
            await dialogs.NotifyAsync(title, PairingStrings.Failed(result.Message)).ConfigureAwait(true);
            return;
        }

        if (RepositoryRemotes.FindConfig(project.RootPath) is { } config)
        {
            RepositoryRemotes.RaiseAdded(config);
        }

        composition.Notifications.Notify(
            NotificationLevel.Success,
            PairingStrings.DoneTitle,
            PairingStrings.Done(project.Name),
            "remote-paired:" + project.Id);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }
}
