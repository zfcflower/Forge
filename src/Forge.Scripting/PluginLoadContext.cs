using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Forge.Plugin.Sdk;

namespace Forge.Scripting;

/// <summary>
/// One collectible context per plugin. In the default path mode the plugin's own dependencies resolve from its
/// directory (via <c>.deps.json</c> when a component path is supplied, then a flat directory probe); in verified-image
/// mode (<see cref="FromVerifiedImages"/>) every managed dependency resolves from bytes supplied up front and nothing
/// is read from the plugin directory. <c>Forge.Plugin.Sdk</c> and the shared framework always bind to the host copy, so
/// <see cref="IPlugin"/> keeps a single type identity. Nothing is shared between two contexts.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    public static readonly TimeSpan DefaultUnloadTimeout = TimeSpan.FromSeconds(2);

    private static readonly FrozenSet<string> PlatformAssemblyNames = LoadPlatformAssemblyNames();

    private static readonly string ContractAssemblyName =
        typeof(IPlugin).Assembly.GetName().Name ?? "Forge.Plugin.Sdk";

    private readonly AssemblyDependencyResolver? resolver;
    private readonly string directory;
    private readonly FrozenSet<string> sharedAssemblyNames;
    private readonly ConcurrentDictionary<string, string?> assemblyProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IntPtr> unmanagedProbes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Assembly> verifiedDependencyAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock verifiedDependencyGate = new();

    private static readonly ConcurrentDictionary<TrackedFile, byte> TrackedFiles = new();

    private const int ReleasePollMilliseconds = 5;

    private FrozenDictionary<string, byte[]>? verifiedDependencyImages;
    private Assembly? pluginAssembly;
    private Task<bool>? unloadTask;
    private int unloadStarted;

    /// <param name="pluginDirectory">Directory the plugin's private dependencies are probed in.</param>
    /// <param name="dependencyAssemblyPath">Path of the plugin assembly on disk, used to build an
    /// <see cref="AssemblyDependencyResolver"/> over its <c>.deps.json</c>. Optional when loading from bytes.</param>
    /// <param name="name">Context name; defaults to <c>forge-plugin:&lt;directory name&gt;</c>.</param>
    /// <param name="sharedAssemblyNames">Extra simple names the host always resolves itself.</param>
    public PluginLoadContext(
        string pluginDirectory,
        string? dependencyAssemblyPath = null,
        string? name = null,
        IEnumerable<string>? sharedAssemblyNames = null)
        : this(pluginDirectory, dependencyAssemblyPath, name, sharedAssemblyNames, verifiedDependencyImages: null)
    {
    }

    /// <summary>
    /// Creates a context whose private dependencies come only from already verified in-memory images, keyed by
    /// assembly simple name. In this mode no path fallback and no <c>.deps.json</c> lookup happens: an assembly that is
    /// neither shared nor supplied fails the load that needs it instead of being read from the mutable plugin directory.
    /// </summary>
    public static PluginLoadContext FromVerifiedImages(
        string pluginDirectory,
        IReadOnlyDictionary<string, byte[]> verifiedDependencyImages,
        string? name = null,
        IEnumerable<string>? sharedAssemblyNames = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedDependencyImages);
        return new PluginLoadContext(pluginDirectory, null, name, sharedAssemblyNames, verifiedDependencyImages);
    }

    private PluginLoadContext(
        string pluginDirectory,
        string? dependencyAssemblyPath,
        string? name,
        IEnumerable<string>? sharedAssemblyNames,
        IReadOnlyDictionary<string, byte[]>? verifiedDependencyImages)
        : base(name ?? ContextName(pluginDirectory), isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        directory = Path.GetFullPath(pluginDirectory);
        this.verifiedDependencyImages = IndexVerifiedImages(verifiedDependencyImages);

        if (this.verifiedDependencyImages is null && !string.IsNullOrWhiteSpace(dependencyAssemblyPath))
        {
            var componentPath = Path.GetFullPath(dependencyAssemblyPath);
            resolver = File.Exists(componentPath) ? new AssemblyDependencyResolver(componentPath) : null;
        }

        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assemblyName in PlatformAssemblyNames)
        {
            shared.Add(assemblyName);
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

    /// <summary>True when private dependencies resolve only from the verified images supplied at construction and the
    /// plugin directory is never consulted.</summary>
    public bool UsesVerifiedDependencyImages => Volatile.Read(ref verifiedDependencyImages) is not null;

    /// <summary>The assembly loaded into this context, or <c>null</c> before loading and after unload.</summary>
    public Assembly? PluginAssembly => Volatile.Read(ref pluginAssembly);

    /// <summary>Assemblies the host always resolves itself instead of loading a plugin-local copy: the shared
    /// framework (TPA), <c>Forge.Plugin.Sdk</c> and the explicitly configured names. An assembly the host happened
    /// to load into the default context is not shared unless it is one of those.</summary>
    public IReadOnlyCollection<string> SharedAssemblyNames => sharedAssemblyNames;

    public bool IsUnloading => Volatile.Read(ref unloadStarted) != 0;

    /// <summary>Loads a plugin image from bytes. An embedded PDB travels with the image; pass
    /// <paramref name="symbols"/> only for a separately emitted portable PDB.</summary>
    public Assembly LoadPluginAssembly(byte[] image, byte[]? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ThrowIfUnloading();
        if (PluginAssembly is not null)
        {
            throw new InvalidOperationException("A plugin assembly is already loaded in this context.");
        }

        using var peStream = new MemoryStream(image, writable: false);
        var assembly = symbols is null
            ? LoadFromStream(peStream)
            : LoadFromStream(peStream, new MemoryStream(symbols, writable: false));
        return Publish(assembly);
    }

    public Assembly LoadPluginAssembly(ReadOnlyMemory<byte> image, ReadOnlyMemory<byte>? symbols = null) =>
        LoadPluginAssembly(image.ToArray(), symbols?.ToArray());

    public Assembly LoadPluginAssemblyFromPath(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ThrowIfUnloading();
        if (PluginAssembly is not null)
        {
            throw new InvalidOperationException("A plugin assembly is already loaded in this context.");
        }

        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {full}", full);
        }

        Track(full);
        return Publish(LoadFromAssemblyPath(full));
    }

    /// <summary>Instantiates a plugin entry type, rejecting anything that is not a concrete
    /// <see cref="IPlugin"/> with a public parameterless constructor.</summary>
    public IPlugin CreatePlugin(Type entryType)
    {
        ArgumentNullException.ThrowIfNull(entryType);
        if (!typeof(IPlugin).IsAssignableFrom(entryType))
        {
            throw new InvalidOperationException(
                $"Entry type '{Describe(entryType)}' does not implement {typeof(IPlugin).FullName}.");
        }

        if (entryType.IsAbstract || entryType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Entry type '{Describe(entryType)}' cannot be instantiated: it is abstract.");
        }

        var constructor = entryType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                $"Entry type '{Describe(entryType)}' has no public parameterless constructor.");
        }

        return (IPlugin)constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: null, culture: null);
    }

    /// <summary>Instantiates a plugin entry type by full name from the loaded assembly.</summary>
    public IPlugin CreatePlugin(string entryTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryTypeName);
        var assembly = PluginAssembly ??
            throw new InvalidOperationException("No plugin assembly is loaded in this context.");
        var type = assembly.GetType(entryTypeName, throwOnError: false) ??
            throw new InvalidOperationException(
                $"Entry type '{entryTypeName}' was not found in assembly '{assembly.GetName().Name}'.");
        return CreatePlugin(type);
    }

    /// <summary>Starts unloading and reports whether the context was collected and its dependency files released
    /// within <see cref="DefaultUnloadTimeout"/>. A reference held by the caller keeps the context alive, so this reports
    /// false when invoked through a live local; an owner that drops its reference should call
    /// <see cref="BeginUnload"/> and then <see cref="WaitForCollectionAsync"/>.</summary>
    public new Task<bool> Unload() => UnloadAsync(DefaultUnloadTimeout, CancellationToken.None);

    /// <summary>Starts unloading, waits up to <paramref name="timeout"/> for collection and reports the outcome.
    /// Repeat calls return the same task.</summary>
    public Task<bool> UnloadAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var existing = Volatile.Read(ref unloadTask);
        if (existing is not null)
        {
            return existing;
        }

        ct.ThrowIfCancellationRequested();
        var weak = new WeakReference(this);
        BeginUnload();
        var task = WaitForCollectionAsync(weak, timeout ?? DefaultUnloadTimeout);
        return Interlocked.CompareExchange(ref unloadTask, task, null) ?? task;
    }

    /// <summary>Drops the loaded assembly and marks the context for collection without waiting. Safe to call twice.</summary>
    public void BeginUnload() => DropAndUnload();

    /// <summary>Waits up to <paramref name="timeout"/> for a context to be collected, forcing GC between attempts,
    /// and reports <c>true</c> only once the dependency files of every collected context are actually released.
    /// Only the weak reference is held, so the caller must have dropped every strong reference first.</summary>
    public static Task<bool> WaitForCollectionAsync(
        WeakReference context,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        return WaitForCollectionCoreAsync(context, timeout ?? DefaultUnloadTimeout);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } simpleName)
        {
            return null;
        }

        if (sharedAssemblyNames.Contains(simpleName))
        {
            return null;
        }

        if (Volatile.Read(ref verifiedDependencyImages) is not null)
        {
            return LoadVerifiedDependency(simpleName);
        }

        var path = assemblyProbes.GetOrAdd(simpleName, static (name, context) => context.ResolveAssembly(name), this);
        if (path is null)
        {
            return null;
        }

        Track(path);
        return LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName) || Volatile.Read(ref verifiedDependencyImages) is not null)
        {
            return IntPtr.Zero;
        }

        return unmanagedProbes.GetOrAdd(unmanagedDllName, static (name, context) => context.ResolveUnmanaged(name), this);
    }

    private Assembly Publish(Assembly assembly)
    {
        Volatile.Write(ref pluginAssembly, assembly);
        return assembly;
    }

    private void Track(string path)
    {
        ReleaseUnloadedFiles();
        TrackedFiles.TryAdd(new TrackedFile(new WeakReference(this), path), 0);
    }

    /// <summary>Separate frame so the unload path keeps no strong reference to this context while waiting.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DropAndUnload()
    {
        if (Interlocked.Exchange(ref unloadStarted, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref pluginAssembly, null);
        Volatile.Write(ref verifiedDependencyImages, null);
        assemblyProbes.Clear();
        unmanagedProbes.Clear();
        verifiedDependencyAssemblies.Clear();
        base.Unload();
    }

    private Assembly? LoadVerifiedDependency(string simpleName)
    {
        if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (verifiedDependencyGate)
        {
            if (verifiedDependencyAssemblies.TryGetValue(simpleName, out var cached))
            {
                return cached;
            }

            var images = Volatile.Read(ref verifiedDependencyImages) ??
                throw new InvalidOperationException("The plugin load context is unloading; load a new context instead.");
            if (!images.TryGetValue(simpleName, out var image))
            {
                throw new FileNotFoundException(
                    $"Plugin dependency '{simpleName}' is not among the verified images supplied for this context, so it is not loaded from '{directory}'.",
                    simpleName);
            }

            using var stream = new MemoryStream(image, writable: false);
            var assembly = LoadFromStream(stream);
            verifiedDependencyAssemblies[simpleName] = assembly;
            return assembly;
        }
    }

    private static FrozenDictionary<string, byte[]>? IndexVerifiedImages(
        IReadOnlyDictionary<string, byte[]>? images)
    {
        if (images is null)
        {
            return null;
        }

        var index = new Dictionary<string, byte[]>(images.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, image) in images)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "A verified dependency image was supplied without an assembly simple name.",
                    nameof(images));
            }

            ArgumentNullException.ThrowIfNull(image, name);
            if (!ManagedImage.IsManaged(image))
            {
                throw new BadImageFormatException(
                    $"The verified dependency image '{name}' is not a managed assembly, so it cannot be loaded from memory.",
                    name);
            }

            if (!index.TryAdd(name.Trim(), image))
            {
                throw new ArgumentException(
                    $"The verified dependency image '{name}' was supplied more than once.",
                    nameof(images));
            }
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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

    private void ThrowIfUnloading()
    {
        if (Volatile.Read(ref unloadStarted) != 0)
        {
            throw new InvalidOperationException("The plugin load context is unloading; load a new context instead.");
        }
    }

    private static async Task<bool> WaitForCollectionCoreAsync(WeakReference weak, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        var attempt = 0;
        while (weak.IsAlive)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            if (!weak.IsAlive)
            {
                break;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            attempt++;
            await Task.Delay(Math.Min(attempt * 5, 50), CancellationToken.None).ConfigureAwait(false);
        }

        return await WaitForFileReleaseAsync(deadline).ConfigureAwait(false);
    }

    /// <summary>Polls until every file tracked by a collected context is released, and reports <c>false</c> when the
    /// deadline passes without that confirmation.</summary>
    private static async Task<bool> WaitForFileReleaseAsync(long deadline)
    {
        while (true)
        {
            if (ReleaseUnloadedFiles())
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            await Task.Delay(ReleasePollMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool ReleaseUnloadedFiles()
    {
        var released = true;
        foreach (var tracked in TrackedFiles.Keys)
        {
            if (tracked.Owner.IsAlive)
            {
                continue;
            }

            if (IsFileReleased(tracked.Path))
            {
                TrackedFiles.TryRemove(tracked, out _);
            }
            else
            {
                released = false;
            }
        }

        return released;
    }

    private static bool IsFileReleased(string path) =>
        IsOpenAvailable(path, FileAccess.Read, FileShare.None) && IsWriteAvailable(path);

    private static bool IsOpenAvailable(string path, FileAccess access, FileShare share)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, access, share, bufferSize: 1, FileOptions.None))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWriteAvailable(string path)
    {
        try
        {
            using (new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class TrackedFile(WeakReference owner, string path)
    {
        public WeakReference Owner { get; } = owner;

        public string Path { get; } = path;
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;

    private static string ContextName(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var full = Path.GetFullPath(pluginDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"forge-plugin:{Path.GetFileName(full)}";
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
