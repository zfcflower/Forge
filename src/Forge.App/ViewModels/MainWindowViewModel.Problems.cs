using Forge.App.Editing;
using Forge.App.Localization;
using Forge.Editor.Lsp;

namespace Forge.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Opens the file a problem row points at, with the caret on the diagnostic. A file already open is activated rather
    /// than reopened, and the workspace comes from the diagnostic itself: the list spans every workspace the language
    /// sessions serve, so the active session is not necessarily the one that reported it.
    /// </summary>
    private void OpenProblem(WorkspaceDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _ = OpenProblemAsync(diagnostic);
    }

    private async Task OpenProblemAsync(WorkspaceDiagnostic diagnostic)
    {
        try
        {
            var target = new EditorLspDefinitionTarget(diagnostic.Path, diagnostic.Range.Start, diagnostic.Encoding);

            // The list spans workspaces, so the review owner has to come from the workspace the diagnostic names — not from the
            // active session, which would key a comment on another project's file to the wrong conversation. No session of that
            // workspace means no owner, so review stays off for the file instead of being attached to a stranger.
            var owner = FindReviewOwner(diagnostic.Workspace);
            if (FindFileTab(diagnostic.Path) is { } existing)
            {
                Workbench.ActivateTab(existing);
            }
            else
            {
                await OpenFileAsync(
                    diagnostic.Path,
                    newTab: true,
                    CancellationToken.None,
                    workspaceOverride: diagnostic.Workspace,
                    reviewOwnerOverride: owner,
                    hasReviewOwnerOverride: true,
                    hasWorkspaceOverride: true).ConfigureAwait(true);
            }

            if (FindFileTab(diagnostic.Path) is { Content: FileDocumentViewModel opened } &&
                opened.HasDocument && !opened.HasError && !opened.IsEditorFaulted)
            {
                opened.RevealEditorPosition(target);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Status.SetWorkspaceProblem(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
        }
    }

    /// <summary>
    /// The review owner of a workspace: the active session when it is the one that has this workspace open, else the first
    /// session that does, else null. Several sessions can share one workspace, and the reader is looking at the active one, so
    /// a comment written from this panel must not land in a different conversation's queue.
    /// </summary>
    private Review.ReviewOwner? FindReviewOwner(string workspace)
    {
        if (workspace.Length == 0)
        {
            return null;
        }

        if (activeSession is { WorkDir: { Length: > 0 } activeDirectory } active &&
            string.Equals(activeDirectory, workspace, StringComparison.OrdinalIgnoreCase))
        {
            return new Review.ReviewOwner(active.WorkDir, active.SessionId);
        }

        foreach (var open in sessions.Values)
        {
            if (string.Equals(open.WorkDir, workspace, StringComparison.OrdinalIgnoreCase))
            {
                return new Review.ReviewOwner(open.WorkDir, open.SessionId);
            }
        }

        return null;
    }
}
