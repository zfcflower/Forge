using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Loader;
using Forge.Abstractions;

namespace Forge.Plugins;

/// <summary>
/// One collectible context per plugin. The plugin's own dependencies resolve from its directory (via
/// <c>.deps.json</c> when present) while <see cref="Forge.Abstractions"/> and the shared framework always
/// bind to the host copy, so contract types keep a single identity. Nothing is shared between two plugin
/// contexts, so a plugin cannot see another plugin's private dependencies.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly FrozenSet<string> PlatformAssemblyNames = LoadPlatformAssemblyNames();

    private static readonly string ContractAssemblyName =
        typeof(IForgePlugin).Assembly.GetName().Name ?? "Forge.Abstractions";

    private readonly AssemblyDependencyResolver? resolver;
    private readonly string directory;
    private readonly FrozenSet<string> sharedAssemblyNames;
    private readonly ConcurrentDictionary<string, string?> assemblyProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IntPtr> unmanagedProbes = new(StringComparer.Ordinal);

    public PluginLoadContext(string pluginDirectory, string mainAssemblyPath, string? name = null, IEnumerable<string>? sharedAssemblyNames = null)
        : base(name ?? $"forge-plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);
        directory = Path.GetFullPath(pluginDirectory);

        var mainAssembly = Path.GetFullPath(mainAssemblyPath);
        resolver = File.Exists(mainAssembly)
            ? new AssemblyDependencyResolver(mainAssembly)
            : null;

        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ContractAssemblyName };
        foreach (var assembly in Default.Assemblies)
        {
            if (assembly.GetName().Name is { } simpleName)
            {
                shared.Add(simpleName);
            }
        }

        if (sharedAssemblyNames is not null)
        {
            foreach (var sharedName in sharedAssemblyNames)
            {
                if (!string.IsNullOrWhiteSpace(sharedName))
                {
                    shared.Add(sharedName);
                }
            }
        }

        this.sharedAssemblyNames = shared.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public string PluginDirectory => directory;

    /// <summary>Assemblies the host always resolves itself instead of loading a plugin-local copy.</summary>
    public IReadOnlyCollection<string> SharedAssemblyNames => sharedAssemblyNames;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } simpleName)
        {
            return null;
        }

        if (sharedAssemblyNames.Contains(simpleName) || PlatformAssemblyNames.Contains(simpleName))
        {
            return null;
        }

        var path = assemblyProbes.GetOrAdd(simpleName, static (name, context) => context.ResolveAssembly(name), this);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName))
        {
            return IntPtr.Zero;
        }

        return unmanagedProbes.GetOrAdd(unmanagedDllName, static (name, context) => context.ResolveUnmanaged(name), this);
    }

    private string? ResolveAssembly(string simpleName)
    {
        if (resolver is not null)
        {
            var resolved = resolver.ResolveAssemblyToPath(new AssemblyName(simpleName));
            if (resolved is not null && File.Exists(resolved))
            {
                return resolved;
            }
        }

        var candidate = Path.Combine(directory, simpleName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private IntPtr ResolveUnmanaged(string unmanagedDllName)
    {
        if (resolver is null)
        {
            return IntPtr.Zero;
        }

        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return LoadUnmanagedDllFromPath(path);
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or BadImageFormatException)
        {
            return IntPtr.Zero;
        }
    }

    private static FrozenSet<string> LoadPlatformAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string platformAssemblies)
        {
            foreach (var path in platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var simpleName = Path.GetFileNameWithoutExtension(path);
                if (simpleName.Length > 0)
                {
                    names.Add(simpleName);
                }
            }
        }

        names.Add(ContractAssemblyName);
        return names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
