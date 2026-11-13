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
        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                var (root, previous) = TempDataRoot.Redirect("forge-tree-recovery-");
                var data = Path.Combine(root, "data");
                try
                {
                    var repository = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
                    await using (var registry = new SqliteProjectRegistry(data))
                    {
                        await registry.AddAsync(repository, "demo", ProjectKind.Repository, CancellationToken.None);
                    }

                    await RestampAsVersion2FileAsync(Path.Combine(data, StorageFileName));

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
                    previous.Restore();
                    TestTempFiles.DeleteTree(root);
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
}
