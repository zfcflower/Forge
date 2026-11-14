using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Tests.Git;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The add-project flow of the shell (wave W9) against the real composition root and a recording dialog host: the picker
/// offers the three sources with the shell's own texts, an existing folder that is not a repository is offered
/// <c>git init</c> first (and only a confirmed one is registered), the creation dialog's answer is registered when it names a
/// directory and nothing is registered when it does not (a cancelled dialog, a closed picker), and every registration
/// refreshes the project tree so the new row appears without a manual refresh.
/// </summary>
[Collection("forge-app-ui")]
public sealed class AddProjectFlowTests
{
    [Fact]
    public async Task An_existing_folder_is_initialized_after_confirmation_and_registered()
    {
        await WithShellAsync(async shell =>
        {
            using var sandbox = new ProjectSandbox();
            var folder = sandbox.Combine("plain-folder");
            Directory.CreateDirectory(folder);
            shell.Dialogs.PickAnswers.Enqueue(0);
            shell.Dialogs.FolderAnswer = folder;
            shell.Dialogs.ConfirmAnswers.Enqueue(true);

            shell.Shell.AddProjectCommand.Execute(null);

            var project = await WaitForProjectAsync(shell, folder);
            await WaitForRowAsync(shell, project);

            // The picker is the shell's, and the confirmation is about running git init in a folder that is not a repository.
            var pick = Assert.Single(shell.Dialogs.Picks);
            Assert.Equal(ProjectFlowStrings.AddTitle, pick.Title);
            Assert.Equal(ProjectFlowStrings.AddQuestion, pick.Message);
            Assert.Equal(
                [ProjectFlowStrings.AddExisting, ProjectFlowStrings.AddClone, ProjectFlowStrings.AddCreate],
                pick.Entries.Select(static entry => entry.Label));

            var confirm = Assert.Single(shell.Dialogs.Confirms);
            Assert.Equal(ShellStrings.NotRepositoryTitle, confirm.Title);
            Assert.Equal(ShellStrings.NotRepositoryMessage(folder), confirm.Message);
            Assert.Equal(ShellStrings.GitInit, confirm.ConfirmText);
            Assert.Equal(ShellStrings.Cancel, confirm.CancelText);
            Assert.Equal(ShellStrings.PickRepositoryTitle, Assert.Single(shell.Dialogs.FolderTitles));

            // The folder became a repository and the project tree was rebuilt around the registry, so the row exists already.
            Assert.True(await new GitCli().IsRepositoryAsync(folder).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains(shell.Shell.Projects.Sections, node => node.Key == "project:" + project.Id);
        });
    }

    [Fact]
    public async Task Declining_the_git_init_registers_nothing()
    {
        await WithShellAsync(async shell =>
        {
            using var sandbox = new ProjectSandbox();
            var folder = sandbox.Combine("plain-folder");
            Directory.CreateDirectory(folder);
            shell.Dialogs.PickAnswers.Enqueue(0);
            shell.Dialogs.FolderAnswer = folder;
            shell.Dialogs.ConfirmAnswers.Enqueue(false);

            shell.Shell.AddProjectCommand.Execute(null);
            await WaitForAsync(() => shell.Dialogs.Confirms.Count == 1, "the git init confirmation");

            Assert.Single(shell.Dialogs.Confirms);
            Assert.Equal(ProjectKind.General, Assert.Single(await shell.Composition.Projects.ListAsync(CancellationToken.None)).Kind);
            Assert.False(await new GitCli().IsRepositoryAsync(folder).WaitAsync(TimeSpan.FromSeconds(30)));
        });
    }

    [Fact]
    public async Task A_created_project_is_registered()
    {
        await WithShellAsync(async shell =>
        {
            using var sandbox = new ProjectSandbox();
            var created = sandbox.Combine("new-project");
            GitTestShell.RunChecked(sandbox.Path, "init", "-q", "-b", "main", created);
            shell.Dialogs.PickAnswers.Enqueue(2);
            shell.Dialogs.CreationAnswer = created;

            shell.Shell.AddProjectCommand.Execute(null);

            var project = await WaitForProjectAsync(shell, created);
            await WaitForRowAsync(shell, project);

            // The dialog is the clone/new-project one, and the shell takes its answer as the folder to register.
            var model = Assert.Single(shell.Dialogs.Creations).Model;
            Assert.Equal(ProjectCreationMode.Create, model.Mode);
            Assert.Contains(shell.Shell.Projects.Sections, node => node.Key == "project:" + project.Id);
        });
    }

    [Fact]
    public async Task A_clone_the_dialog_answers_is_registered()
    {
        await WithShellAsync(async shell =>
        {
            using var sandbox = new ProjectSandbox();
            using var source = GitTestSandbox.Create();
            var cloned = source.CloneTracked("cloned");
            shell.Dialogs.PickAnswers.Enqueue(1);
            shell.Dialogs.CreationAnswer = cloned;

            shell.Shell.AddProjectCommand.Execute(null);

            var project = await WaitForProjectAsync(shell, cloned);
            await WaitForRowAsync(shell, project);

            var model = Assert.Single(shell.Dialogs.Creations).Model;
            Assert.Equal(ProjectCreationMode.Clone, model.Mode);
            Assert.Contains(shell.Shell.Projects.Sections, node => node.Key == "project:" + project.Id);
        });
    }

    [Fact]
    public async Task A_cancelled_creation_dialog_registers_nothing()
    {
        await WithShellAsync(async shell =>
        {
            using var sandbox = new ProjectSandbox();
            var prepared = sandbox.Combine("prepared");
            GitTestShell.RunChecked(sandbox.Path, "init", "-q", "-b", "main", prepared);
            shell.Dialogs.PickAnswers.Enqueue(2);
            shell.Dialogs.CreationAnswer = null;

            shell.Shell.AddProjectCommand.Execute(null);
            SharedUi.Settle();

            Assert.Single(shell.Dialogs.Creations);
            Assert.Equal(ProjectKind.General, Assert.Single(await shell.Composition.Projects.ListAsync(CancellationToken.None)).Kind);
            Assert.DoesNotContain(shell.Shell.Projects.Sections, static node => node.Key.StartsWith("project:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_closed_picker_changes_nothing()
    {
        await WithShellAsync(async shell =>
        {
            shell.Dialogs.PickAnswers.Enqueue(-1);

            shell.Shell.AddProjectCommand.Execute(null);
            SharedUi.Settle();

            Assert.Single(shell.Dialogs.Picks);
            Assert.Empty(shell.Dialogs.FolderTitles);
            Assert.Empty(shell.Dialogs.Creations);
            Assert.Equal(ProjectKind.General, Assert.Single(await shell.Composition.Projects.ListAsync(CancellationToken.None)).Kind);
        });
    }

    // ---------------------------------------------------------------- the real composition root

    private static async Task WithShellAsync(Func<ProjectShell, Task> body)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                await using var shell = await ProjectShell.CreateAsync();
                await body(shell);
                return 0;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Waits until the project tree shows <paramref name="project"/>: the registration writes the row first and refreshes the
    /// tree after it, so a tree assertion made right after the registry read would be one refresh too early.
    /// </summary>
    private static Task WaitForRowAsync(ProjectShell shell, ProjectInfo project) =>
        WaitForAsync(
            () => shell.Shell.Projects.Sections.Any(node => node.Key == "project:" + project.Id),
            "the project row in the tree");

    /// <summary>A bounded wait over a condition that the flow satisfies on the dispatcher.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Waits until <paramref name="directory"/> is registered and returns its row; the flow registers on the UI thread, so
    /// the wait yields to the dispatcher instead of blocking it.
    /// </summary>
    private static async Task<ProjectInfo> WaitForProjectAsync(ProjectShell shell, string directory)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var projects = await shell.Composition.Projects.ListAsync(CancellationToken.None);
            var match = projects.FirstOrDefault(project =>
                string.Equals(Path.GetFullPath(project.RootPath), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for the project to be registered: " + directory);
            await Task.Delay(10);
        }
    }

    private sealed class ProjectShell : IAsyncDisposable
    {
        private readonly string root;
        private readonly EnvironmentSnapshot previous;

        private ProjectShell(string root, EnvironmentSnapshot previous, AppComposition composition, MainWindowViewModel shell, RecordingDialogs dialogs)
        {
            this.root = root;
            this.previous = previous;
            Composition = composition;
            Shell = shell;
            Dialogs = dialogs;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel Shell { get; }

        public RecordingDialogs Dialogs { get; }

        public static async Task<ProjectShell> CreateAsync()
        {
            var (root, previous) = TempDataRoot.Redirect("forge-add-project-");
            var composition = AppComposition.Create();
            var shell = new MainWindowViewModel(composition);
            var dialogs = new RecordingDialogs();
            shell.AttachDialogs(dialogs);
            await shell.InitializeAsync();
            return new ProjectShell(root, previous, composition, shell, dialogs);
        }

        public async ValueTask DisposeAsync()
        {
            Shell.Dispose();
            await Composition.DisposeAsync();
            SqliteConnection.ClearAllPools();
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    private sealed class ProjectSandbox : IDisposable
    {
        public ProjectSandbox() => Path = TestTempFiles.NewDirectory("forge-add-project-dirs-");

        public string Path { get; }

        public string Combine(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => TestTempFiles.DeleteTree(Path);
    }

    private sealed record PickCall(string Title, string Message, IReadOnlyList<PickEntry> Entries);

    private sealed record ConfirmCall(string Title, string Message, string ConfirmText, string CancelText);

    private sealed record CreationCall(ProjectCreationViewModel Model);

    /// <summary>
    /// Answers every dialog from a queue (closed / cancelled when empty) and records what was asked, so the flow's texts and
    /// its decisions can both be asserted.
    /// </summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public Queue<int> PickAnswers { get; } = new();

        public Queue<bool> ConfirmAnswers { get; } = new();

        public List<PickCall> Picks { get; } = [];

        public List<ConfirmCall> Confirms { get; } = [];

        public List<string> FolderTitles { get; } = [];

        public List<CreationCall> Creations { get; } = [];

        /// <summary>What the folder picker answers; null means the user closed it.</summary>
        public string? FolderAnswer { get; set; }

        /// <summary>What the clone/new-project dialog answers; null means the user cancelled.</summary>
        public string? CreationAnswer { get; set; }

        public Task<string?> PickFolderAsync(string title)
        {
            FolderTitles.Add(title);
            return Task.FromResult(FolderAnswer);
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
        {
            Confirms.Add(new ConfirmCall(title, message, confirmText, cancelText));
            return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
        }

        public Task<string?> PromptTextAsync(string title, string initial) => Task.FromResult<string?>(null);

        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) =>
            Task.FromResult<NewBranchChoice?>(null);

        public Task<int> PickAsync(string title, string message, IReadOnlyList<PickEntry> entries)
        {
            Picks.Add(new PickCall(title, message, entries));
            return Task.FromResult(PickAnswers.Count > 0 ? PickAnswers.Dequeue() : -1);
        }

        public Task<string?> RunProjectCreationAsync(ProjectCreationViewModel model, CancellationToken ct)
        {
            Creations.Add(new CreationCall(model));
            return Task.FromResult(CreationAnswer);
        }
    }
}