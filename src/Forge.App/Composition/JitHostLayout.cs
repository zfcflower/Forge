namespace Forge.App.Composition;

/// <summary>
/// The JIT-side executables the extension host needs: itself, the plugin worker it spawns and the single-use script
/// executor. Located the same way <c>tools/Forge.Smoke</c> does it - an explicit directory first, then the sibling build
/// layout under the repository root - so the AOT app never hard-codes a machine path.
/// </summary>
internal sealed record JitHostLayout(string HostPath, string WorkerPath, string ExecutorPath)
{
    public const string DirectoryVariable = "FORGE_JIT_DIR";

    private const string HostAssembly = "Forge.ExtensionHost";

    private const string WorkerAssembly = "Forge.PluginWorker";

    private const string ExecutorAssembly = "Forge.ScriptExecutor";

    private const string TargetFramework = "net10.0";

    private const int ProbeDepth = 8;

    private static readonly string[] Configurations =
#if DEBUG
        ["Debug", "Release"];
#else
        ["Release", "Debug"];
#endif

    /// <summary>Resolves the layout, or returns null with a sentence explaining what was searched.</summary>
    public static JitHostLayout? Locate(out string problem)
    {
        var host = Resolve(HostAssembly);
        if (host is null)
        {
            problem =
                "找不到 " + HostAssembly + ".dll。请先构建 JIT 部分(dotnet build Forge.slnx)，或把 " +
                DirectoryVariable + " 指向它所在的目录。";
            return null;
        }

        var worker = Resolve(WorkerAssembly);
        if (worker is null)
        {
            problem = "找不到 " + WorkerAssembly + ".dll；请先构建 JIT 部分，或设置 " + DirectoryVariable + "。";
            return null;
        }

        var executor = Resolve(ExecutorAssembly);
        if (executor is null)
        {
            problem = "找不到 " + ExecutorAssembly + ".dll；请先构建 JIT 部分，或设置 " + DirectoryVariable + "。";
            return null;
        }

        problem = string.Empty;
        return new JitHostLayout(host, worker, executor);
    }

    private static string? Resolve(string assemblyName)
    {
        var fileName = assemblyName + ".dll";
        foreach (var directory in CandidateDirectories(assemblyName))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string assemblyName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (seen.Add(full))
            {
                yield return full;
            }
        }

        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; start is not null && depth < ProbeDepth; depth++, start = start.Parent)
        {
            yield return start.FullName;
            foreach (var configuration in Configurations)
            {
                foreach (var candidate in
                    new[]
                    {
                        Path.Combine(start.FullName, "src", assemblyName, "bin", configuration, TargetFramework),
                        Path.Combine(start.FullName, "artifacts", "publish", assemblyName, configuration),
                        Path.Combine(start.FullName, "publish", assemblyName, configuration),
                    })
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }
}
