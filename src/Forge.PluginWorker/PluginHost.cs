using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>A plugin failed to load: the assembly is missing, unverified or malformed, no entry type was found,
/// registration threw, a contribution cannot be honoured, or the declarations are invalid.</summary>
internal sealed class PluginLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Owns the one plugin this worker runs. Loading a second plugin unloads the first; the load
/// context is reachable only from <see cref="LoadedPlugin"/>, so dropping that reference releases it.</summary>
internal sealed class PluginHost
{
    private readonly SemaphoreSlim mutation = new(1, 1);
    private LoadedPlugin? loaded;

    /// <summary>How long an unload waits for the collectible load context to be collected.</summary>
    public TimeSpan UnloadTimeout { get; init; } = PluginLoadContext.DefaultUnloadTimeout;

    public async Task<ToolDeclaration[]> LoadAsync(
        string pluginId,
        string version,
        string assemblyPath,
        string? assemblySha256,
        IReadOnlyDictionary<string, string>? files,
        CancellationToken ct)
    {
        await mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var created = await CreateAsync(pluginId, version, assemblyPath, assemblySha256, files, ct).ConfigureAwait(false);
            await FinishUnloadAsync(PublishAndDetach(created)).ConfigureAwait(false);
            return created.Declarations;
        }
        finally
        {
            mutation.Release();
        }
    }

    /// <summary>Unloads the plugin when it matches <paramref name="pluginId"/>; an empty id unloads
    /// whatever is loaded and an id that is not loaded is a no-op.</summary>
    public async Task UnloadAsync(string? pluginId, CancellationToken ct)
    {
        Task<bool>? pending;
        await mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            pending = UnloadCurrent(pluginId);
        }
        finally
        {
            mutation.Release();
        }

        await FinishUnloadAsync(pending).ConfigureAwait(false);
    }

    public async Task<ToolResult> ExecuteAsync(
        string? pluginId,
        string toolName,
        JsonElement arguments,
        HostCallFacade host,
        CancellationToken ct)
    {
        var current = Volatile.Read(ref loaded);
        if (current is null)
        {
            return Error($"no plugin is loaded in this worker, so '{toolName}' cannot run");
        }

        if (!string.IsNullOrEmpty(pluginId) && !string.Equals(pluginId, current.PluginId, StringComparison.Ordinal))
        {
            return Error($"plugin '{pluginId}' is not loaded in this worker (loaded: '{current.PluginId}')");
        }

        if (!current.Tools.TryGetValue(toolName, out var tool))
        {
            return Error($"the plugin does not declare a tool named '{toolName}'");
        }

        var invocation = new ToolInvocation(host.SessionId, host.WorkingDirectory, arguments, host);
        try
        {
            var result = await tool.InvokeAsync(invocation, ct).ConfigureAwait(false);
            return result ?? Error($"tool '{toolName}' returned no result");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(Describe(ex));
        }
    }

    private static async Task<LoadedPlugin> CreateAsync(
        string pluginId,
        string version,
        string assemblyPath,
        string? assemblySha256,
        IReadOnlyDictionary<string, string>? files,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new PluginLoadException($"Plugin assembly not found: {fullPath}");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new PluginLoadException($"Cannot determine the plugin directory of '{fullPath}'.");
        }

        // Verify before a load context exists, so no plugin code can run against unverified bytes.
        var verified = await PluginIntegrity
            .VerifyAsync(fullPath, directory, assemblySha256, files, ct)
            .ConfigureAwait(false);

        var context = PluginLoadContext.FromVerifiedImages(
            directory,
            verified.DependencyImages,
            $"forge-worker-plugin:{pluginId}");
        try
        {
            var assembly = context.LoadPluginAssembly(verified.EntryImage);
            var plugin = CreateEntry(context, assembly, pluginId);
            var recorder = new RegistrationRecorder(pluginId);
            plugin.Register(recorder);
            return LoadedPlugin.Create(pluginId, version, fullPath, context, plugin, recorder.Tools);
        }
        catch (PluginLoadException)
        {
            context.BeginUnload();
            throw;
        }
        catch (OperationCanceledException)
        {
            context.BeginUnload();
            throw;
        }
        catch (Exception ex)
        {
            context.BeginUnload();
            throw new PluginLoadException($"Loading plugin assembly '{fullPath}' failed: {Describe(ex)}", ex);
        }
    }

    private static IPlugin CreateEntry(PluginLoadContext context, System.Reflection.Assembly assembly, string pluginId)
    {
        var candidates = FindEntryTypes(assembly);
        if (candidates.Count == 1)
        {
            return context.CreatePlugin(candidates[0]);
        }

        var matches = new List<IPlugin>(candidates.Count);
        var described = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            IPlugin instance;
            try
            {
                instance = context.CreatePlugin(candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                described.Add($"{candidate.FullName} (construction failed: {Describe(ex)})");
                continue;
            }

            var id = instance.Info?.Id ?? "";
            described.Add($"{candidate.FullName} (Info.Id '{id}')");
            if (string.Equals(id, pluginId, StringComparison.Ordinal))
            {
                matches.Add(instance);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new PluginLoadException(matches.Count == 0
            ? $"Assembly '{assembly.GetName().Name}' declares no {nameof(IPlugin)} with Info.Id '{pluginId}'. Candidates: {string.Join(", ", described)}."
            : $"Assembly '{assembly.GetName().Name}' declares more than one {nameof(IPlugin)} with Info.Id '{pluginId}'.");
    }

    private static List<Type> FindEntryTypes(System.Reflection.Assembly assembly)
    {
        Type[] types;
        var loadError = "";
        try
        {
            types = assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
            loadError = string.Join("; ", ex.LoaderExceptions.OfType<Exception>().Select(static error => error.Message));
        }

        var candidates = new List<Type>(types.Length);
        foreach (var type in types)
        {
            if (type is { IsAbstract: false, IsInterface: false, IsPublic: true } &&
                typeof(IPlugin).IsAssignableFrom(type))
            {
                candidates.Add(type);
            }
        }

        if (candidates.Count == 0)
        {
            throw new PluginLoadException(loadError.Length == 0
                ? $"Assembly '{assembly.GetName().Name}' declares no concrete public {nameof(IPlugin)} entry type."
                : $"Assembly '{assembly.GetName().Name}' could not be inspected for an {nameof(IPlugin)} entry type: {loadError}");
        }

        candidates.Sort(static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));
        return candidates;
    }

    /// <summary>Swaps in the new plugin and starts unloading the previous one. Kept out of line so no
    /// frame that awaits collection holds a reference to the old context.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<bool>? PublishAndDetach(LoadedPlugin created)
    {
        var previous = Volatile.Read(ref loaded);
        Volatile.Write(ref loaded, created);
        return previous is null ? null : Detach(previous);
    }

    /// <summary>Removes the matching plugin from the host and starts its unload without waiting.
    /// Returns the pending collection check, or <c>null</c> when nothing matched.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<bool>? UnloadCurrent(string? pluginId)
    {
        var current = Volatile.Read(ref loaded);
        if (current is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(pluginId) && !string.Equals(pluginId, current.PluginId, StringComparison.Ordinal))
        {
            return null;
        }

        Volatile.Write(ref loaded, null);
        return Detach(current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<bool>? Detach(LoadedPlugin current)
    {
        var context = current.Context;
        current.Detach();
        var weak = new WeakReference(context);
        context.BeginUnload();
        return PluginLoadContext.WaitForCollectionAsync(weak, UnloadTimeout);
    }

    private static async Task FinishUnloadAsync(Task<bool>? pending)
    {
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static ToolResult Error(string message) => new(message, IsError: true);

    private static string Describe(Exception exception)
    {
        var effective = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;
        return effective.Message.Length == 0
            ? effective.GetType().Name
            : $"{effective.GetType().Name}: {effective.Message}";
    }
}
