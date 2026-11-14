using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The window's pull request checks panel (wave W5 owns this file). <see cref="Checks"/> is the overlay the window shows:
/// null hides it, an instance is drawn by the template registered for its type in <c>App.axaml</c>. The panel is opened from
/// the git row's 提交目标 menu (<see cref="Git.GitSyncViewModel.OpenChecksCommand"/>), which passes the pull request number
/// of the attached session's branch.
/// <para>The panel belongs to one session: the worktree it reads comes from the active session when it opens, and closing the
/// overlay disposes its poll. Handing failing checks to the agent goes through the same session — a turn when it is idle, its
/// composer when it is busy — so a hand-off never interrupts work and never starts a second run.</para>
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? checks;

    public object? Checks
    {
        get => checks;
        private set => Set(ref checks, value);
    }

    /// <summary>
    /// Opens the checks panel for <paramref name="number"/> over the active session's worktree. A no-op without an active
    /// session: the number comes from the git row of that session's branch.
    /// </summary>
    private void OpenChecksForPullRequest(int number)
    {
        if (number <= 0)
        {
            return;
        }

        var session = activeSession;
        var workDirectory = session?.WorkDir;
        if (workDirectory is not { Length: > 0 })
        {
            // Nothing to read the checks from: the git row only offers this for a published session branch, so the window
            // holds an inconsistent pair (a session whose tab closed between the menu and the click).
            AppLogs.Session.ZLogDebug($"checks panel skipped: no worktree for pull request #{number}");
            return;
        }

        // A panel that is already open (another pull request, or the same one clicked again) is replaced, and its poll and
        // language subscription released, instead of being dropped with both still running.
        CloseChecks();
        var panel = new ChecksPanelViewModel(
            workDirectory,
            number,
            GitSync.PullRequestUrl,
            composition.PullRequests,
            () => GitToolPreferences.ChecksOptions(composition.Preferences),
            () => composition.Preferences.GitChecksFailureLogTailLines,
            action => Dispatcher.UIThread.Post(action),
            url => OpenChecksLink(url),
            message => HandChecksToAgentAsync(session, message),
            CloseChecks);
        Checks = panel;
        panel.Start();
    }

    /// <summary>Closes and releases the panel; idempotent.</summary>
    private void CloseChecks()
    {
        if (Checks is ChecksPanelViewModel panel)
        {
            Checks = null;
            panel.Dispose();
        }
    }

    /// <summary>
    /// Hands the panel's message to the session through the session's own hand-off contract (wave W11): an idle session runs
    /// it as one turn, a busy one gets it in its composer so the user sends it when the current turn is done. Never starts a
    /// second turn behind the user's back.
    /// </summary>
    private async Task<ChecksHandOff> HandChecksToAgentAsync(SessionWorkspaceViewModel? session, string message)
    {
        if (session is null || !session.CanHandOff)
        {
            return ChecksHandOff.NoSession;
        }

        var ran = await session
            .HandMessageAsync(message, LocText.Literal(Loc.Instance[ChecksStrings.HandOffOrigin]))
            .ConfigureAwait(true);
        return ran ? ChecksHandOff.Ran : ChecksHandOff.Queued;
    }

    /// <summary>Opens a link from the panel in the system browser; a failure is traced, never thrown into the click.</summary>
    /// <remarks>Only http and https links are opened: a check's link is whatever the CI integration reported, and a
    /// <c>file:</c> or custom-scheme target is not something a click on a check should launch.</remarks>
    internal static bool OpenChecksLink(string url)
    {
        if (!IsWebLink(url))
        {
            AppLogs.Session.ZLogDebug($"checks link not opened, not http(s): '{url}'");
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"could not open '{url}': {exception.Message}");
            return false;
        }
    }

    /// <summary>An absolute http or https URL.</summary>
    internal static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
}
