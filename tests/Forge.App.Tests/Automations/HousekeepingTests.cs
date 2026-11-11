using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class HousekeepingTests : IDisposable
{
    private readonly string root = RunnerTestKit.TempDir("forge-housekeeping-");
    private readonly CiFakeTime time = new();
    private readonly RunnerProjects projects = new();
    private readonly RunnerCatalog catalog = new();
    private readonly RunnerRunStore runs = new();
    private readonly List<string> pruned = [];
    private readonly HashSet<string> open = new(StringComparer.Ordinal);
    private Func<string, CancellationToken, Task>? prune;

    public HousekeepingTests()
    {
        time.Advance(TimeSpan.FromDays(100));
    }

    public void Dispose() => RunnerTestKit.Delete(root);

    private string Scratch(string name) => Path.Combine(root, "scratch", name);

    private ProjectInfo General() => new("g", "General", root, ProjectKind.General, 0, DateTimeOffset.UnixEpoch);

    private static ProjectInfo Repo(string id, string path) => new(id, id, path, ProjectKind.Repository, 1, DateTimeOffset.UnixEpoch);

    private HousekeepingRunner Runner() => new(
        projects,
        catalog,
        runs,
        prune ?? ((path, _) =>
        {
            pruned.Add(path);
            return Task.CompletedTask;
        }),
        () => open,
        time);

    private static RunnerContext Context() =>
        new(RunnerContext.NewRun(BuiltInAutomations.Housekeeping, DateTimeOffset.UnixEpoch), new AutomationDefinition(BuiltInAutomations.Housekeeping, "hk"));

    private SessionInfo Session(string id, string workspace, TimeSpan idle, string? automation = "auto-1", SessionStatus status = SessionStatus.Active, string? parent = null) =>
        new(id, workspace, id, DateTimeOffset.UnixEpoch)
        {
            AutomationId = automation,
            Status = status,
            ParentSessionId = parent,
            LastActiveAt = time.GetUtcNow() - idle,
        };

    private static string Summary(int worktrees, int folders, int trimmed, int archived) =>
        AutomationStrings.HousekeepingSummary(worktrees.ToString(), folders.ToString(), trimmed.ToString(), archived.ToString());

    // ------------------------------------------------------------------ scratch folders

    [Fact]
    public async Task Empty_unused_scratch_folders_are_removed_and_non_empty_ones_kept()
    {
        Directory.CreateDirectory(Scratch("empty"));
        Directory.CreateDirectory(Scratch("with-file"));
        File.WriteAllText(Path.Combine(Scratch("with-file"), "note.txt"), "x");
        Directory.CreateDirectory(Path.Combine(Scratch("with-subdir"), "inner"));
        projects.Projects = [General()];

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal(Summary(0, 1, 0, 0), outcome.Message);
        Assert.False(Directory.Exists(Scratch("empty")));
        Assert.True(Directory.Exists(Scratch("with-file")));
        Assert.True(Directory.Exists(Scratch("with-subdir")));
    }

    [Fact]
    public async Task An_empty_scratch_folder_that_a_session_points_at_is_kept_whatever_the_sessions_status()
    {
        Directory.CreateDirectory(Scratch("active"));
        Directory.CreateDirectory(Scratch("archived"));
        Directory.CreateDirectory(Scratch("gone"));
        projects.Projects = [General()];
        catalog.Sessions =
        [
            Session("a", Scratch("active") + Path.DirectorySeparatorChar, TimeSpan.Zero, automation: null),
            Session("b", Scratch("archived"), TimeSpan.Zero, automation: null, status: SessionStatus.Archived),
        ];

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.True(Directory.Exists(Scratch("active")));
        Assert.True(Directory.Exists(Scratch("archived")));
        Assert.False(Directory.Exists(Scratch("gone")));
        Assert.Equal(Summary(0, 1, 0, 0), outcome.Message);
    }

    [Fact]
    public async Task Missing_scratch_root_and_missing_general_project_are_fine()
    {
        projects.Projects = [General()];
        var withGeneral = await Runner().RunAsync(Context(), CancellationToken.None).Within();
        projects.Projects = [];
        var without = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal(Summary(0, 0, 0, 0), withGeneral.Message);
        Assert.Equal(Summary(0, 0, 0, 0), without.Message);
    }

    [Fact]
    public void RemoveEmptyScratchFolders_counts_only_what_it_deleted_and_leaves_loose_files()
    {
        Directory.CreateDirectory(Scratch("a"));
        Directory.CreateDirectory(Scratch("b"));
        File.WriteAllText(Path.Combine(root, "scratch", "loose.txt"), "x");

        var removed = HousekeepingRunner.RemoveEmptyScratchFolders(root, []);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(Path.Combine(root, "scratch", "loose.txt")));
        Assert.Equal(0, HousekeepingRunner.RemoveEmptyScratchFolders(root, []));
    }

    // ------------------------------------------------------------------ archiving

    [Fact]
    public async Task Idle_automation_sessions_are_archived_unless_open_or_otherwise_exempt()
    {
        var week = TimeSpan.FromDays(7);
        catalog.Sessions =
        [
            Session("old", "/w/old", TimeSpan.FromDays(8)),
            Session("boundary", "/w/boundary", week),
            Session("recent", "/w/recent", TimeSpan.FromDays(6)),
            Session("manual", "/w/manual", TimeSpan.FromDays(30), automation: null),
            Session("child", "/w/child", TimeSpan.FromDays(30), parent: "old"),
            Session("archived", "/w/archived", TimeSpan.FromDays(30), status: SessionStatus.Archived),
            Session("deleted", "/w/deleted", TimeSpan.FromDays(30), status: SessionStatus.Deleted),
            Session("open", "/w/open", TimeSpan.FromDays(30)),
        ];
        open.Add("open");
        runs.TrimResult = 4;

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal(["old", "boundary"], catalog.StatusChanges.Select(static c => c.Id));
        Assert.All(catalog.StatusChanges, static c => Assert.Equal(SessionStatus.Archived, c.Status));
        Assert.Equal(Summary(0, 0, 4, 2), outcome.Message);
        Assert.Equal((HousekeepingRunner.RunsPerAutomation, HousekeepingRunner.RunsTotal), Assert.Single(runs.Trims));
    }

    [Fact]
    public async Task A_session_the_catalog_refuses_to_archive_is_not_counted()
    {
        catalog.Sessions =
        [
            Session("a", "/w/a", TimeSpan.FromDays(9)),
            Session("b", "/w/b", TimeSpan.FromDays(9)),
        ];
        catalog.RefuseStatus.Add("a");

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal("b", Assert.Single(catalog.StatusChanges).Id);
        Assert.Equal(Summary(0, 0, 0, 1), outcome.Message);
    }

    [Fact]
    public async Task Sessions_without_an_automation_id_are_never_touched()
    {
        catalog.Sessions = [Session("m", "/w/m", TimeSpan.FromDays(365), automation: null)];

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Empty(catalog.StatusChanges);
        Assert.Equal(Summary(0, 0, 0, 0), outcome.Message);
    }

    // ------------------------------------------------------------------ worktree pruning

    [Fact]
    public async Task Only_repository_projects_are_pruned()
    {
        projects.Projects = [General(), Repo("r1", "/repo/one"), Repo("r2", "/repo/two")];

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal(["/repo/one", "/repo/two"], pruned);
        Assert.Equal(Summary(2, 0, 0, 0), outcome.Message);
    }

    [Fact]
    public async Task A_failing_prune_does_not_stop_the_other_steps()
    {
        projects.Projects = [General(), Repo("r1", "/repo/one"), Repo("r2", "/repo/two")];
        Directory.CreateDirectory(Scratch("empty"));
        catalog.Sessions = [Session("old", "/w/old", TimeSpan.FromDays(10))];
        prune = (path, _) =>
        {
            pruned.Add(path);
            return path == "/repo/one" ? Task.FromException(new InvalidOperationException("git broke")) : Task.CompletedTask;
        };

        var outcome = await Runner().RunAsync(Context(), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal(["/repo/one", "/repo/two"], pruned);
        Assert.Equal(Summary(1, 1, 0, 1), outcome.Message);
        Assert.False(Directory.Exists(Scratch("empty")));
        Assert.Single(catalog.StatusChanges);
    }

    [Fact]
    public async Task Cancellation_inside_a_prune_propagates()
    {
        projects.Projects = [Repo("r1", "/repo/one")];
        using var cts = new CancellationTokenSource();
        prune = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().RunAsync(Context(), cts.Token)).Within();

        Assert.Empty(catalog.StatusChanges);
        Assert.Empty(runs.Trims);
    }

    [Fact]
    public async Task A_failing_project_list_propagates_to_the_engine()
    {
        projects.ThrowOnList = new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(Context(), CancellationToken.None)).Within();
    }
}
