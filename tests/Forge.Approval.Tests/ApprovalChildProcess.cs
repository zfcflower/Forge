using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Forge.Approval.Tests;

internal static class ApprovalChildProcess
{
    internal const string ModeVariable = "FORGE_APPROVAL_TEST_CHILD_MODE";
    internal const string DirectoryVariable = "FORGE_APPROVAL_TEST_CHILD_DIR";
    internal const string TagVariable = "FORGE_APPROVAL_TEST_CHILD_TAG";
    internal const string CountVariable = "FORGE_APPROVAL_TEST_CHILD_COUNT";
    internal const string StressMode = "stress";
    internal const string ImportMode = "import";
    internal const string CrashMode = "crash";
    internal const string ArmedLine = "armed";

    internal static Process Start(string mode, string directory, string tag = "", int count = 0)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(typeof(ApprovalChildProcess).Assembly.Location);
        info.Environment[ModeVariable] = mode;
        info.Environment[DirectoryVariable] = directory;
        info.Environment[TagVariable] = tag;
        info.Environment[CountVariable] = count.ToString(CultureInfo.InvariantCulture);
        return Process.Start(info) ?? throw new InvalidOperationException("The child test process did not start.");
    }

    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        var directory = Environment.GetEnvironmentVariable(DirectoryVariable)
            ?? throw new InvalidOperationException("The child test process has no directory.");
        var tag = Environment.GetEnvironmentVariable(TagVariable) ?? string.Empty;
        var count = int.Parse(Environment.GetEnvironmentVariable(CountVariable) ?? "0", CultureInfo.InvariantCulture);
        int exitCode;
        try
        {
            exitCode = Run(mode, directory, tag, count);
        }
        catch (Exception exception)
        {
            Console.Out.WriteLine("fatal " + exception.GetType().Name + ": " + exception.Message);
            Console.Out.Flush();
            exitCode = 1;
        }

        Environment.Exit(exitCode);
    }

    private static int Run(string mode, string directory, string tag, int count) => mode switch
    {
        StressMode => Stress(directory, tag, count),
        ImportMode => Import(directory),
        CrashMode => Crash(directory),
        _ => throw new InvalidOperationException($"Unknown child test mode '{mode}'."),
    };

    private static int Stress(string directory, string tag, int count)
    {
        using var store = new ApprovalStore(directory);
        var busy = 0;
        var fail = 0;
        string? first = null;
        for (var i = 0; i < count; i++)
        {
            try
            {
                store.SetApproved($"{tag}.plugin.{i}", "1.0.0", $"hash-{i}", "dep", "10.0.100");
                store.SetApproved("shared.plugin", "1.0.0", $"{tag}-{i}", "dep", "10.0.100");
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                busy++;
                first ??= exception.GetType().Name + " " + exception.SqliteErrorCode + ": " + exception.Message;
            }
            catch (Exception exception)
            {
                fail++;
                first ??= exception.GetType().Name + ": " + exception.Message;
            }
        }

        Console.Out.WriteLine($"stress {tag} ok={count - busy - fail} busy={busy} fail={fail} rows={store.Count} first={first ?? "-"}");
        Console.Out.Flush();
        return busy == 0 && fail == 0 ? 0 : 1;
    }

    private static int Import(string directory)
    {
        using var store = new ApprovalStore(directory);
        Console.Out.WriteLine($"import count={store.Count}");
        Console.Out.Flush();
        return 0;
    }

    private static int Crash(string directory)
    {
        using (var store = new ApprovalStore(directory))
        {
            for (var i = 1; i <= 20; i++)
            {
                store.SetApproved("victim.plugin", "1.0.0", $"revision-{i}", "dep", "10.0.100");
            }
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, ApprovalStore.FileName),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE plugin_approval SET source_hash = 'uncommitted' WHERE plugin_id = 'victim.plugin' AND version = '1.0.0'";
            update.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO plugin_approval
                    (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
                VALUES
                    ($pluginId, '1.0.0', 'Draft', 'uncommitted', 'd', 's', NULL, '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)
                """;
            var id = insert.Parameters.Add("$pluginId", SqliteType.Text);
            for (var i = 0; i < 2000; i++)
            {
                id.Value = "doomed-" + i.ToString(CultureInfo.InvariantCulture);
                insert.ExecuteNonQuery();
            }
        }

        Console.Out.WriteLine(ArmedLine);
        Console.Out.Flush();
        Console.In.ReadLine();
        return 0;
    }
}
