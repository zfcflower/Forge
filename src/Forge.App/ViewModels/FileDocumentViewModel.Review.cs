using Forge.App.Review;

namespace Forge.App.ViewModels;

internal sealed partial class FileDocumentViewModel
{
    private FileReviewController? reviewController;

    internal FileReviewController ReviewController => reviewController ??= new FileReviewController(this);
    internal ReviewOwner? ReviewOwner => reviewController?.Owner;
    internal int ReviewVersion => document?.Version ?? -1;

    internal void ConfigureReview(ReviewServices? services, ReviewOwner? owner) => ReviewController.Configure(services, owner);

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
