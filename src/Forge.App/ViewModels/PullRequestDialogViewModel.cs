using Forge.App.Git;
using Forge.App.ViewModels;

namespace Forge.App.ViewModels;

/// <summary>
/// The 打开 PR dialog: a title (prefilled with the branch name, which is what a reviewer reads first), an empty body the user
/// writes, and the draft flag. It owns no git work — the row creates the pull request after the answer comes back.
/// </summary>
internal sealed class PullRequestDialogViewModel : ObservableObject
{
    public PullRequestDialogViewModel(string title, string body, bool draft)
    {
        Title = title;
        Body = body;
        IsDraft = draft;
    }

    public string Title
    {
        get;
        set => Set(ref field, value ?? string.Empty);
    }

    public string Body
    {
        get;
        set => Set(ref field, value ?? string.Empty);
    }

    public bool IsDraft
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>True once there is a title to send: a pull request without one is rejected by the platform.</summary>
    public bool CanCreate => Title.Trim().Length > 0;

    /// <summary>The answer the dialog returns; null while cancelled.</summary>
    public PullRequestDraft ToDraft() => new(Title.Trim(), Body, IsDraft);
}
