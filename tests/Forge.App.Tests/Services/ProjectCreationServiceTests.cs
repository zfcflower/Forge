using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// <see cref="ProjectCreationService"/> (wave W9): the name rules in the order the dialog checks them, the folder suggestion
/// taken from a remote's address, whether an address is usable at all, where the request lands, and the two modes — an empty
/// repository created by <c>git init</c> and a clone — with what a refused, failed or cancelled run leaves behind.
/// </summary>
public sealed class ProjectCreationServiceTests
{
    // ---------------------------------------------------------------- ValidateName

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_empty_name_is_refused(string? name)
    {
        Assert.Equal(ProjectNameProblem.Empty, ProjectCreationService.ValidateName(null, name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\u0001b")]
    [InlineData("a\u007Fb")]
    public void A_name_a_folder_cannot_hold_is_refused_for_its_characters(string name)
    {
        Assert.Equal(ProjectNameProblem.InvalidCharacters, ProjectCreationService.ValidateName(null, name));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("C:\\x")]
    public void A_name_windows_cannot_hold_is_refused_for_its_characters(string name)
    {
        // These characters are only names the file system refuses on Windows (':' and '?' are ordinary there on Unix), so
        // the expectation is stated for the platform that has the rule.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // "C:\x" reaches the character rule first: the backslash is what makes it unusable, not its root.
        Assert.Equal(ProjectNameProblem.InvalidCharacters, ProjectCreationService.ValidateName(null, name));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void A_relative_step_is_not_a_folder_name(string name)
    {
        Assert.Equal(ProjectNameProblem.NotASegment, ProjectCreationService.ValidateName(null, name));
    }

    [Fact]
    public void A_rooted_or_drive_relative_name_is_refused_by_the_character_rule_first()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The rooted-name rule is defensive: on Windows a rooted name always carries a separator or a drive colon, and both
        // are characters a folder name cannot hold, so the character rule refuses it one step earlier.
        Assert.Equal(ProjectNameProblem.InvalidCharacters, ProjectCreationService.ValidateName(null, "C:name"));
        Assert.Equal(ProjectNameProblem.InvalidCharacters, ProjectCreationService.ValidateName(null, "/name"));
    }

    [Fact]
    public void The_length_limit_is_exactly_one_hundred_characters()
    {
        var longest = new string('a', ProjectCreationService.MaxNameLength);
        Assert.Equal(100, ProjectCreationService.MaxNameLength);

        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName(null, longest));
        Assert.Equal(ProjectNameProblem.TooLong, ProjectCreationService.ValidateName(null, longest + "a"));
    }

    [Theory]
    [InlineData("name ")]
    [InlineData("name.")]
    [InlineData("name. ")]
    public void A_name_windows_would_trim_is_refused(string name)
    {
        Assert.Equal(ProjectNameProblem.TrailingDotOrSpace, ProjectCreationService.ValidateName(null, name));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("Con.tar.gz")]
    [InlineData("com1")]
    [InlineData("COM9")]
    [InlineData("lpt9")]
    [InlineData("nul")]
    [InlineData("aux")]
    [InlineData("prn")]
    public void A_windows_device_name_is_refused_in_any_case_and_with_any_extension(string name)
    {
        Assert.Equal(ProjectNameProblem.ReservedName, ProjectCreationService.ValidateName(null, name));
    }

    [Theory]
    [InlineData("console")] // starts like a device name but is not one
    [InlineData("com0")] // devices start at 1
    [InlineData("com10")]
    [InlineData("my.con")]
    public void A_name_that_only_looks_like_a_device_name_is_accepted(string name)
    {
        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName(null, name));
    }

    [Fact]
    public void A_directory_that_already_holds_something_is_refused_while_an_empty_one_is_usable()
    {
        using var sandbox = NewSandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "empty"));
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "full"));
        File.WriteAllText(Path.Combine(sandbox.Path, "full", "keep.txt"), "kept");

        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName(sandbox.Path, "empty"));
        Assert.Equal(ProjectNameProblem.Exists, ProjectCreationService.ValidateName(sandbox.Path, "full"));
        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName(sandbox.Path, "absent"));

        // A parent the user has not chosen yet cannot have an occupied entry, so the existence check is skipped.
        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName("  ", "full"));
    }

    [Fact]
    public void A_plain_name_is_usable()
    {
        Assert.Equal(ProjectNameProblem.None, ProjectCreationService.ValidateName(null, "forge-clone"));
    }

    // ---------------------------------------------------------------- SuggestNameFromUrl

    [Theory]
    [InlineData("https://host/team/repo", "repo")]
    [InlineData("https://host/team/repo/", "repo")]
    [InlineData("https://host/team/repo.GIT", "repo")]
    [InlineData("https://host/team/repo.git?ref=main", "repo")]
    [InlineData("https://host/team/repo#readme", "repo")]
    [InlineData("https://host/team/repo.git#main", "repo")]
    [InlineData("git@host:team/repo.git", "repo")]
    [InlineData("git@host:repo.git", "repo")]
    [InlineData("ssh://git@host:2222/team/repo.git", "repo")]
    [InlineData("  https://host/team/repo.git  ", "repo")]
    public void The_folder_name_is_the_last_path_segment(string url, string expected)
    {
        Assert.Equal(expected, ProjectCreationService.SuggestNameFromUrl(url));
    }

    [Theory]
    [InlineData("https://host")]
    [InlineData("https://host/")]
    [InlineData("git@host:")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_address_with_no_segment_suggests_no_name_at_all(string? url)
    {
        Assert.Equal(string.Empty, ProjectCreationService.SuggestNameFromUrl(url));
    }

    /// <summary>
    /// The contract the suggestion keeps: the <c>.git</c> suffix is dropped from the end, never from the front, so a real
    /// remote prefills the folder name it names.
    /// </summary>
    [Theory]
    [InlineData("https://host/team/my-project.git", "my-project")]
    [InlineData("https://host/team/analysis.git", "analysis")]
    [InlineData("git@host:team/forge-cli.git", "forge-cli")]
    [InlineData("https://host/team/web.git", "web")]
    [InlineData("https://host/team/x.git", "x")]
    [InlineData("https://host/team/a-b-c.git", "a-b-c")]
    [InlineData("  https://host/team/my-project.git  ", "my-project")]
    [InlineData("https://host/team/my-project.GIT", "my-project")]
    public void Any_repository_name_survives_its_git_suffix(string url, string expected)
    {
        Assert.Equal(expected, ProjectCreationService.SuggestNameFromUrl(url));
    }

    // ---------------------------------------------------------------- IsUsableUrl

    [Theory]
    [InlineData("https://host/team/repo.git", true)]
    [InlineData("http://host/team/repo.git", true)]
    [InlineData("ssh://git@host/team/repo.git", true)]
    [InlineData("git@host:team/repo.git", true)]
    [InlineData("git@host:repo", true)]
    [InlineData("file:///C:/repos/source", true)]
    [InlineData("repo", false)]
    [InlineData("-x", false)]
    [InlineData("--upload-pack=evil", false)]
    [InlineData("https://host/re\tpo", false)]
    [InlineData("https://host/re\u0000po", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void An_address_must_be_absolute_and_must_not_be_an_option(string? url, bool usable)
    {
        Assert.Equal(usable, ProjectCreationService.IsUsableUrl(url));
    }

    // ---------------------------------------------------------------- ResolveDirectory

    [Fact]
    public void The_directory_is_the_parent_and_the_name_as_a_full_path()
    {
        using var sandbox = NewSandbox();
        var request = new ProjectCreationRequest(ProjectCreationMode.Create, sandbox.Path, "forge");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(sandbox.Path, "forge")),
            ProjectCreationService.ResolveDirectory(request));
    }

    // ---------------------------------------------------------------- RunAsync: a new repository

    [Fact]
    public async Task Creating_a_project_runs_git_init_in_the_new_folder()
    {
        using var sandbox = NewSandbox();
        var service = Service();
        var destination = Path.GetFullPath(Path.Combine(sandbox.Path, "fresh"));

        var result = await service.RunAsync(new ProjectCreationRequest(ProjectCreationMode.Create, sandbox.Path, "fresh"));

        Assert.Equal(ProjectCreationResult.Created(destination), result);
        Assert.Null(result.Error);
        Assert.True(Directory.Exists(destination));
        Assert.True(await IsRepositoryAsync(destination), "git init left no repository behind");
    }

    [Fact]
    public async Task Creating_a_project_creates_a_missing_parent_directory()
    {
        using var sandbox = NewSandbox();
        var service = Service();
        var parent = Path.Combine(sandbox.Path, "not", "yet", "there");

        var result = await service.RunAsync(new ProjectCreationRequest(ProjectCreationMode.Create, parent, "fresh"));

        Assert.Equal(Path.GetFullPath(Path.Combine(parent, "fresh")), result.Directory);
        Assert.True(Directory.Exists(result.Directory));
    }

    [Theory]
    [InlineData("a/b", ProjectCreationStrings.NameInvalidKey)]
    [InlineData("con", ProjectCreationStrings.NameReservedKey)]
    [InlineData("name.", ProjectCreationStrings.NameTrailingKey)]
    [InlineData("..", ProjectCreationStrings.NameNotSegmentKey)]
    public async Task A_refused_name_creates_nothing_and_says_which_rule_refused_it(string name, string expectedKey)
    {
        using var sandbox = NewSandbox();
        var service = Service();

        var result = await service.RunAsync(new ProjectCreationRequest(ProjectCreationMode.Create, sandbox.Path, name));

        Assert.Null(result.Directory);
        Assert.Equal(expectedKey, result.Error!.Value.Key);

        // Nothing was created: the checked folder name never reached the file system.
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Path));
    }

    [Fact]
    public async Task A_name_taken_by_a_non_empty_folder_is_refused_and_that_folder_is_kept()
    {
        using var sandbox = NewSandbox();
        var service = Service();
        var occupied = Path.Combine(sandbox.Path, "taken");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "kept");

        var result = await service.RunAsync(new ProjectCreationRequest(ProjectCreationMode.Create, sandbox.Path, "taken"));

        Assert.Null(result.Directory);
        Assert.Equal(ProjectCreationStrings.NameExistsKey, result.Error!.Value.Key);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(occupied, "keep.txt")));
    }

    [Fact]
    public async Task A_missing_parent_directory_is_refused_before_anything_is_created()
    {
        using var sandbox = NewSandbox();
        var service = Service();

        var result = await service.RunAsync(new ProjectCreationRequest(ProjectCreationMode.Create, "  ", "fresh"));

        Assert.Null(result.Directory);
        Assert.Equal(ProjectCreationStrings.ParentMissingKey, result.Error!.Value.Key);
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Path));
    }

    // ---------------------------------------------------------------- RunAsync: a clone

    [Fact]
    public async Task A_clone_of_an_unusable_address_is_refused_without_touching_the_file_system()
    {
        using var sandbox = NewSandbox();
        var service = Service();

        var result = await service.RunAsync(
            new ProjectCreationRequest(ProjectCreationMode.Clone, sandbox.Path, "clone", "not-an-address"));

        Assert.Null(result.Directory);
        Assert.Equal(ProjectCreationStrings.UrlNotUsableKey, result.Error!.Value.Key);
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Path));
    }

    [Fact]
    public async Task A_clone_that_fails_reports_gits_message_and_leaves_no_folder()
    {
        using var sandbox = NewSandbox();
        var service = Service();
        var missing = new Uri(Path.Combine(sandbox.Path, "no-such-repository")).AbsoluteUri;

        var result = await service.RunAsync(
            new ProjectCreationRequest(ProjectCreationMode.Clone, sandbox.Path, "clone", missing));

        Assert.Null(result.Directory);
        Assert.False(result.Error!.Value.IsEmpty);
        Assert.False(Directory.Exists(Path.Combine(sandbox.Path, "clone")));
    }

    [Fact]
    public async Task A_clone_reports_progress_and_returns_the_created_folder()
    {
        using var sandbox = NewSandbox();
        using var source = GitTestSandbox.Create();
        var repository = source.CloneTracked("source");
        var service = Service();
        var phases = new List<GitClonePhase>();

        // The file:// form makes git use its transport layer, which reports progress even for a local source.
        var result = await service.RunAsync(
            new ProjectCreationRequest(ProjectCreationMode.Clone, sandbox.Path, "clone", FileUrl(repository)),
            progress => phases.Add(progress.Phase));

        Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "clone")), result.Directory);
        Assert.Null(result.Error);
        Assert.True(await IsRepositoryAsync(Assert.IsType<string>(result.Directory)));
        Assert.NotEmpty(phases);
    }

    [Fact]
    public async Task A_cancelled_creation_throws_instead_of_reporting_a_failure()
    {
        using var sandbox = NewSandbox();
        using var source = GitTestSandbox.Create();
        var repository = source.CloneTracked("source");
        var service = Service();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
            new ProjectCreationRequest(ProjectCreationMode.Clone, sandbox.Path, "clone", FileUrl(repository)),
            null,
            cancelled.Token));

        Assert.False(Directory.Exists(Path.Combine(sandbox.Path, "clone")));
    }

    // ---------------------------------------------------------------- defaults from the preferences

    [Fact]
    public void The_default_parent_is_the_fallback_until_a_preference_names_one()
    {
        var preferences = new AppPreferences();
        var service = new ProjectCreationService(new GitCli(), preferences, @"C:\fallback");

        Assert.Equal(string.Empty, preferences.ProjectsCloneDirectory);
        Assert.Equal(@"C:\fallback", service.DefaultParentDirectory);

        preferences.ProjectsCloneDirectory = @"D:\projects";
        Assert.Equal(@"D:\projects", service.DefaultParentDirectory);

        // The preference is read on every access, so a value saved in the settings page applies to the next dialog.
        preferences.ProjectsCloneDirectory = @"E:\elsewhere";
        Assert.Equal(@"E:\elsewhere", service.DefaultParentDirectory);
    }

    [Fact]
    public void The_clone_timeout_is_null_for_zero_and_the_preference_otherwise()
    {
        var preferences = new AppPreferences();
        var service = new ProjectCreationService(new GitCli(), preferences, @"C:\fallback");
        Assert.Equal(AppPreferences.DefaultProjectsCloneTimeoutSeconds, preferences.ProjectsCloneTimeoutSeconds);
        Assert.Null(service.CloneTimeout);

        preferences.ProjectsCloneTimeoutSeconds = 30;
        Assert.Equal(TimeSpan.FromSeconds(30), service.CloneTimeout);

        // The preference clamps to the documented range, and the service reads back what it accepted.
        preferences.ProjectsCloneTimeoutSeconds = 7200;
        Assert.Equal(TimeSpan.FromSeconds(AppPreferences.MaxProjectsCloneTimeoutSeconds), service.CloneTimeout);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A throw-away directory for one test's parent and destination folders.</summary>
    private static ProjectSandbox NewSandbox() => new();

    private static ProjectCreationService Service() => new(new GitCli(), new AppPreferences(), @"C:\fallback");

    private static async Task<bool> IsRepositoryAsync(string directory) =>
        await new GitCli().IsRepositoryAsync(directory).WaitAsync(TimeSpan.FromSeconds(30));

    private static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private sealed class ProjectSandbox : IDisposable
    {
        public ProjectSandbox() => Path = TestTempFiles.NewDirectory("forge-project-creation-");

        public string Path { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Path);
    }
}