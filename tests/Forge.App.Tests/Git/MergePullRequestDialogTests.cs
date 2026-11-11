using Forge.App.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The 合并 PR dialog's defaults are a safety contract, not a preference: squash is preselected (what this repository's
/// pull requests use) and "delete the head branch on GitHub" starts OFF, because deleting it cannot be undone here.
/// </summary>
public sealed class MergePullRequestDialogTests
{
    [Fact]
    public void The_dialog_defaults_to_squash_and_leaves_the_remote_branch_alone()
    {
        var model = new MergePullRequestDialogViewModel(new PullRequestMergeRequest(12, "feature", "main"));

        Assert.True(model.IsSquash);
        Assert.False(model.IsMergeCommit);
        Assert.False(model.IsRebase);
        Assert.False(model.DeleteHeadBranch);

        var choice = model.ToChoice();
        Assert.Equal(PrMergeMethod.Squash, choice.Method);
        Assert.False(choice.DeleteBranch);
    }

    [Theory]
    [InlineData(PrMergeMethod.Merge)]
    [InlineData(PrMergeMethod.Rebase)]
    public void Picking_a_strategy_is_exclusive_and_reaches_the_choice(PrMergeMethod expected)
    {
        var model = new MergePullRequestDialogViewModel(new PullRequestMergeRequest(3, "feature", "main"))
        {
            IsMergeCommit = expected == PrMergeMethod.Merge,
            IsRebase = expected == PrMergeMethod.Rebase,
        };

        Assert.Equal(expected, model.ToChoice().Method);
        // The three radios are exclusive: picking one clears the others, squash included.
        Assert.Equal(expected == PrMergeMethod.Squash, model.IsSquash);
    }

    [Fact]
    public void The_delete_switch_only_affects_the_choice_when_the_user_turns_it_on()
    {
        var model = new MergePullRequestDialogViewModel(new PullRequestMergeRequest(9, "feature", "main"))
        {
            DeleteHeadBranch = true,
        };

        var choice = model.ToChoice();
        Assert.Equal(PrMergeMethod.Squash, choice.Method);
        Assert.True(choice.DeleteBranch);
    }
}
