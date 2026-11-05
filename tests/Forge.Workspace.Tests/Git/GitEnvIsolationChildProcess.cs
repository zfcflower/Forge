using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Child-process seam for <see cref="GitCliEnvIsolationTests"/>: the parent launches the test
/// assembly with repository-location variables (<c>GIT_DIR</c>, <c>GIT_WORK_TREE</c>,
/// <c>GIT_INDEX_FILE</c>, <c>GIT_OBJECT_DIRECTORY</c>, <c>GIT_COMMON_DIR</c>) pointing at a
/// decoy repository. The child calls <see cref="GitCli"/> against the target repository only.
/// If <see cref="GitCli"/> inherited those variables, the snapshot and status would describe
/// the decoy; the fix strips them from every child git process. Mirrors the proven
/// <c>ApprovalChildProcess</c> pattern, so the test-process environment is never mutated
/// and the test stays parallel-safe.
/// </summary>
internal static class GitEnvIsolationChildProcess
{
    internal const string ModeVariable = "FORGE_WORKSPACE_GIT_CHILD_MODE";
    internal const string TargetVariable = "FORGE_WORKSPACE_GIT_CHILD_TARGET";
    internal const string ProbeMode = "probe";

    internal static Process Start(string target, IReadOnlyDictionary<string, string> poison)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(typeof(GitEnvIsolationChildProcess).Assembly.Location);
        info.Environment[ModeVariable] = ProbeMode;
        info.Environment[TargetVariable] = target;
        foreach (var pair in poison)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The git child process did not start.");
    }

    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        // The module-initializer thread must return promptly: blocking it on genuinely
        // asynchronous work deadlocks (verified: the first real async hop inside
        // GitCli never completes while the initializer is parked). A foreground worker
        // thread runs the async probe instead; the host keeps the process alive until the
        // worker calls Environment.Exit, so the initializer just starts it and returns.
        var worker = new Thread(() =>
        {
            int exitCode;
            try
            {
                exitCode = Run(mode);
            }
            catch (Exception exception)
            {
                Console.Out.WriteLine("fatal " + exception.GetType().Name + ": " + exception.Message);
                Console.Out.Flush();
                exitCode = 1;
            }

            Environment.Exit(exitCode);
        })
        {
            IsBackground = false,
            Name = "git-env-isolation-child",
        };
        worker.Start();
    }

    private static int Run(string mode)
    {
        if (!string.Equals(mode, ProbeMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown git child mode '{mode}'.");
        }

        var target = Environment.GetEnvironmentVariable(TargetVariable)
            ?? throw new InvalidOperationException("The git child process has no target.");
        return Probe(target).GetAwaiter().GetResult();
    }

    private static async Task<int> Probe(string target)
    {
        var cli = new GitCli();
        var tree = await cli.SnapshotTreeAsync(target).ConfigureAwait(false);
        Console.Out.WriteLine("tree " + tree.Trim());
        var status = await cli.GetStatusAsync(target).ConfigureAwait(false);
        Console.Out.WriteLine("status " + status.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var entry in status.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            Console.Out.WriteLine("entry " + entry.Kind + " " + entry.Path);
        }

        Console.Out.Flush();
        return 0;
    }
}
