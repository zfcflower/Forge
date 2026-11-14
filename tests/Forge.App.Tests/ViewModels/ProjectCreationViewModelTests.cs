using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The add-project dialog's model (wave W9): the folder name follows the remote address until the user types one, the hint
/// names the first input that is not usable yet and keeps the primary button disabled, a run reports its outcome once
/// (completed / failed / cancelled), a second click while a run is going joins it instead of starting another, and a new
/// repository has no progress to show.
/// <para>Every test runs on the shared headless UI thread: the model hands clone progress to the dispatcher and its
/// continuations have to land back on the context it was built on.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectCreationViewModelTests
{
    [Fact]
    public async Task The_name_follows_the_url_until_the_user_types_one()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(
                ProjectCreationMode.Clone,
                Service(sandbox.Path),
                "https://host/team/repo");
            Assert.Equal("repo", model.Name);

            model.Url = "https://host/team/other";
            Assert.Equal("other", model.Name);

            // The suggestion keeps following the address as long as it is the suggestion.
            model.Url = "git@host:team/third";
            Assert.Equal("third", model.Name);

            // Once the folder name is the user's own, another address must not overwrite it.
            model.Name = "my-name";
            model.Url = "https://host/team/fourth";
            Assert.Equal("my-name", model.Name);
        });
    }

    [Fact]
    public async Task An_empty_parent_directory_is_the_first_hint_and_blocks_the_start()
    {
        await SharedUi.RunAsync(() =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));
            model.Name = "fresh";
            Assert.Null(model.Hint);
            Assert.True(model.CanStart);

            model.ParentDirectory = "   ";

            Assert.Equal(ProjectCreationStrings.ParentDirectoryMissing.Resolve(), model.Hint);
            Assert.True(model.HasHint);
            Assert.False(model.CanStart);
            Assert.False(model.StartCommand.CanExecute(null));
            Assert.Equal(string.Empty, model.DestinationText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_unusable_name_is_named_in_the_hint_and_blocks_the_start()
    {
        await SharedUi.RunAsync(() =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));

            model.Name = "a/b";

            Assert.Equal(ProjectCreationStrings.NameProblem(ProjectNameProblem.InvalidCharacters).Resolve(), model.Hint);
            Assert.False(model.CanStart);
            Assert.False(model.StartCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_unusable_remote_is_named_in_the_hint_while_the_folder_name_is_accepted()
    {
        await SharedUi.RunAsync(() =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Clone, Service(sandbox.Path));

            // The name is fine, so the remote is the input the hint has to blame.
            model.Name = "repo";
            model.Url = "not-an-address";

            Assert.Equal(ProjectCreationStrings.UrlNotUsable.Resolve(), model.Hint);
            Assert.False(model.CanStart);

            model.Url = "https://host/team/repo.git";

            Assert.Null(model.Hint);
            Assert.True(model.CanStart);
            Assert.True(model.StartCommand.CanExecute(null));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Complete_input_shows_the_destination_and_lets_the_start_run()
    {
        await SharedUi.RunAsync(() =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));

            model.Name = "fresh";

            Assert.Null(model.Hint);
            Assert.False(model.HasHint);
            Assert.True(model.CanStart);
            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "fresh")), model.DestinationText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_successful_run_fills_the_created_directory_and_raises_completed_once()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));
            var completed = 0;
            model.Completed += (_, _) => completed++;

            model.Name = "fresh";
            Assert.False(model.IsRunning);
            Assert.Equal(ProjectCreationStrings.CreateStart, model.StartText);
            Assert.Equal(ProjectCreationStrings.Cancel, model.CancelText);

            var started = await model.StartAsync();

            Assert.True(started);
            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "fresh")), model.CreatedDirectory);
            Assert.Equal(1, completed);
            Assert.False(model.IsRunning);
            Assert.Null(model.Error);
            Assert.False(model.CanStart);

            // A completed run is not run twice: the answer is already known.
            Assert.True(await model.StartAsync());
            Assert.Equal(1, completed);
            Assert.True(Directory.Exists(model.CreatedDirectory));
        });
    }

    [Fact]
    public async Task A_failed_run_shows_the_reason_and_can_be_retried()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var source = GitTestSandbox.Create();
            var repository = source.CloneTracked("source");

            // The source lives in a subdirectory: an address that pointed at the clone's own destination would make git find
            // an (empty) repository where it just created the folder.
            var missing = new Uri(Path.Combine(sandbox.Path, "sources", "no-such-repository")).AbsoluteUri;
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Clone, Service(sandbox.Path), missing);
            var completed = 0;
            model.Completed += (_, _) => completed++;

            Assert.Equal("no-such-repository", model.Name);
            Assert.False(await model.StartAsync());

            Assert.False(string.IsNullOrWhiteSpace(model.Error));
            Assert.Null(model.CreatedDirectory);
            Assert.Equal(0, completed);
            Assert.False(model.IsRunning);
            Assert.True(model.HasError);
            Assert.True(model.CanStart, "a failed run has to leave the input usable");

            // The retry is a real second attempt: another address, another run, and this time it lands.
            model.Url = FileUrl(repository);
            Assert.True(await model.StartAsync());

            Assert.Equal(1, completed);
            Assert.Null(model.Error);
            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "source")), model.CreatedDirectory);
        });
    }

    [Fact]
    public async Task A_cancelled_run_reports_the_cancel_text_and_does_not_complete()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));
            var completed = 0;
            model.Completed += (_, _) => completed++;
            model.Name = "stopped";

            var run = model.StartAsync();
            model.Cancel();
            Assert.False(await run);

            Assert.Equal(ProjectCreationStrings.Cancelled.Resolve(), model.Error);
            Assert.Null(model.CreatedDirectory);
            Assert.Equal(0, completed);
            Assert.False(model.IsRunning);
            Assert.False(Directory.Exists(Path.Combine(sandbox.Path, "stopped")), "a stopped creation must clean up after itself");
        });
    }

    [Fact]
    public async Task A_second_start_while_one_runs_joins_that_run_instead_of_stacking_another()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));
            model.Name = "single-flight";

            // The service is sealed with non-virtual methods, so no counting fake can be injected; the observable contract is
            // the one asserted instead: the second call hands back the very same run, and one folder is created.
            var first = model.StartAsync();
            var second = model.StartAsync();

            Assert.Same(first, second);
            Assert.True(await first);
            Assert.Same(first, second);
            Assert.Single(Directory.EnumerateDirectories(sandbox.Path));
            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "single-flight")), model.CreatedDirectory);
        });
    }

    [Fact]
    public async Task A_new_repository_shows_no_progress_while_it_runs()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var sandbox = new ProjectSandbox();
            using var model = new ProjectCreationViewModel(ProjectCreationMode.Create, Service(sandbox.Path));
            model.Name = "quiet";

            var run = model.StartAsync();

            // A new repository has nothing git could report, so the progress line stays empty and the footer offers "Stop"
            // while there is still something to stop. Whether the bar is indeterminate is not asserted here: without a
            // progress frame there is no phase to be indeterminate about.
            Assert.True(model.IsRunning);
            Assert.Equal(string.Empty, model.ProgressText);
            Assert.Equal(ProjectCreationStrings.Stop, model.CancelText);
            Assert.True(model.CancelCommand.CanExecute(null), "a running creation is what the stop button is for");

            Assert.True(await run);

            Assert.Equal(string.Empty, model.ProgressText);
            Assert.Equal(ProjectCreationStrings.Cancel, model.CancelText);
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A real service over a throw-away parent and the live default preferences: the model's rules, retries and cleanup are
    /// the service's, so a fake of it would test a different dialog.
    /// </summary>
    private static ProjectCreationService Service(string parent) =>
        new(new GitCli(), new AppPreferences { ProjectsCloneDirectory = parent }, parent);

    private static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    internal sealed class ProjectSandbox : IDisposable
    {
        public ProjectSandbox() => Path = TestTempFiles.NewDirectory("forge-project-dialog-");

        public string Path { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Path);
    }
}