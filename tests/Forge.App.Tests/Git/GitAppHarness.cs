using System.Text.Json;
using Forge.App.Composition;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.Workspace;

namespace Forge.App.Tests.Git;

/// <summary>
/// One throw-away data root, a real composition root with one open session restored, and the root view model over it.
/// Everything runs on the headless UI thread; dispose detaches the panels first so no refresh outlives the temp root.
/// </summary>
internal sealed class GitAppHarness : IAsyncDisposable
{
    private readonly string root;
    private readonly EnvironmentSnapshot saved;

    private GitAppHarness(string root, EnvironmentSnapshot saved)
    {
        this.root = root;
        this.saved = saved;
    }

    public AppComposition Composition { get; private set; } = null!;

    public MainWindowViewModel ViewModel { get; private set; } = null!;

    /// <summary>The temp data root, the three data-root variables redirected at it and a real composition root - no session.</summary>
    public static async Task<GitAppHarness> Open()
    {
        var harness = Begin();
        try
        {
            harness.Composition = AppComposition.Create();
        }
        catch (Exception)
        {
            await harness.DisposeAsync();
            throw;
        }

        return harness;
    }

    /// <summary>Seeds one session in <paramref name="sessionWorkDir"/> and restores it as the active session.</summary>
    public static async Task<GitAppHarness> OpenWithSession(string sessionWorkDir)
    {
        var harness = Begin();
        try
        {
            harness.Composition = AppComposition.Create();
            var info = await Task.Run(
                () => harness.Composition.Sessions.CreateSessionAsync(sessionWorkDir, "session", CancellationToken.None));
            var tabs = new OpenTabsSnapshot { Sessions = [info.Id], ActiveSession = info.Id };
            var json = JsonSerializer.Serialize(tabs, WorkbenchJsonContext.Default.OpenTabsSnapshot);
            await Task.Run(
                () => harness.Composition.UiState.PutAsync("open-tabs", OpenTabsSnapshot.CurrentVersion, json, CancellationToken.None));

            harness.ViewModel = new MainWindowViewModel(harness.Composition);
            await harness.ViewModel.InitializeAsync();
            return harness;
        }
        catch (Exception)
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>One fresh data root with the three data-root variables pointed at it and the previous values captured.</summary>
    private static GitAppHarness Begin()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-git-harness-");
        return new GitAppHarness(root, previous);
    }

    public void Detach(MainWindowViewModel viewModel)
    {
        viewModel.Files.Attach(PanelContext.Empty);
        viewModel.GitSync.Attach(PanelContext.Empty);
        viewModel.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (ViewModel is not null)
        {
            Detach(ViewModel);
        }

        if (Composition is not null)
        {
            await Composition.DisposeAsync();
        }

        saved.Restore();
        GitTestFiles.DeleteTree(root);
    }
}
