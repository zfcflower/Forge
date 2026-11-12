using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Git;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="NewBranchDialogViewModel"/> without any UI session (the dialog is plain view models over
/// <see cref="GitCli"/>): the suggested name is prefilled but Create stays disabled while the branch list loads;
/// an empty, invalid or taken name each reports its localized hint and leaves Create disabled; a valid unused name
/// with a selected start point enables Create with the chosen ref and the trimmed name; and the dialog's picker
/// lists the checked-out branch while the changes panel's picker hides it.
/// </summary>
public sealed class NewBranchDialogViewModelTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void Opens_with_the_suggested_name_prefilled_and_create_disabled_while_loading()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest("/repo", "forge/session-0101-120000"),
            CancellationToken.None);

        Assert.Equal("forge/session-0101-120000", dialog.BranchName);
        Assert.False(dialog.CanCreate);
        Assert.Equal(ProjectStrings.NewBranchLoading, dialog.Hint);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public async Task Empty_name_reports_the_required_hint()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("repo");

        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest(work, "forge/session-0101-120000"),
            CancellationToken.None);
        await dialog.InitializeAsync();

        dialog.BranchName = "   ";
        await WaitUntilAsync(
            () => string.Equals(dialog.Hint, ProjectStrings.NewBranchNameRequired, StringComparison.Ordinal),
            "the required-name hint");

        Assert.False(dialog.CanCreate);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public async Task Invalid_name_reports_the_invalid_hint()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("repo");

        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest(work, "forge/session-0101-120000"),
            CancellationToken.None);
        await dialog.InitializeAsync();

        dialog.BranchName = "bad..name";
        await WaitUntilAsync(
            () => string.Equals(dialog.Hint, ProjectStrings.NewBranchNameInvalid, StringComparison.Ordinal),
            "the invalid-name hint");

        Assert.False(dialog.CanCreate);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public async Task Taken_name_reports_the_taken_hint()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("repo");

        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest(work, "forge/session-0101-120000"),
            CancellationToken.None);
        await dialog.InitializeAsync();
        Assert.Contains(dialog.Picker.LocalBranches, static branch => branch.Name == "main");

        dialog.BranchName = "main";
        await WaitUntilAsync(
            () => string.Equals(dialog.Hint, ProjectStrings.NewBranchNameTaken, StringComparison.Ordinal),
            "the taken-name hint");

        Assert.False(dialog.CanCreate);
        Assert.Null(dialog.Result);
    }

    [Fact]
    public async Task Valid_unused_name_with_a_start_point_enables_create_with_the_trimmed_name()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("repo");

        using var _ = new LanguageScope(UiLanguage.Chinese);
        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest(work, "forge/session-0101-120000"),
            CancellationToken.None);
        await dialog.InitializeAsync();
        Assert.NotNull(dialog.Picker.SelectedBranch);

        dialog.BranchName = "  forge/dialog-ok  ";
        await WaitUntilAsync(() => dialog.CanCreate, "Create to become available");

        Assert.Null(dialog.Hint);
        var result = dialog.Result;
        Assert.NotNull(result);
        Assert.Equal(dialog.Picker.SelectedBranch!.RefName, result!.StartPoint);
        Assert.Equal("forge/dialog-ok", result!.BranchName);
    }

    [Fact]
    public async Task Dialog_picker_lists_the_current_branch_while_the_changes_picker_hides_it()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("repo");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/session");

        using var dialog = new NewBranchDialogViewModel(
            new GitCli(),
            new NewBranchRequest(work, "forge/session-0101-120000"),
            CancellationToken.None);
        await dialog.InitializeAsync();
        Assert.Contains(dialog.Picker.LocalBranches, static branch => branch.Name == "feature/session");

        using var changes = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
        changes.Attach(new PanelContext("dialog-picker-session", work, null));
        await changes.LoadAsync(CancellationToken.None);
        try
        {
            Assert.DoesNotContain(changes.LocalBranches, static branch => branch.Name == "feature/session");
        }
        finally
        {
            changes.Dispose();
        }

        dialog.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("timed out waiting for " + what + ".");
            }

            await Task.Delay(PollInterval);
        }
    }

}
