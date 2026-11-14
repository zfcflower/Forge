using Forge.App.Editing;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private void AttachEditorNavigation(FileDocumentViewModel file) => file.DefinitionRequested += OnEditorDefinitionRequested;
    private void DetachEditorNavigation(FileDocumentViewModel file) => file.DefinitionRequested -= OnEditorDefinitionRequested;

    private void OnEditorDefinitionRequested(object? sender, EditorLspDefinitionTarget target)
    {
        if (sender is FileDocumentViewModel source)
        {
            _ = NavigateToDefinitionAsync(source, target);
        }
    }

    private bool IsEditorNavigationSourceOpen(FileDocumentViewModel source) =>
        source.Path is { Length: > 0 } path && ReferenceEquals(FindFileTab(path)?.Content, source);

    private async Task NavigateToDefinitionAsync(FileDocumentViewModel source, EditorLspDefinitionTarget target)
    {
        try
        {
            if (!IsEditorNavigationSourceOpen(source))
            {
                return;
            }
            if (FindFileTab(target.Path) is not { Content: FileDocumentViewModel } existingTab)
            {
                await OpenFileAsync(
                    target.Path, newTab: true, CancellationToken.None,
                    workspaceOverride: source.EditorWorkspace,
                    reviewOwnerOverride: source.ReviewOwner,
                    hasReviewOwnerOverride: true,
                    hasWorkspaceOverride: true).ConfigureAwait(true);
                if (!IsEditorNavigationSourceOpen(source))
                {
                    return;
                }
            }
            else
            {
                Workbench.ActivateTab(existingTab);
            }
            if (FindFileTab(target.Path) is { Content: FileDocumentViewModel opened }
                && opened.HasDocument && !opened.HasError && !opened.IsEditorFaulted)
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
}
