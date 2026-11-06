using System.Diagnostics;
using System.Text;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the new-session branch prompt for repository projects:
/// <see cref="ProjectTreeViewModel"/> asks for a branch name through the prompt callback with a title
/// and an initial value starting with <c>forge/</c>; cancel (null) or blank creates neither a session
/// nor a worktree; a typed name creates the worktree and <see cref="SessionInfo.Branch"/> equals it;
/// an invalid or existing name creates nothing and surfaces a non-empty localized message through the
/// existing failure notification path (<see cref="ProjectTreeViewModel.StatusText"/>).
/// Scratch/general sessions and sessions in an existing worktree never prompt.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectTreeBranchPromptTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task New_session_prompts_with_a_title_and_a_forge_prefixed_initial_value()
    {
        using var repo = IsoRepo.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var prompt = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>(null));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                return await WaitForPromptOnUiAsync(inner);
            },
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(prompt.Title));
        Assert.StartsWith("forge/", prompt.Initial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_leaves_no_session_and_no_worktree()
    {
        using var repo = IsoRepo.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var state = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>(null));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                await WaitForPromptOnUiAsync(inner);
                await Task.Delay(1000);
                return new
                {
                    Created = inner.Catalog.Created.Count,
                    Worktrees = await inner.Worktrees.ListAsync(repo.Root, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.Equal(0, state.Created);
        Assert.Empty(state.Worktrees);
    }

    [Fact]
    public async Task Blank_answer_leaves_no_session_and_no_worktree()
    {
        using var repo = IsoRepo.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var blankState = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>("   "));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                await WaitForPromptOnUiAsync(inner);
                await Task.Delay(1000);
                return new
                {
                    Created = inner.Catalog.Created.Count,
                    Worktrees = await inner.Worktrees.ListAsync(repo.Root, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.Equal(0, blankState.Created);
        Assert.Empty(blankState.Worktrees);
    }

    [Fact]
    public async Task Typed_name_creates_the_worktree_and_SessionInfo_Branch_equals_it()
    {
        using var repo = IsoRepo.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var typed = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>("feature/prompted"));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                await WaitForCreatedOnUiAsync(inner, 1);
                return new
                {
                    Created = inner.Catalog.Created[^1],
                    Worktrees = await inner.Worktrees.ListAsync(repo.Root, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.Equal("feature/prompted", typed.Created.Branch);
        Assert.NotNull(typed.Created.WorktreePath);
        Assert.True(Directory.Exists(typed.Created.WorktreePath));
        Assert.Equal(
            "feature/prompted",
            IsoGit.RunChecked(typed.Created.WorktreePath!, "branch", "--show-current").Trim());
        Assert.NotEmpty(typed.Worktrees);
    }

    [Fact]
    public async Task Invalid_name_creates_nothing_and_reports_a_localized_failure()
    {
        using var repo = IsoRepo.Create();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var badState = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>("bad..name"));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                var baseline = inner.Tree.StatusText;
                var status = await WaitForChangedStatusOnUiAsync(inner, baseline);
                return new
                {
                    Status = status,
                    Created = inner.Catalog.Created.Count,
                    Worktrees = await inner.Worktrees.ListAsync(repo.Root, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(badState.Status));
        Assert.Equal(0, badState.Created);
        Assert.DoesNotContain("bad..name", IsoGit.BranchList(repo.Root));
        Assert.Empty(badState.Worktrees);
    }

    [Fact]
    public async Task Existing_name_creates_nothing_and_reports_a_localized_failure()
    {
        using var repo = IsoRepo.Create();
        IsoGit.RunChecked(repo.Root, "branch", "taken");
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var takenState = await session.Dispatch(
            async () =>
            {
                var inner = NewTree(repo.Root, _ => Task.FromResult<string?>("taken"));
                await inner.Tree.RefreshAsync(CancellationToken.None);
                ProjectNode(inner).NewSessionCommand!.Execute(null);
                var baseline = inner.Tree.StatusText;
                var status = await WaitForChangedStatusOnUiAsync(inner, baseline);
                return new
                {
                    Status = status,
                    Created = inner.Catalog.Created.Count,
                    Worktrees = await inner.Worktrees.ListAsync(repo.Root, CancellationToken.None),
                };
            },
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(takenState.Status));
        Assert.Equal(0, takenState.Created);
        Assert.Empty(takenState.Worktrees);
    }

    [Fact]
    public async Task General_session_never_prompts()
    {
        var generalRoot = NewTempDir();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            var prompts = await session.Dispatch(
                async () =>
                {
                    var inner = NewGeneralTree(generalRoot);
                    await inner.Tree.RefreshAsync(CancellationToken.None);
                    var general = inner.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.General);
                    general.NewSessionCommand!.Execute(null);
                    await WaitForCreatedOnUiAsync(inner, 1);
                    return inner.Prompts.Count;
                },
                CancellationToken.None);

            Assert.Equal(0, prompts);
        }
        finally
        {
            DeleteTree(generalRoot);
        }
    }

    [Fact]
    public async Task Session_in_an_existing_worktree_never_prompts()
    {
        using var repo = IsoRepo.Create();
        var worktreeDir = Directory.CreateDirectory(Path.Combine(repo.Root, "linked")).FullName;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var outcome = await session.Dispatch(
            async () =>
            {
                var inner = NewTreeWithSession(repo.Root, worktreeDir, "main");
                await inner.Tree.RefreshAsync(CancellationToken.None);
                var worktree = inner.Tree.Sections
                    .SelectMany(static node => node.Children)
                    .Single(static node => node.Kind == TreeNodeKind.Worktree);
                worktree.NewSessionCommand!.Execute(null);
                await WaitForCreatedOnUiAsync(inner, 1);
                return new { Prompts = inner.Prompts.Count, Created = inner.Catalog.Created[^1] };
            },
            CancellationToken.None);

        Assert.Equal(0, outcome.Prompts);
        Assert.Equal(worktreeDir, outcome.Created.WorktreePath);
    }

    // ------------------------------------------------------------ harness

    private static TreeNodeViewModel ProjectNode(World world) =>
        world.Tree.Sections.Single(static node => node.Kind == TreeNodeKind.Project);

    private static World NewTree(string repositoryRoot, Func<(string Title, string Initial), Task<string?>> answer)
    {
        var prompts = new List<(string Title, string Initial)>();
        var catalog = new FakeCatalog([]);
        var worktrees = new WorktreeService(new GitCli());
        var tree = new ProjectTreeViewModel(
            new FakeRegistry(new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow)),
            catalog,
            worktrees,
            (_, _) => Task.CompletedTask,
            async factory => { await factory(CancellationToken.None); return true; },
            (title, initial) =>
            {
                prompts.Add((title, initial));
                return answer((title, initial));
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        return new World(tree, catalog, worktrees, prompts);
    }

    private static World NewGeneralTree(string generalRoot)
    {
        var prompts = new List<(string Title, string Initial)>();
        var catalog = new FakeCatalog([]);
        var worktrees = new WorktreeService(new GitCli());
        var tree = new ProjectTreeViewModel(
            new FakeRegistry(new ProjectInfo("general", "general", generalRoot, ProjectKind.General, 0, DateTimeOffset.UtcNow)),
            catalog,
            worktrees,
            (_, _) => Task.CompletedTask,
            async factory => { await factory(CancellationToken.None); return true; },
            (title, initial) =>
            {
                prompts.Add((title, initial));
                return Task.FromResult<string?>(null);
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        return new World(tree, catalog, worktrees, prompts);
    }

    private static World NewTreeWithSession(string repositoryRoot, string worktreePath, string branch)
    {
        var seed = new SessionInfo("seed-1", worktreePath, "seed", DateTimeOffset.UtcNow)
        {
            ProjectId = "proj-1",
            WorktreePath = worktreePath,
            Branch = branch,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        var prompts = new List<(string Title, string Initial)>();
        var catalog = new FakeCatalog([seed]);
        var worktrees = new WorktreeService(new GitCli());
        var tree = new ProjectTreeViewModel(
            new FakeRegistry(new ProjectInfo("proj-1", "repo", repositoryRoot, ProjectKind.Repository, 0, DateTimeOffset.UtcNow)),
            catalog,
            worktrees,
            (_, _) => Task.CompletedTask,
            async factory => { await factory(CancellationToken.None); return true; },
            (title, initial) =>
            {
                prompts.Add((title, initial));
                return Task.FromResult<string?>(null);
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        return new World(tree, catalog, worktrees, prompts);
    }

    /// <summary>The world behind one tree: the tree itself plus the fakes the test observes.</summary>
    private sealed class World(
        ProjectTreeViewModel tree,
        FakeCatalog catalog,
        WorktreeService worktrees,
        List<(string Title, string Initial)> prompts)
    {
        public ProjectTreeViewModel Tree { get; } = tree;

        public FakeCatalog Catalog { get; } = catalog;

        public WorktreeService Worktrees { get; } = worktrees;

        public List<(string Title, string Initial)> Prompts { get; } = prompts;
    }

    private sealed class FakeRegistry : IProjectRegistry
    {
        private readonly ProjectInfo[] projects;

        public FakeRegistry(params ProjectInfo[] projects) => this.projects = projects;

        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectInfo>>(projects);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(projects[0]);

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeCatalog(IReadOnlyList<SessionInfo> seed) : ISessionCatalog
    {
        public List<SessionCreate> Created { get; } = [];

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
        {
            Created.Add(request);
            return Task.FromResult(
                new SessionInfo(Guid.NewGuid().ToString("N"), request.WorkspacePath, request.Title, DateTimeOffset.UtcNow)
                {
                    ProjectId = request.ProjectId,
                    WorktreePath = request.WorktreePath,
                    Branch = request.Branch,
                    ParentSessionId = request.ParentSessionId,
                    LastActiveAt = DateTimeOffset.UtcNow,
                });
        }

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            if (query.ParentSessionId == string.Empty)
            {
                return Task.FromResult(seed);
            }

            return Task.FromResult<IReadOnlyList<SessionInfo>>([]);
        }

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) =>
            Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    // ------------------------------------------------------------ waits

    /// <summary>
    /// Waits for the prompt on the UI thread: the tree's continuations only drain while the dispatcher
    /// yields, so the poll must await on the UI thread itself.
    /// </summary>
    private static async Task<(string Title, string Initial)> WaitForPromptOnUiAsync(World world)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Prompts.Count == 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the branch prompt was never shown.");
            }

            await Task.Delay(PollInterval);
        }

        return world.Prompts[0];
    }

    private static async Task WaitForCreatedOnUiAsync(World world, int expected)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Catalog.Created.Count < expected)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"the expected session was never created; status text: '{world.Tree.StatusText}', "
                    + $"prompts: {world.Prompts.Count}");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Waits for <see cref="ProjectTreeViewModel.StatusText"/> to change away from <paramref name="baseline"/>:
    /// a refresh already sets it (e.g. the empty-projects hint), so only a change proves the failure path ran.
    /// </summary>
    private static async Task<string> WaitForChangedStatusOnUiAsync(World world, string baseline)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (world.Tree.StatusText == baseline || string.IsNullOrWhiteSpace(world.Tree.StatusText))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("no failure message surfaced through StatusText.");
            }

            await Task.Delay(PollInterval);
        }

        return world.Tree.StatusText;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-tree-prompt-" + Guid.NewGuid().ToString("N"));
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
    private static class IsoGit
    {
        private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "forge-tree-prompt-tests", Guid.NewGuid().ToString("n"));
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

        public static IReadOnlyList<string> BranchList(string repository) =>
            RunChecked(repository, "branch", "--list", "--format=%(refname:short)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .ToList();
    }

    /// <summary>Throwaway repository on branch main with pinned identity; global config stays out via IsoGit.</summary>
    private sealed class IsoRepo : IDisposable
    {
        private readonly TempDirectory sandbox;

        private IsoRepo(TempDirectory sandbox, string root)
        {
            this.sandbox = sandbox;
            Root = root;
        }

        public string Root { get; }

        public static IsoRepo Create()
        {
            var sandbox = new TempDirectory();
            var root = Path.Combine(sandbox.Path, "repo");
            Directory.CreateDirectory(root);
            IsoGit.RunChecked(sandbox.Path, "init", "-q", "-b", "main", root);
            IsoGit.RunChecked(root, "config", "--local", "user.name", "Forge Test");
            IsoGit.RunChecked(root, "config", "--local", "user.email", "forge@example.com");
            IsoGit.RunChecked(root, "config", "--local", "core.autocrlf", "false");
            IsoGit.RunChecked(root, "config", "--local", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(root, "README.md"), "# repo\n", new UTF8Encoding(false));
            IsoGit.RunChecked(root, "add", "-A");
            IsoGit.RunChecked(root, "commit", "-q", "-m", "initial");
            return new IsoRepo(sandbox, root);
        }

        public void Dispose() => sandbox.Dispose();
    }

    /// <summary>Throwaway temp directory that resolves symlinks like the workspace tests do.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            var requested = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-tree-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(requested);
            Path = requested;
        }

        public string Path { get; }

        public void Dispose() => DeleteTree(Path);
    }
}
