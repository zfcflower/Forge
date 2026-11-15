using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The window's pull request review-thread panel (wave W16a owns this file). <see cref="Comments"/> is the overlay the
/// window shows: null hides it, an instance is drawn by the template registered for its type in <c>App.axaml</c>. The
/// overlay slot in <c>MainWindow.axaml</c> is pre-wired by the W10 contract step so W16a never edits that file. The panel
/// reads threads through <c>IPrPlatform.GetReviewThreadsAsync</c> and replies/resolves through the existing platform API;
/// handing a thread to the agent goes through the same session hand-off as checks and Mermaid fixes.
/// <para>The panel belongs to one session, like the checks panel: the worktree it reads comes from the active session when
/// it opens, and closing the overlay releases everything it had in flight. Opening it again (another pull request, or the
/// same one clicked twice) replaces the panel instead of stacking a second read.</para>
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? comments;

    public object? Comments
    {
        get => comments;
        private set => Set(ref comments, value);
    }

    /// <summary>
    /// Opens the review-thread panel for <paramref name="number"/> over the active session's worktree. A no-op without an
    /// active session: the number comes from the git row of that session's branch, so the pair is only inconsistent when the
    /// tab closed between the click and the call.
    /// </summary>
    internal void OpenCommentsForPullRequest(int number)
    {
        if (number <= 0)
        {
            return;
        }

        var session = activeSession;
        var workDirectory = session?.WorkDir;
        if (workDirectory is not { Length: > 0 })
        {
            AppLogs.Session.ZLogDebug($"comments panel skipped: no worktree for pull request #{number}");
            return;
        }

        CloseComments();
        var panel = new PrCommentsViewModel(
            workDirectory,
            number,
            GitSync.PullRequestUrl,
            composition.PullRequests,
            action => Dispatcher.UIThread.Post(action),
            url => OpenChecksLink(url),
            message => HandCommentsToAgentAsync(session, message),
            CloseComments);
        Comments = panel;
        panel.Start();
    }

    /// <summary>Closes and releases the panel; idempotent.</summary>
    private void CloseComments()
    {
        if (Comments is PrCommentsViewModel panel)
        {
            Comments = null;
            panel.Dispose();
        }
    }

    /// <summary>
    /// Hands one review thread's message to the session through its own hand-off contract: an idle session runs it as one
    /// turn, a busy one gets it in its composer so the user sends it when the current turn is done — never a second turn
    /// behind the user's back. The origin label differs from the checks panel's; the hand-off itself is the same rule.
    /// </summary>
    private async Task<ChecksHandOff> HandCommentsToAgentAsync(SessionWorkspaceViewModel? session, string message)
    {
        if (session is null || !session.CanHandOff)
        {
            return ChecksHandOff.NoSession;
        }

        var ran = await session
            .HandMessageAsync(message, LocText.Literal(Loc.Instance[PrCommentsStrings.HandOffOrigin]))
            .ConfigureAwait(true);
        return ran ? ChecksHandOff.Ran : ChecksHandOff.Queued;
    }
}