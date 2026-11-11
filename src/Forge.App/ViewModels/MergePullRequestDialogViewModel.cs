using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// The 合并 PR dialog: three strategies (squash by default, because it is what most repositories here use) and the
/// "delete the head branch on the platform" switch, which starts OFF — deleting it is the one step that cannot be undone
/// from this machine, so it must never be the default.
/// </summary>
internal sealed class MergePullRequestDialogViewModel : ObservableObject
{
    public MergePullRequestDialogViewModel(PullRequestMergeRequest request)
    {
        Request = request;
    }

    public PullRequestMergeRequest Request { get; }

    public string Title => Loc.Instance.Format(GitStrings.ShipMergeDialogTitle, Request.BaseBranch);

    public string Body => Loc.Instance.Format(GitStrings.ShipMergeDialogBody, Request.HeadBranch);

    public string DeleteLabel => Loc.Instance.Format(GitStrings.ShipMergeDialogDelete, Request.HeadBranch);

    public string GoLabel => Loc.Instance[GitStrings.ShipMergeDialogGo];

    /// <summary>
    /// Three radio rows over one backing value: the picked one is the strategy the merge will use. Setting a row to true
    /// selects it and clears the other two; setting one to false is ignored (a radio group always has a selection).
    /// </summary>
    public bool IsSquash
    {
        get => method == PrMergeMethod.Squash;
        set
        {
            if (value)
            {
                SetMethod(PrMergeMethod.Squash);
            }
        }
    }

    public bool IsMergeCommit
    {
        get => method == PrMergeMethod.Merge;
        set
        {
            if (value)
            {
                SetMethod(PrMergeMethod.Merge);
            }
        }
    }

    public bool IsRebase
    {
        get => method == PrMergeMethod.Rebase;
        set
        {
            if (value)
            {
                SetMethod(PrMergeMethod.Rebase);
            }
        }
    }

    public PrMergeMethod SelectedMethod => method;

    private PrMergeMethod method = PrMergeMethod.Squash;

    private void SetMethod(PrMergeMethod next)
    {
        if (method == next)
        {
            return;
        }

        method = next;
        OnPropertyChanged(nameof(IsSquash));
        OnPropertyChanged(nameof(IsMergeCommit));
        OnPropertyChanged(nameof(IsRebase));
        OnPropertyChanged(nameof(SelectedMethod));
    }

    public bool DeleteHeadBranch
    {
        get;
        set => Set(ref field, value);
    }

    public PullRequestMergeChoice ToChoice() => new(SelectedMethod, DeleteHeadBranch);
}
