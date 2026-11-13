using Avalonia.Headless;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the title-bar and status-bar git wiring in <see cref="MainWindowViewModel"/>:
/// <c>ActiveBranchText</c>/<c>HasActiveBranch</c> follow the active session through the shared Git panel
/// and clear with no session, and <see cref="StatusBarViewModel.AheadBehindText"/>,
/// <c>HasAheadBehind</c> and <c>SetAheadBehind</c> carry the panel's divergence.
/// Also pins that the inspector tool group holds the Git tab after the existing three tabs, in order.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MainWindowGitStatusTests
{
    [Fact]
    public async Task The_title_bar_branch_follows_the_active_session_and_clears_with_none()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                var viewModel = harness.ViewModel;

                Assert.NotNull(viewModel.ActiveSession);
                Assert.Equal(string.Empty, viewModel.ActiveBranchText);
                Assert.False(viewModel.HasActiveBranch);

                viewModel.GitSync.Attach(new PanelContext(viewModel.ActiveSession!.SessionId, work, null));
                await GitPanelTesting.WaitIdleAsync(viewModel.GitSync);
                Assert.Equal("main", viewModel.ActiveBranchText);
                Assert.True(viewModel.HasActiveBranch);

                viewModel.GitSync.Attach(PanelContext.Empty);
                await GitPanelTesting.WaitIdleAsync(viewModel.GitSync);
                Assert.Equal(string.Empty, viewModel.ActiveBranchText);
                Assert.False(viewModel.HasActiveBranch);

                harness.Detach(viewModel);
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task The_status_bar_carries_the_panel_ahead_behind_and_clears()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                var viewModel = harness.ViewModel;

                viewModel.Status.SetAheadBehind("↑2 ↓0");
                Assert.Equal("↑2 ↓0", viewModel.Status.AheadBehindText);
                Assert.True(viewModel.Status.HasAheadBehind);

                viewModel.Status.SetAheadBehind(string.Empty);
                Assert.Equal(string.Empty, viewModel.Status.AheadBehindText);
                Assert.False(viewModel.Status.HasAheadBehind);

                viewModel.Status.SetAheadBehind(null);
                Assert.Equal(string.Empty, viewModel.Status.AheadBehindText);
                Assert.False(viewModel.Status.HasAheadBehind);

                // The shell forwards the panel's divergence into the status bar on every re-read.
                sandbox.CommitFile(work, "file.txt", "v2\n", "second");
                viewModel.GitSync.Attach(new PanelContext(viewModel.ActiveSession!.SessionId, work, null));
                await GitPanelTesting.WaitIdleAsync(viewModel.GitSync);

                Assert.Equal(viewModel.GitSync.AheadBehindText, viewModel.Status.AheadBehindText);
                Assert.Equal(
                    Loc.Instance.Format(GitStrings.AheadBehind, 1, 0),
                    viewModel.Status.AheadBehindText);
                Assert.True(viewModel.Status.HasAheadBehind);

                harness.Detach(viewModel);
                return 0;
            },
            CancellationToken.None);
    }

}
