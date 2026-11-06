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
    private readonly (string? Data, string? General, string? Workspace) saved;

    private GitAppHarness(string root, (string? Data, string? General, string? Workspace) saved)
    {
        this.root = root;
        this.saved = saved;
    }

    public AppComposition Composition { get; private set; } = null!;

    public MainWindowViewModel ViewModel { get; private set; } = null!;

    /// <summary>Seeds one session in <paramref name="sessionWorkDir"/> and restores it as the active session.</summary>
    public static async Task<GitAppHarness> OpenWithSession(string sessionWorkDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-git-harness-" + Guid.NewGuid().ToString("N"));
        foreach (var directory in new[] { "data", "workspace", "general" })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }

        var saved = (
            Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
            General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
            Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));

        var harness = new GitAppHarness(root, saved);
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

    public void Detach(MainWindowViewModel viewModel)
    {
        viewModel.Files.Attach(PanelContext.Empty);
        viewModel.GitPanel.Attach(PanelContext.Empty);
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

        Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
        GitTestFiles.DeleteTree(root);
    }
}
