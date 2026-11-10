using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Workbench;
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
    private static readonly TimeSpan IdleBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task The_title_bar_branch_follows_the_active_session_and_clears_with_none()
    {
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await using var harness = await AppHarness.OpenWithSession(sessionDir);
                var viewModel = harness.ViewModel;

                Assert.NotNull(viewModel.ActiveSession);
                Assert.Equal(string.Empty, viewModel.ActiveBranchText);
                Assert.False(viewModel.HasActiveBranch);

                viewModel.GitPanel.Attach(new PanelContext(viewModel.ActiveSession!.SessionId, work, null));
                await WaitIdleAsync(viewModel);
                Assert.Equal("main", viewModel.ActiveBranchText);
                Assert.True(viewModel.HasActiveBranch);

                viewModel.GitPanel.Attach(PanelContext.Empty);
                await WaitIdleAsync(viewModel);
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
        using var sandbox = GitSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "file.txt", "v1\n", "initial");
        GitShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        var sessionDir = Directory.CreateDirectory(Path.Combine(sandbox.Root, "session-work")).FullName;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                await using var harness = await AppHarness.OpenWithSession(sessionDir);
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
                viewModel.GitPanel.Attach(new PanelContext(viewModel.ActiveSession!.SessionId, work, null));
                await WaitIdleAsync(viewModel);

                Assert.Equal(viewModel.GitPanel.AheadBehindText, viewModel.Status.AheadBehindText);
                Assert.Equal(
                    Loc.Instance.Format(GitStrings.AheadBehind, 1, 0),
                    viewModel.Status.AheadBehindText);
                Assert.True(viewModel.Status.HasAheadBehind);

                harness.Detach(viewModel);
                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Git_panel_sits_in_the_inspector_with_its_view_model_and_title_bar_tools()
    {
        var plain = NewTempDir();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            var (region, gitContent, gitTools) = await session.Dispatch(
                async () =>
                {
                    await using var harness = await AppHarness.OpenWithSession(plain);
                    var workbench = harness.ViewModel.Workbench;
                    var where = workbench.GitPanel!.Region;
                    var content = workbench.GitPanel?.Content?.GetType().FullName;
                    var git = harness.ViewModel.GitPanel;
                    var tools = workbench.GitPanel!.HeaderTools.OfType<PanelToolButton>()
                        .Select(tool => (tool.Id, tool.IconKey, Bound: ReferenceEquals(tool.Command, tool.Id switch
                        {
                            "git-fetch" => git.FetchCommand,
                            "git-pull" => git.PullCommand,
                            "git-push" => git.PushCommand,
                            _ => git.RefreshCommand,
                        })))
                        .ToList();
                    harness.Detach(harness.ViewModel);
                    return (where, content, tools);
                },
                CancellationToken.None);

            Assert.Equal(RegionRole.Inspector, region);
            Assert.Equal(typeof(Forge.App.Git.GitPanelViewModel).FullName, gitContent);
            // Fetch, pull, push and refresh are icon tools in the Git panel's title bar, each bound to its command.
            Assert.Equal(new[] { "git-fetch", "git-pull", "git-push", "git-refresh" }, gitTools.Select(static tool => tool.Id));
            Assert.Equal(new[] { "IconGitFetch", "IconGitPull", "IconGitPush", "IconRefresh" }, gitTools.Select(static tool => tool.IconKey));
            Assert.All(gitTools, static tool => Assert.True(tool.Bound, tool.Id));
        }
        finally
        {
            DeleteTree(plain);
        }
    }

    // ------------------------------------------------------------ harness

    private static async Task WaitIdleAsync(MainWindowViewModel viewModel)
    {
        var deadline = DateTime.UtcNow + IdleBudget;
        while (viewModel.GitPanel.IsBusy)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the git panel stayed busy past the 30 s budget");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// One throw-away data root, a real composition root with one open session restored, and the root
    /// view model over it. Everything runs on the headless UI thread; dispose detaches the panels first
    /// so no watcher or refresh outlives the temp root.
    /// </summary>
    private sealed class AppHarness : IAsyncDisposable
    {
        private readonly string root;
        private readonly (string? Data, string? General, string? Workspace) saved;

        private AppHarness(string root, (string? Data, string? General, string? Workspace) saved)
        {
            this.root = root;
            this.saved = saved;
        }

        public AppComposition Composition { get; private set; } = null!;

        public MainWindowViewModel ViewModel { get; private set; } = null!;

        /// <summary>Seeds one session in <paramref name="sessionWorkDir"/> and restores it as the active session.</summary>
        public static async Task<AppHarness> OpenWithSession(string sessionWorkDir)
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-git-status-" + Guid.NewGuid().ToString("N"));
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

            var harness = new AppHarness(root, saved);
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
            DeleteTree(root);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-git-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Isolated git runner: per-process empty global/system stubs so the developer's config never leaks in.</summary>
    private static class GitShell
    {
        private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "forge-git-status-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            var global = Path.Combine(dir, "global.config");
            var system = Path.Combine(dir, "system.config");
            File.WriteAllText(global, string.Empty, new UTF8Encoding(false));
            File.WriteAllText(system, string.Empty, new UTF8Encoding(false));
            return (global, system);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string RunChecked(string directory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[]
            {
                "-c", "user.name=Forge Test",
                "-c", "user.email=forge@example.com",
                "-c", "core.autocrlf=false",
                "-c", "core.safecrlf=false",
                "-c", "init.defaultBranch=main",
                "-c", "commit.gpgsign=false",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
            startInfo.Environment["GIT_PAGER"] = "cat";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Stubs.Value.Global;
            startInfo.Environment["GIT_CONFIG_SYSTEM"] = Stubs.Value.System;
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr.Result}");
            }

            return stdout.Result;
        }
    }

    /// <summary>A bare <c>origin.git</c> plus clones with pinned local identity.</summary>
    private sealed class GitSandbox : IDisposable
    {
        private GitSandbox(string root, string origin)
        {
            Root = root;
            Origin = origin;
        }

        public string Root { get; }

        public string Origin { get; }

        public static GitSandbox Create()
        {
            var root = NewTempDir();
            var origin = Path.Combine(root, "origin.git");
            GitShell.RunChecked(root, "init", "--bare", "-q", "-b", "main", origin);
            return new GitSandbox(root, origin);
        }

        public string Clone(string name)
        {
            var path = Path.Combine(Root, name);
            GitShell.RunChecked(Root, "clone", "-q", Origin, path);
            GitShell.RunChecked(path, "config", "--local", "user.name", "Forge Test");
            GitShell.RunChecked(path, "config", "--local", "user.email", "forge@example.com");
            GitShell.RunChecked(path, "config", "--local", "core.autocrlf", "false");
            GitShell.RunChecked(path, "config", "--local", "commit.gpgsign", "false");
            return path;
        }

        public void CommitFile(string repository, string relativePath, string content, string message)
        {
            var full = Path.Combine(repository, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            GitShell.RunChecked(repository, "add", "-A");
            GitShell.RunChecked(repository, "commit", "-q", "-m", message);
        }

        public void Dispose() => DeleteTree(Root);
    }
}
