using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>A plugin failed to load: the assembly is missing, unverified or malformed, no entry type was found,
/// registration threw, a contribution cannot be honoured, or the declarations are invalid.</summary>
internal sealed class PluginLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What loading a plugin produced: the declaration snapshot the host publishes and the entry's invocation
/// targets, held by <see cref="LoadedPlugin"/>.</summary>
internal sealed record PluginLoadout(PluginContributionSet Contributions, ToolDeclaration[] Tools);

/// <summary>Owns the one plugin this worker runs. Loading a second plugin unloads the first; the load
/// context is reachable only from <see cref="LoadedPlugin"/>, so dropping that reference releases it.</summary>
internal sealed class PluginHost
{
    private static readonly JsonElement EmptyObject = ParseEmptyObject();

    private readonly SemaphoreSlim mutation = new(1, 1);
    private LoadedPlugin? loaded;

    /// <summary>How long an unload waits for the collectible load context to be collected.</summary>
    public TimeSpan UnloadTimeout { get; init; } = PluginLoadContext.DefaultUnloadTimeout;

    private static JsonElement ParseEmptyObject()
    {
        using var document = JsonDocument.Parse("{}"u8.ToArray());
        return document.RootElement.Clone();
    }

    public async Task<PluginLoadout> LoadAsync(
        string pluginId,
        string version,
        string assemblyPath,
        string? assemblySha256,
        IReadOnlyDictionary<string, string>? files,
        IReadOnlyList<Permission>? manifestPermissions,
        CancellationToken ct)
    {
        await mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var created = await CreateAsync(
                pluginId,
                version,
                assemblyPath,
                assemblySha256,
                files,
                manifestPermissions ?? [],
                ct).ConfigureAwait(false);
            await FinishUnloadAsync(PublishAndDetach(created)).ConfigureAwait(false);
            return new PluginLoadout(created.Contributions, [.. created.Contributions.Tools]);
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
        ReadOnlyMemory<byte> argumentsJson,
        HostCallFacade host,
        ExtensionCallContext? context,
        int timeoutMilliseconds,
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

        // The one place the raw argument bytes become a JsonElement; the document lives until the tool returns.
        JsonDocument? arguments = null;
        try
        {
            arguments = argumentsJson.IsEmpty ? null : JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            return Error($"the arguments of tool '{toolName}' are not valid JSON: {ex.Message}");
        }

        using var argumentsLifetime = arguments;
        var invocation = new ToolInvocation(
            host.SessionId,
            host.WorkingDirectory,
            arguments?.RootElement ?? EmptyObject,
            host,
            context);
        try
        {
            var result = await InvokeWithinBudgetAsync(
                timeoutMilliseconds,
                ct,
                token => tool.InvokeAsync(invocation, token),
                $"tool '{toolName}'").ConfigureAwait(false);
            return result ?? Error($"tool '{toolName}' returned no result");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BudgetExceededException exception)
        {
            return Error(exception.Message);
        }
        catch (Exception ex)
        {
            return Error(Describe(ex));
        }
    }

    /// <summary>Runs one contributed command. The command body only ever sees the bound context and a facade that
    /// carries its run id; anything it calls is routed back through the host.</summary>
    public async Task<CommandOutput> ExecuteCommandAsync(
        CommandExecutionRequest request,
        HostCallFacade host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = Volatile.Read(ref loaded);
        if (current is null)
        {
            return new CommandOutput($"no plugin is loaded in this worker, so command '{request.Command}' cannot run", IsError: true);
        }

        if (!string.Equals(request.PluginId, current.PluginId, StringComparison.Ordinal))
        {
            return new CommandOutput(
                $"plugin '{request.PluginId}' is not loaded in this worker (loaded: '{current.PluginId}')",
                IsError: true);
        }

        if (request.Context is not { } context)
        {
            return new CommandOutput(
                $"command '{request.Command}' was not run: the worker received no bound call context",
                IsError: true);
        }

        if (!current.Commands.TryGetValue(request.Command, out var command))
        {
            return new CommandOutput($"the plugin does not declare an executable command named '{request.Command}'", IsError: true);
        }

        var invocation = new CommandInvocation(request.Arguments ?? string.Empty, context, host);
        try
        {
            var output = await InvokeWithinBudgetAsync(
                request.TimeoutMilliseconds,
                ct,
                token => command.ExecuteAsync(invocation, token),
                $"command '{request.Command}'").ConfigureAwait(false);
            return output ?? new CommandOutput($"command '{request.Command}' returned no output", IsError: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BudgetExceededException exception)
        {
            return new CommandOutput(exception.Message, IsError: true);
        }
        catch (Exception ex)
        {
            return new CommandOutput(Describe(ex), IsError: true);
        }
    }

    /// <summary>Evaluates one prompt contributor asynchronously; null text means it contributed nothing.</summary>
    public async Task<PromptOutput> EvaluatePromptAsync(
        PromptExecutionRequest request,
        HostCallFacade host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = Volatile.Read(ref loaded);
        if (current is null)
        {
            throw new PluginExecutionException(
                $"no plugin is loaded in this worker, so prompt contributor '{request.ContributorId}' cannot run");
        }

        if (!string.Equals(request.PluginId, current.PluginId, StringComparison.Ordinal))
        {
            throw new PluginExecutionException(
                $"plugin '{request.PluginId}' is not loaded in this worker (loaded: '{current.PluginId}')");
        }

        if (request.Context is not { } context)
        {
            throw new PluginExecutionException(
                $"prompt contributor '{request.ContributorId}' was not run: the worker received no bound call context");
        }

        if (!current.Prompts.TryGetValue(request.ContributorId, out var contributor))
        {
            throw new PluginExecutionException(
                $"the plugin does not declare a prompt contributor with id '{request.ContributorId}'");
        }

        var invocation = new PromptInvocation(context, host);
        try
        {
            var text = await InvokeWithinBudgetAsync(
                request.TimeoutMilliseconds,
                ct,
                token => contributor.GetTextAsync(invocation, token),
                $"prompt contributor '{request.ContributorId}'").ConfigureAwait(false);
            return new PromptOutput(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static async Task<T> InvokeWithinBudgetAsync<T>(
        int timeoutMilliseconds,
        CancellationToken ct,
        Func<CancellationToken, Task<T>> body,
        string description)
    {
        if (timeoutMilliseconds <= 0)
        {
            return await body(ct).ConfigureAwait(false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMilliseconds);
        try
        {
            return await body(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new BudgetExceededException(
                $"{description} exceeded its {timeoutMilliseconds} ms budget and was cancelled; the host kills the worker "
                + "process tree if it does not stop.");
        }
    }

    private static async Task<LoadedPlugin> CreateAsync(
        string pluginId,
        string version,
        string assemblyPath,
        string? assemblySha256,
        IReadOnlyDictionary<string, string>? files,
        IReadOnlyList<Permission> manifestPermissions,
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
            RequireDeclaredPermissions(pluginId, recorder.Permissions, manifestPermissions);
            return LoadedPlugin.Create(pluginId, version, fullPath, context, plugin, recorder);
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

    /// <summary>
    /// Runtime permission requirements must be a subset of what the manifest declared before approval. A plugin that
    /// asks at registration time for a permission the user never saw fails the load; nothing is granted implicitly and
    /// nothing is silently dropped.
    /// </summary>
    private static void RequireDeclaredPermissions(
        string pluginId,
        IReadOnlyList<Permission> required,
        IReadOnlyList<Permission> declared)
    {
        foreach (var permission in required)
        {
            var covered = false;
            foreach (var candidate in declared)
            {
                if (candidate.Kind == permission.Kind &&
                    string.Equals(candidate.Scope, permission.Scope, StringComparison.Ordinal))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                throw new PluginLoadException(
                    $"Plugin '{pluginId}' requires {Describe(permission)} at registration, but its manifest does not declare it. "
                    + "Add it to the manifest permissions and get the plugin approved again; the load is refused.");
            }
        }
    }

    private static string Describe(Permission permission) => permission.Scope is { Length: > 0 } scope
        ? $"permission '{permission.Kind}' for '{scope}'"
        : $"permission '{permission.Kind}'";

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

/// <summary>A call ended because its own budget ran out; the host separately enforces the hard deadline.</summary>
internal sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>A prompt contributor could not be evaluated (not loaded, unknown id, or not bound to a context).</summary>
internal sealed class PluginExecutionException(string message) : Exception(message);
