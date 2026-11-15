namespace Forge.App.ViewModels;

/// <summary>
/// The window's pull request review-thread panel (wave W16a owns this file). <see cref="Comments"/> is the overlay the
/// window shows: null hides it, an instance is drawn by the template registered for its type in <c>App.axaml</c>. The
/// overlay slot in <c>MainWindow.axaml</c> is pre-wired by the W10 contract step so W16a never edits that file. The panel
/// reads threads through <c>IPrPlatform.GetReviewThreadsAsync</c> and replies/resolves through the existing platform API;
/// handing a thread to the agent goes through the same session hand-off as checks and Mermaid fixes.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? comments;

    public object? Comments
    {
        get => comments;
        private set => Set(ref comments, value);
    }
}