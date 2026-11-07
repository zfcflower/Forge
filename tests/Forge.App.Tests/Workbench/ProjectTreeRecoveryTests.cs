using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the bug behind "adding a project fails on the left panel": a project row that another tool stamped with a
/// six-digit fractional second made every <c>forge.db</c> read fail, so the panel showed the raw database error, the
/// tree stayed empty, and a project the user had just added was written but never appeared. The v3 step repairs that
/// row when the database is opened, so the shell starts with a working tree and no status text.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProjectTreeRecoveryTests
{
    /// <summary>The value found in a real database: <c>datetime.isoformat()</c> writes six fractional digits.</summary>
    private const string ForeignTimestamp = "2026-11-04T07:44:28.766342+00:00";

    private const string StorageFileName = "forge.db";

    [Fact]
    public async Task A_project_stamped_by_another_writer_no_longer_empties_the_tree_or_reports_an_error()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "forge-tree-recovery-" + Guid.NewGuid().ToString("N"));
                var data = Path.Combine(root, "data");
                var general = Path.Combine(root, "general");
                var workspace = Path.Combine(root, "workspace");
                foreach (var directory in new[] { data, general, workspace })
                {
                    Directory.CreateDirectory(directory);
                }

                var previous = (
                    Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
                    General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
                    Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
                try
                {
                    var repository = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
                    await using (var registry = new SqliteProjectRegistry(data))
                    {
                        await registry.AddAsync(repository, "demo", ProjectKind.Repository, CancellationToken.None);
                    }

                    await RestampAsVersion2FileAsync(Path.Combine(data, StorageFileName));

                    Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
                    Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);
                    Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);

                    var composition = AppComposition.Create();
                    try
                    {
                        var viewModel = new MainWindowViewModel(composition);
                        await viewModel.InitializeAsync();

                        Assert.Empty(viewModel.Projects.StatusText);
                        Assert.Contains(
                            "demo",
                            viewModel.Projects.Sections.Select(static section => section.Title));
                    }
                    finally
                    {
                        await composition.DisposeAsync();
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable(AppPaths.DataVariable, previous.Data);
                    Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, previous.General);
                    Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, previous.Workspace);
                    DeleteTree(root);
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Turns the fresh file into the shape a v2 build left — no triggers, <c>user_version</c> 2 — and stamps its project
    /// row with the foreign spelling, so the next open has to run the real v3 step over a database that would
    /// otherwise fail every read.
    /// </summary>
    private static async Task RestampAsVersion2FileAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        var triggers = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger'";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                triggers.Add(reader.GetString(0));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            string.Concat(triggers.Select(static name => $"DROP TRIGGER {name};\n")) +
            $"UPDATE projects SET added_at = '{ForeignTimestamp}';\n" +
            "PRAGMA user_version = 2;";
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteTree(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory never fails the assertion the test is actually about.
        }
    }
}
