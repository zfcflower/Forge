using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The "Screenshots" button of the Git tab: it takes the before/after images of the branch's change and puts them into the
/// <c>## Screenshots</c> section of the pull request, through the same <see cref="ScreenshotRunner"/> the
/// <c>pr_screenshots</c> tool uses, so the two can never disagree about what is captured or how it is published.
/// <para>The command that will run is shown next to the button: it is the project's own declaration
/// (<c>.forge/screenshots.json</c> of this worktree, read with the rest of the snapshot), and the button is only offered
/// when there is one, because the panel has no way to invent a capture. Clicking it is the user's confirmation - the panel
/// does not go through <see cref="Forge.App.Approval.IApprovalHandler"/> for its own buttons - which is why the command it
/// is about to run is on screen before the click.</para>
/// <para>Threading: like every other write of the panel, the run happens on the thread pool under the panel's lifetime
/// token, counts as <see cref="IsBusy"/> (the capture writes into the worktree, so nothing else may touch it meanwhile) and
/// reports its outcome on the worktree that started it, which is re-read afterwards when that worktree is still attached.</para>
/// </summary>
internal sealed partial class GitPanelViewModel
{
    /// <summary>Runs the project's screenshot command and publishes the images to the pull request.</summary>
    public RelayCommand ScreenshotsCommand { get; }

    /// <summary>The screenshot group is part of the pull request half; without <see cref="GitPrServices"/> it stays hidden.</summary>
    public bool ShowScreenshots => HasPullRequest && pullRequests is not null;

    /// <summary>True when this branch declares how to take its screenshots, which is what enables the button.</summary>
    public bool HasScreenshotCommand => snapshot.Screenshot is not null;

    /// <summary>"Will run: npm run screenshots", or why nothing can be captured; empty when the group is hidden.</summary>
    public string ScreenshotCommandText
    {
        get
        {
            if (!ShowScreenshots)
            {
                return string.Empty;
            }

            return snapshot.Screenshot is { } config
                ? Loc.Instance.Format(GitStrings.ScreenshotCommand, config.Command)
                : Loc.Instance[GitStrings.ScreenshotNoCommand];
        }
    }

    /// <summary>
    /// ".forge/screenshots.json is not usable: …" when the declaration cannot be read; empty otherwise. Shown under the
    /// command so a broken file is visible instead of only disabling the button.
    /// </summary>
    public string ScreenshotProblemText
    {
        get
        {
            if (!ShowScreenshots || snapshot.ScreenshotProblem is not { } problem)
            {
                return string.Empty;
            }

            return Loc.Instance.Format(GitStrings.ScreenshotConfigProblem, problem);
        }
    }

    public bool HasScreenshotProblem => ScreenshotProblemText.Length > 0;

    /// <summary>The command's precondition: a pull request, a usable declaration and no other write in flight.</summary>
    private bool CanTakeScreenshots() =>
        !disposed && pullRequests is not null && HasContext && snapshot.HasRepository && HasPullRequest
        && snapshot.Screenshot is not null && !IsBusy;

    /// <summary>
    /// Runs the capture and the publication. The user's click is the confirmation, so nothing here asks again; a failure is
    /// reported through the panel's result area with the hint of its failure kind, like any other operation.
    /// </summary>
    private async Task RunScreenshotsAsync()
    {
        if (disposed || busy)
        {
            return;
        }

        if (!HasContext)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorNoRepository, null);
            return;
        }

        if (pullRequests is not { } services || pullRequest is not { } info)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorScreenshotNoPullRequest, null);
            return;
        }

        if (snapshot.Screenshot is null)
        {
            ApplyLocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorScreenshotNoCommand, null);
            return;
        }

        var state = current;
        var workingDirectory = context.WorkingDirectory;
        state.Writing = true;
        RefreshBusy();
        try
        {
            var runner = new ScreenshotRunner(git, services.Platform, services.GitSettings, services.ScreenshotOptions);
            ScreenshotRunResult run;
            try
            {
                run = await Task.Run(
                        () => runner.RunAsync(workingDirectory, info.BaseRefName, new ScreenshotRunRequest(), lifetime.Token),
                        lifetime.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (IsGitFailure(exception))
            {
                Report(state, LocalFailure(GitRemoteFailureKind.Other, GitStrings.ErrorGitFailed, exception.Message));
                return;
            }

            if (disposed)
            {
                return;
            }

            Report(state, new PanelResult(run.Failure, run.Message, string.Empty, null, null));
            if (IsCurrent(state))
            {
                await ReadCoreAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            state.Writing = false;
            EndWork();
        }
    }

    /// <summary>The declaration read with the snapshot; called on a pool thread from <see cref="ReadSnapshotAsync"/>.</summary>
    private static (ScreenshotConfig? Config, string? Problem) ReadScreenshotConfig(string workingDirectory)
    {
        var loaded = ScreenshotConfigLoader.Load(workingDirectory);
        return loaded.State switch
        {
            ScreenshotConfigState.Valid => (loaded.Config, null),
            ScreenshotConfigState.Invalid => (null, loaded.Error),
            _ => (null, null),
        };
    }

    private void RaiseScreenshotTexts()
    {
        OnPropertyChanged(nameof(ShowScreenshots));
        OnPropertyChanged(nameof(HasScreenshotCommand));
        OnPropertyChanged(nameof(ScreenshotCommandText));
        OnPropertyChanged(nameof(ScreenshotProblemText));
        OnPropertyChanged(nameof(HasScreenshotProblem));
    }
}
