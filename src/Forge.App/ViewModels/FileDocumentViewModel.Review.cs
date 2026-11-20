using Forge.App.Review;

namespace Forge.App.ViewModels;

internal sealed partial class FileDocumentViewModel
{
    private FileReviewController? reviewController;

    /// <summary>
    /// The owner the opener pinned to this document (a stored diagnostic, a definition target); null when the document follows
    /// the tab on screen. Kept here so a tab switch can re-resolve the owner without losing the pin.
    /// </summary>
    private ReviewOwner? pinnedReviewOwner;

    internal FileReviewController ReviewController => reviewController ??= new FileReviewController(this);
    internal ReviewOwner? ReviewOwner => reviewController?.Owner;
    internal int ReviewVersion => document?.Version ?? -1;

    /// <summary>The owner the opener pinned; null means the preview's comments follow the active tab.</summary>
    internal ReviewOwner? PinnedReviewOwner => pinnedReviewOwner;

    internal void PinReviewOwner(ReviewOwner? owner) => pinnedReviewOwner = owner;

    internal void ConfigureReview(ReviewServices? services, ReviewOwner? owner)
    {
        // The shell re-resolves a preview's owner on every tab switch; a switch that moves neither the services nor the owner
        // must keep the tab's drafts instead of dropping and reloading them.
        if (reviewController?.IsConfiguredFor(services, owner) == true)
        {
            return;
        }

        ReviewController.Configure(services, owner);
    }

    internal void DisposeReview()
    {
        reviewController?.Dispose();
        reviewController = null;
    }

    internal async Task<bool> ReviewSaveAsync()
    {
        var doc = document;
        if (doc is null || IsEditorFaulted)
        {
            return false;
        }

        if (!IsDirty)
        {
            return true;
        }

        if (isSaving)
        {
            return false;
        }

        await SaveCoreAsync(CancellationToken.None).ConfigureAwait(true);
        return ReferenceEquals(document, doc) && !IsDirty && !HasSaveError && !IsEditorFaulted;
    }
}
